using System.Globalization;
using HarmonyLib;
using Il2CppAssets.Scripts.PeroTools.Managers;
using Il2CppFormulaBase;
using Il2CppGameLogic;
using UnityEngine;
using NativeStopwatch = Il2CppSystem.Diagnostics.Stopwatch;

namespace MuseDashPracticePrototype;

public sealed partial class PracticeMod
{
    private readonly PlaybackClock _playbackClock = new();
    private string _speedText = "1.00";
    private double _selectedPlaybackRate = 1;
    private NativeStopwatch? _speedWatch;
    private long _speedWatchPointer;
    private bool _speedClockActive;
    private string? _speedFault;
    private AudioSource? _speedAudio;
    private int _speedClip;
    private float _originalPitch = 1;
    private float _originalTimeScale = 1;
    private float _originalFixedDelta;
    private bool _speedCalibrationPending;

    private void InstallSpeedHooks()
    {
        // Inspected native ElapsedMilliseconds AND Elapsed call ElapsedTicks;
        // all three StageBattle time getters use ElapsedMilliseconds. Patch one
        // shared native getter, filtered to this one song-watch instance.
        Patch(typeof(NativeStopwatch), "get_ElapsedTicks", postfix: nameof(ScaleSongStopwatchTicks));
        Patch(typeof(Il2CppMDEx.Timer.GlobalTimer), "get_deltaTime", postfix: nameof(ScaleAnimationTimerDelta));
        Patch(typeof(StageBattleComponent), "get_getMusicOffset", postfix: nameof(ScaleMusicCalibration));
        Patch(typeof(StageBattleComponent), "Pause", postfix: nameof(RefreshSpeedAfterPause));
        Patch(typeof(StageBattleComponent), "Resume", postfix: nameof(RefreshSpeedAfterPause));
    }

    private static void ScaleAnimationTimerDelta(ref float __result)
    {
        var mod = Current;
        if (mod is null || !mod._enabled || !mod._inBattle || !mod._speedClockActive) return;
        var stage = StageBattleComponent.instance;
        if (stage is null || !stage.isInGame || stage.isPause) return;
        __result = mod._playbackClock.ScaleAnimationDelta(__result);
    }

    private static void ScaleSongStopwatchTicks(NativeStopwatch __instance, ref long __result)
    {
        var mod = Current;
        if (mod is null || !mod._speedClockActive || __instance.Pointer.ToInt64() != mod._speedWatchPointer) return;
        try { __result = mod._playbackClock.Read(__result); }
        catch (Exception error)
        {
            // Never unwind a managed exception through the game's native clock.
            // The main update stops practice and restores settings on this fault.
            mod._speedClockActive = false;
            mod._speedFault = error.Message;
        }
    }

    private static void ScaleMusicCalibration(StageBattleComponent __instance, ref int __result)
    {
        var mod = Current;
        if (mod is null || !mod._enabled || !mod._inBattle || !mod._speedClockActive) return;
        if (__instance.Pointer != StageBattleComponent.instance?.Pointer) return;
        try
        {
            var clip = mod._speedAudio?.clip;
            if (clip is not null)
                __result = mod._playbackClock.AdjustMusicOffsetSamples(__result, __instance.offset, clip.frequency);
        }
        catch (Exception error) { mod._speedFault = error.Message; }
    }

    private void ApplySpeedCalibration(StageBattleComponent stage, AudioSource audio)
    {
        if (!_speedCalibrationPending || (!audio.isPlaying && !stage.isPause)) return;
        // Same absolute native sync path used by startup and pause/resume. Its
        // postfix changes only calibration, retaining the chart's own delay.
        var sample = stage.getMusicOffset;
        if (sample < 0 || sample >= audio.clip.samples) return; // Native pre-audio/ending range.
        audio.timeSamples = sample;
        _audioOffset = sample / (float)audio.clip.frequency - stage.timeFromMusicStart;
        _offsetCaptured = true;
        _speedCalibrationPending = false;
        Log("speed_calibration", new { rate = _playbackClock.Rate, calibration = stage.offset,
            effectiveCalibration = stage.offset * _playbackClock.Rate, sourceAudioOffset = _audioOffset });
    }

    private static long RawSongTicks(NativeStopwatch watch) => checked(watch.elapsed
        + (watch.is_running ? NativeStopwatch.GetTimestamp() - watch.started : 0));

    private static void RefreshSpeedAfterPause(StageBattleComponent __instance)
    {
        var mod = Current;
        if (mod?._speedWatch is null) return;
        try { mod.RefreshSpeedPresentation(__instance); }
        catch (Exception error) { mod._speedFault = error.Message; }
    }

    private void EnsurePlaybackSpeed(StageBattleComponent stage, AudioSource audio)
    {
        if (_speedFault is { } fault) throw new InvalidOperationException("Playback speed stopped: " + fault);
        if (!_enabled || !_inBattle || (!stage.isInGame && !stage.isPause))
        { RestorePlaybackSpeed(); return; }
        var watch = GameGlobal.stopwatch ?? throw new InvalidOperationException("No chart stopwatch for playback speed");
        if (_speedWatch is not null && (_speedWatchPointer != watch.Pointer.ToInt64()
            || _speedAudio?.Pointer != audio.Pointer || _speedClip != audio.clip.GetInstanceID()))
            RestorePlaybackSpeed(false);
        if (_speedWatch is null)
        {
            if (_selectedPlaybackRate == 1) return; // Original-speed practice stays untouched.
            if (Math.Abs(audio.pitch - 1) > .001f || Math.Abs(Time.timeScale - 1) > .001f)
                throw new InvalidOperationException("Another playback/time-scale change is active; restore it before practice speed");
            _originalPitch = audio.pitch;
            _originalTimeScale = Time.timeScale;
            _originalFixedDelta = Time.fixedDeltaTime;
            var raw = RawSongTicks(watch);
            _playbackClock.Seek(raw, raw, _selectedPlaybackRate);
            _speedWatch = watch;
            _speedWatchPointer = watch.Pointer.ToInt64();
            _speedAudio = audio;
            _speedClip = audio.clip.GetInstanceID();
            _speedClockActive = true;
            _speedCalibrationPending = true;
            _offsetCaptured = false;
            Log("speed_attached", new { rate = _selectedPlaybackRate, watch = _speedWatchPointer, chartTime = stage.timeFromMusicStart });
        }
        else if (_playbackClock.Rate != _selectedPlaybackRate)
        {
            _playbackClock.ChangeRate(RawSongTicks(watch), _selectedPlaybackRate);
            _speedCalibrationPending = true;
            _offsetCaptured = false;
        }
        RefreshSpeedPresentation(stage);
        ApplySpeedCalibration(stage, audio);
    }

    private void RefreshSpeedPresentation(StageBattleComponent stage)
    {
        if (_speedWatch is null) return;
        if (_speedAudio) _speedAudio!.pitch = _originalPitch * (float)_playbackClock.Rate;
        // Native Pause already stops the watch/audio/timers/animations. Restore
        // Unity's original rate for pause menus; Resume reapplies the song rate.
        var multiplier = stage.isPause ? 1f : (float)_playbackClock.Rate;
        Time.timeScale = _originalTimeScale * multiplier;
        Time.fixedDeltaTime = _originalFixedDelta * multiplier;
    }

    private void RebasePlaybackClock(NativeStopwatch watch, long targetTicks)
    {
        if (_speedWatch is null) return;
        if (watch.Pointer.ToInt64() != _speedWatchPointer) throw new InvalidOperationException("Chart stopwatch changed during seek");
        _playbackClock.Seek(targetTicks, targetTicks, _selectedPlaybackRate);
        _speedClockActive = true;
        // Reset/reinitialization may restore presentation defaults. Reassert the
        // owned rate before the caller unpauses audio and restarts the watch.
        if (StageBattleComponent.instance is { } stage) RefreshSpeedPresentation(stage);
    }

    private void RestorePlaybackSpeed(bool preservePosition = true)
    {
        var watch = _speedWatch;
        if (watch is null) { _speedFault = null; return; }
        try
        {
            // Preserve the current SONG position when returning to 1x. Native
            // accumulated ticks remain unscaled during all normal speed play.
            if (preservePosition && _speedClockActive)
            {
                var running = watch.is_running;
                watch.Stop();
                var songTicks = _playbackClock.Read(watch.elapsed);
                _speedClockActive = false;
                watch.elapsed = songTicks;
                if (running) watch.Start();
            }
        }
        catch (Exception error) { Log("speed_clock_restore_error", new { error = error.Message }); }
        finally
        {
            _speedClockActive = false;
            _speedCalibrationPending = false;
            _offsetCaptured = false;
            _speedWatchPointer = 0;
            _speedWatch = null;
            _speedFault = null;
            try
            {
                if (_speedAudio)
                {
                    _speedAudio!.pitch = _originalPitch;
                    if (preservePosition && StageBattleComponent.instance is { } stage
                        && (stage.isInGame || stage.isPause) && _speedAudio.clip?.GetInstanceID() == _speedClip)
                    {
                        // The hook is now disabled and the song watch rebased;
                        // restore the normal calibration without editing settings.
                        var sample = stage.getMusicOffset;
                        if (sample >= 0 && sample < _speedAudio.clip.samples) _speedAudio.timeSamples = sample;
                    }
                }
            }
            catch (Exception error) { Log("speed_audio_restore_error", new { error = error.Message }); }
            finally
            {
                _speedAudio = null;
                Time.timeScale = _originalTimeScale;
                Time.fixedDeltaTime = _originalFixedDelta;
            }
        }
        Log("speed_restored", new { preservePosition });
    }

    private bool PlaybackRateMatches(AudioSource audio) =>
        Math.Abs(audio.pitch - (_speedWatch is null ? 1f : _originalPitch * (float)_playbackClock.Rate)) < .001f;

    private void ApplyPlaybackSpeed()
    {
        if (!PlaybackClock.TryParseRate(_speedText, out var rate))
        { _message = "Speed must be a number from 0.50 to 1.25 (use a decimal point)."; return; }
        var stage = _inBattle ? StageBattleComponent.instance : null;
        if (stage?.isInGame == true && !stage.isPause)
        { _message = "Use Pause for speed first. Apply while paused, then F9 or Resume marking."; return; }
        _selectedPlaybackRate = rate;
        _speedText = rate.ToString("0.00", CultureInfo.InvariantCulture);
        try
        {
            var audio = AudioManager.instance?.bgm;
            if (_enabled && stage is not null && audio?.clip is not null) EnsurePlaybackSpeed(stage, audio);
            _message = $"Speed {rate:0.00}x selected (pitch follows speed). F9 starts/restarts; Resume marking continues.";
            Log("speed_selected", new { rate });
        }
        catch (Exception error)
        {
            RestorePlaybackSpeed();
            _selectedPlaybackRate = 1;
            _speedText = "1.00";
            _message = "Speed change failed; restored original speed. See diagnostics.";
            Log("speed_error", new { error = error.ToString() });
        }
    }

    private void PauseForSpeed()
    {
        if (!_enabled || !_inBattle) return;
        PauseForParameters("Playback speed editing");
        _message = "Paused for speed. Set 0.50-1.25 and Apply. F9 restarts the range; Resume marking continues.";
    }

    private void DrawPlaybackSpeedPanel(float width)
    {
        var originalEnabled = GUI.enabled;
        try
        {
            var playing = _inBattle && StageBattleComponent.instance is { isInGame: true, isPause: false };
            GUI.Label(new Rect(24, 282, 95, 26), "Speed (x)");
            GUI.enabled = originalEnabled && !playing;
            _speedText = GUI.TextField(new Rect(120, 282, 95, 26), _speedText, 8);
            if (GUI.Button(new Rect(225, 282, 145, 26), "Apply speed")) ApplyPlaybackSpeed();
            GUI.enabled = originalEnabled && _enabled && _inBattle;
            if (GUI.Button(new Rect(380, 282, 173, 26), "Pause for speed")) PauseForSpeed();
            GUI.enabled = originalEnabled;
            GUI.Label(new Rect(24, 314, width - 40, 30), $"Selected {_selectedPlaybackRate:0.00}x | pitch changes | A/B and lead-in use song seconds");
        }
        finally { GUI.enabled = originalEnabled; }
    }

    public override void OnDeinitializeMelon()
    {
        _leadInWindow.Cancel();
        RestorePlaybackSpeed();
    }
}
