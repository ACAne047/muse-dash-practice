using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using Il2CppAssets.Scripts.Database;
using Il2CppAssets.Scripts.PeroTools.Managers;
using Il2CppFormulaBase;
using Il2CppGameLogic;
using Il2CppAssets.Scripts.GameCore.HostComponent;
using Il2CppDYUnityLib;
using Il2CppAssets.Scripts.GameCore.Managers;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(MuseDashPracticePrototype.PracticeMod), "Muse Dash Practice Prototype", "0.3.2", "Local prototype")]
[assembly: MelonGame("PeroPeroGames", "MuseDash")]

namespace MuseDashPracticePrototype;

public sealed class PracticeMod : MelonMod
{
    internal static PracticeMod? Current;
    internal static bool Tainted;
    private bool _enabled;
    private bool _panel = true;
    private bool _loop = true;
    private bool _fastLoop = true;
    private bool _offsetCaptured;
    private bool _ready;
    private bool _seekPending;
    private bool _restartPending;
    private bool _exitPending;
    private bool _inBattle;
    private bool _chartOpen;
    private bool _firstProbe;
    private float _lastProbe;
    private float _nextCommand;
    private float _startedAt;
    private int _round;
    private string _aText = "20";
    private string _bText = "30";
    private float _a = 20;
    private float _b = 30;
    private string _message = "F6 panel | Set A/B, then Arm practice";
    private string _root = "";
    private GUIStyle? _label;
    private float _audioOffset;
    private float _position;
    private NoteResetSnapshot? _noteState;
    private readonly HashSet<string> _logged = new();
    private const string SupportedGame = "6.7.0";

    public override void OnInitializeMelon()
    {
        Current = this;
        _root = Path.Combine(MelonEnvironment.GameRootDirectory, "UserData", "MuseDashPracticePrototype");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "diagnostics.ndjson"), "");
        try
        {
            if (Application.version != SupportedGame)
                throw new NotSupportedException($"Only Muse Dash {SupportedGame} was inspected; found {Application.version}");
            using (var assemblyStream = File.OpenRead(Path.Combine(MelonEnvironment.GameRootDirectory, "GameAssembly.dll")))
            using (var sha = SHA256.Create())
            {
                var hash = Convert.ToHexString(sha.ComputeHash(assemblyStream));
                if (hash != "0506A723A59BFCFF3BBF335A9AE583DBD72B2435A776D1E6E6E383EC2379D4A3")
                    throw new NotSupportedException("The game binary differs from the inspected build");
            }
            var game = typeof(StageBattleComponent).Assembly;
            // Block both score processing and persistence for the rest of a practice process.
            PatchGuard(game, "Il2CppAssets.Scripts.Database.DataHelper", "Save");
            PatchGuard(game, "Il2CppAssets.Scripts.PeroTools.Platforms.Steam.SteamSync", "SaveLocal");
            PatchGuard(game, "Il2CppAccount.GameAccountSystem", "UploadScore");
            PatchGuard(game, "Il2CppAccount.GameAccountSystem", "Synchronize");
            PatchGuard(game, "Il2CppAssets.Scripts.GameCore.Managers.StatisticsManager", "UploadScore");
            PatchGuard(game, "Il2CppAssets.Scripts.GameCore.Managers.StatisticsManager", "OnBattleEnd");
            PatchGuard(game, "Il2CppAssets.Scripts.GameCore.Managers.AchievementManager", "RecordBattleArgs");
            Patch(typeof(StageBattleComponent), "GameStart", postfix: nameof(StageStarted));
            Patch(typeof(StageBattleComponent), "End", prefix: nameof(InterceptEnd));
            Patch(typeof(StageBattleComponent), "Dead", prefix: nameof(InterceptDeath));
            Patch(typeof(GameMusicScene), "PreLoadEnemy", prefix: nameof(BeforePreload), postfix: nameof(AfterPreload));
            Patch(typeof(UnityEngine.EventSystems.StandaloneInputModule), "Process", prefix: nameof(BlockMenuPointer));
            _ready = true;
            Log("ready", new { game = SupportedGame, persistenceGuards = 7 });
        }
        catch (Exception error)
        {
            _ready = false;
            _message = "Protection setup failed; practice is disabled";
            LoggerInstance.Error(error.ToString());
            Log("setup_error", new { error = error.ToString() });
        }
    }

    private void PatchGuard(Assembly game, string typeName, string method)
    {
        var type = game.GetType(typeName, true)!;
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == method).ToArray();
        if (methods.Length != 1) throw new InvalidOperationException($"Expected one guard target: {typeName}.{method}");
        HarmonyInstance.Patch(methods[0], prefix: new HarmonyMethod(typeof(PracticeMod), nameof(BlockPersistence)) { priority = HarmonyLib.Priority.First });
        Log("guard_installed", new { type = typeName, method });
    }

    private void Patch(Type type, string method, string? prefix = null, string? postfix = null)
    {
        var original = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        HarmonyInstance.Patch(original, prefix is null ? null : new HarmonyMethod(typeof(PracticeMod), prefix),
            postfix is null ? null : new HarmonyMethod(typeof(PracticeMod), postfix));
    }

    private static bool BlockPersistence(MethodBase __originalMethod)
    {
        if (!Tainted) return true;
        var key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
        if (Current?._logged.Add(key) == true) Current.Log("blocked", new { method = key });
        return false;
    }

    private static void StageStarted(StageBattleComponent __instance)
    {
        var mod = Current;
        if (mod is null) return;
        mod._seekPending = mod._enabled;
        mod._startedAt = Time.realtimeSinceStartup;
        mod._firstProbe = true;
        mod._offsetCaptured = false;
        if (mod._enabled && DataHelper.selectedMusicUid?.StartsWith("album_") == true)
        {
            mod._seekPending = false;
            mod._exitPending = true;
            mod._message = "Official charts only; returning to song selection";
        }
        mod.Log("stage_started", new { practice = mod._enabled, uid = DataHelper.selectedMusicUid, difficulty = DataHelper.selectedDifficulty });
    }

    private static bool InterceptEnd()
    {
        if (!Tainted) return true;
        var mod = Current;
        if (mod is not null)
        {
            if (mod._enabled && mod._loop) mod._restartPending = true;
            else mod._exitPending = true;
        }
        return false;
    }

    private static bool InterceptDeath(StageBattleComponent __instance)
    {
        if (Current?._enabled != true) return true;
        __instance.isDead = false;
        return false;
    }

    private static void BeforePreload() { if (Current is { } mod) mod._noteState = null; }

    private static void AfterPreload(GameMusicScene __instance)
    {
        var mod = Current;
        if (mod is null || !mod._ready) return;
        try
        {
            // Capture after native initialization, before the first note is played.
            // Re-running Init on reused controllers would register duplicate events.
            mod._noteState = new NoteResetSnapshot(__instance);
            mod.Log("note_baseline", mod._noteState.Describe());
        }
        catch (Exception error)
        {
            mod._noteState = null;
            mod.Log("note_baseline_error", new { error = error.ToString() });
        }
    }

    private static bool BlockMenuPointer()
    {
        var mod = Current;
        if (mod is null || (!mod._panel && !mod._enabled && !Tainted)) return true;
        var pos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        var area = new Rect(12, 12, Math.Min(590, Screen.width - 24), mod._panel ? 244 : 58);
        return !(area.Contains(pos) && (Input.GetMouseButton(0) || Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0)));
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _inBattle = sceneName == "GameMain";
        if (_inBattle) _chartOpen = true;
        Log("scene", new { sceneName, buildIndex });
        if (!_inBattle)
        {
            _noteState = null;
            _seekPending = false;
            _restartPending = false;
            if (sceneName is "UISystem_PC" or "Welcome")
            {
                _chartOpen = false;
                _exitPending = false;
            }
        }
    }

    public override void OnUpdate()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F6)) _panel = !_panel;
            PollCommand();
            if (!_inBattle) return;
            var stage = StageBattleComponent.instance;
            if (stage is null) return;
            var audio = AudioManager.instance?.bgm;
            if (audio is null || audio.clip is null) return;

            if (_exitPending)
            {
                if (!stage.isInGame) return;
                _exitPending = false;
                _enabled = false;
                BattleHelper.GameFinish();
                return;
            }
            if (_restartPending)
            {
                if (!stage.isInGame) return;
                _restartPending = false;
                if (_fastLoop && !_seekPending && _noteState?.CanRestore(GameGlobal.gGameMusicScene) == true)
                {
                    RewindInScene(stage, audio);
                    return;
                }
                _seekPending = _enabled;
                _startedAt = Time.realtimeSinceStartup;
                if (_fastLoop) Log("reload_fallback", new { reason = "No intact pre-play note baseline" });
                BattleHelper.GameRestart();
                return;
            }

            var raw = stage.timeFromMusicStart;
            _position = raw;
            if (_firstProbe && stage.isInGame && audio.isPlaying)
            {
                _firstProbe = false;
                DumpChart(stage);
            }
            if (stage.isInGame && Time.realtimeSinceStartup - _lastProbe > .5f)
            {
                _lastProbe = Time.realtimeSinceStartup;
                Probe(stage, audio);
            }
            if (!_enabled || !stage.isInGame || stage.isPause) return;
            if (Input.GetKeyDown(KeyCode.F7)) { _a = raw; _aText = _a.ToString("F2", CultureInfo.InvariantCulture); }
            if (Input.GetKeyDown(KeyCode.F8)) { _b = raw; _bText = _b.ToString("F2", CultureInfo.InvariantCulture); }
            if (Input.GetKeyDown(KeyCode.F9)) Arm();
            if (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.K))
                Log("input", new { chartTime = raw, air = Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.D),
                    ground = Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.K), score = TaskStageTarget.instance.m_Score });
            if (_seekPending && audio.isPlaying && audio.time > .45f && Time.realtimeSinceStartup - _startedAt > .5f)
            {
                _seekPending = false;
                SeekAtFreshStart(stage, audio);
                return;
            }
            if (!_seekPending && _b >= _a + 1 && raw >= _b)
            {
                Log("segment_end", new { round = _round, chartTime = raw, a = _a, b = _b });
                if (_loop) _restartPending = true;
                else _exitPending = true;
            }
        }
        catch (Exception error)
        {
            _enabled = false;
            _seekPending = false;
            _exitPending = _chartOpen;
            _message = "Stopped after an error; see diagnostics. Scores remain blocked.";
            Log("runtime_error", new { error = error.ToString() });
            LoggerInstance.Error(error.ToString());
        }
    }

    private void SeekAtFreshStart(StageBattleComponent stage, AudioSource audio)
    {
        if (_b > audio.clip.length - .25f || _a >= audio.clip.length - 1)
            throw new InvalidOperationException("A/B is outside this song. Set B below the song duration.");
        var music = GameGlobal.gGameMusic ?? throw new InvalidOperationException("No chart controller");
        var scene = GameGlobal.gGameMusicScene ?? throw new InvalidOperationException("No scene controller");
        var watch = GameGlobal.stopwatch ?? throw new InvalidOperationException("No chart clock");
        if (FixUpdateTimer.precision != 100 || Math.Abs(audio.pitch - 1) > .001f)
            throw new InvalidOperationException("Unsupported timer precision or playback speed");

        // Jump to a lead-in before A, allowing notes and holds to spawn normally.
        var target = Math.Max(0, _a - 3f);
        var before = stage.timeFromMusicStart;
        _audioOffset = audio.time - before;
        if (Math.Abs(_audioOffset) > .5f) throw new InvalidOperationException("Unexpected audio/chart offset");
        _offsetCaptured = true;
        if (_fastLoop && _noteState?.CanRestore(scene) == true)
        {
            RewindInScene(stage, audio);
            return;
        }
        if (target > before + .05f)
        {
            watch.Stop();
            var tick = (int)Math.Floor(target * FixUpdateTimer.precision);
            MoveTimer(music.objTimer, tick);
            MoveTimer(music.stepTimer, tick);
            MoveTimer(scene.stepTimer, tick);
            if (music.sceneObjTimer?.IsRunning() == true) MoveTimer(music.sceneObjTimer, tick);

            var lastSkipped = 0;
            var notes = stage.m_MusicTickData;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (DecimalSeconds(note.showTick) < target) lastSkipped = Math.Max(lastSkipped, note.objId);
            }
            // GetMusicIndexByGenTick uses this cursor; without advancing it, old notes respawn.
            BattleEnemyManager.instance.m_CurrentGenIdx = lastSkipped;
            scene.passIdx = lastSkipped;
            scene.prePassIdx = lastSkipped;
            music.m_LastMusicTick = target * 1000;
            watch.elapsed = (long)(target * Il2CppSystem.Diagnostics.Stopwatch.Frequency);
            audio.timeSamples = Math.Clamp((int)((target + _audioOffset) * audio.clip.frequency), 0, audio.clip.samples - 1);
            watch.Start();
            if (Math.Abs(stage.timeFromMusicStart - target) > .08f)
                throw new InvalidOperationException("Chart clock seek did not match the requested position");
        }
        _round++;
        _message = $"Round {_round}: {_a:F2}-{_b:F2}s | 3s lead-in | F9 repeat";
        Log("seek", new { round = _round, a = _a, b = _b, target, before, after = stage.timeFromMusicStart, audioTime = audio.time, offset = _audioOffset,
            genIndex = BattleEnemyManager.instance.m_CurrentGenIdx, frequency = Il2CppSystem.Diagnostics.Stopwatch.Frequency });
        Probe(stage, audio);
    }

    private void RewindInScene(StageBattleComponent stage, AudioSource audio)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var music = GameGlobal.gGameMusic ?? throw new InvalidOperationException("No chart controller");
        var scene = GameGlobal.gGameMusicScene ?? throw new InvalidOperationException("No scene controller");
        var watch = GameGlobal.stopwatch ?? throw new InvalidOperationException("No chart clock");
        var noteState = _noteState ?? throw new InvalidOperationException("No pre-play note baseline");
        if (!noteState.CanRestore(scene)) throw new InvalidOperationException("Preloaded note objects have changed");
        if (stage.isPause) stage.Resume(false);
        if (_b > audio.clip.length - .25f || _a >= audio.clip.length - 1 || FixUpdateTimer.precision != 100 || Math.Abs(audio.pitch - 1) > .001f)
            throw new InvalidOperationException("Unsupported range, timer or playback speed");
        var before = stage.timeFromMusicStart;
        if (!_offsetCaptured)
        {
            _audioOffset = audio.time - before;
            if (Math.Abs(_audioOffset) > .5f) throw new InvalidOperationException("Unexpected audio/chart offset");
            _offsetCaptured = true;
        }
        var target = Math.Max(0, _a - 3f);
        var sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        var clipId = audio.clip.GetInstanceID();
        var stageId = stage.Pointer.ToInt64();
        audio.Pause();
        watch.Stop();

        var inputRoutesBefore = CountGameplayInputRoutes();
        music.Reset();
        // Reset -> GameTouchPlay.Init replaces GTrigger event 2 but appends a new
        // TouchScript route. Remove old routes before any input is dispatched.
        var staleInputRoutesRemoved = RestoreSingleGameplayInputRoute();
        Log("input_routes_reset", new { round = _round + 1, before = inputRoutesBefore,
            removed = staleInputRoutesRemoved, after = CountGameplayInputRoutes() });
        ResetHeldInput();
        noteState.Restore();
        var notes = stage.m_MusicTickData;
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            note.isLongPressing = false;
            notes[i] = note;
        }
        var sorted = stage.m_SortedMusicTickData;
        if (sorted is not null)
            for (var i = 0; i < sorted.Count; i++)
            {
                var note = sorted[i];
                note.isLongPressing = false;
                sorted[i] = note;
            }
        stage.InitTimeNodeOrder();
        BattleEnemyManager.instance.Init();
        BattleEnemyManager.instance.PreloadCreateBattleEnemy();
        TaskStageTarget.instance.Reset();
        stage.isDead = false;
        stage.isSucceed = false;
        stage.failTime = 0;
        BattleRoleAttributeComponent.instance.AddHp(BattleRoleAttributeComponent.instance.GetHpMax());
        FeverManager.Instance.ResetFever();

        var lastSkipped = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (DecimalSeconds(note.showTick) < target) lastSkipped = Math.Max(lastSkipped, note.objId);
        }
        BattleEnemyManager.instance.m_CurrentGenIdx = lastSkipped;
        scene.passIdx = lastSkipped;
        scene.prePassIdx = lastSkipped;
        music.m_TnoIdx = -1;
        music.m_LastMusicTick = target * 1000;
        music.m_IsEnd = false;
        music.m_IsVictoryShow = false;
        var tick = (int)Math.Floor(target * FixUpdateTimer.precision);
        MoveTimer(music.objTimer, tick);
        MoveTimer(music.stepTimer, tick);
        MoveTimer(scene.stepTimer, tick);
        if (music.sceneObjTimer?.IsRunning() == true) MoveTimer(music.sceneObjTimer, tick);
        watch.elapsed = (long)(target * Il2CppSystem.Diagnostics.Stopwatch.Frequency);
        audio.timeSamples = Math.Clamp((int)((target + _audioOffset) * audio.clip.frequency), 0, audio.clip.samples - 1);
        audio.UnPause();
        watch.Start();
        if (Math.Abs(stage.timeFromMusicStart - target) > .08f)
            throw new InvalidOperationException("Rewind clock mismatch");
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != sceneHandle || audio.clip.GetInstanceID() != clipId || stage.Pointer.ToInt64() != stageId)
            throw new InvalidOperationException("Scene or audio resource changed during rewind");
        _round++;
        _position = target;
        _message = $"Round {_round}: {_a:F2}-{_b:F2}s | in-scene rewind | 3s lead-in";
        Log("fast_rewind", new { round = _round, before, target, after = stage.timeFromMusicStart,
            audioTime = audio.time, offset = _audioOffset, resetMs = started.Elapsed.TotalMilliseconds,
            sceneHandle, clipId, stageId, noteObjects = noteState.RootCount, resetState = noteState.Describe(),
            resetPerfect = TaskStageTarget.instance.m_PerfectResult, resetScore = TaskStageTarget.instance.m_Score });
        Probe(stage, audio);
    }

    private static void ResetHeldInput()
    {
        var touch = GameGlobal.gGameTouchPlay;
        touch.Reset();
        touch.m_AirPressIndex?.Clear();
        touch.m_RoadPressIndex?.Clear();
        touch.m_AirKeyIndex?.Clear();
        touch.m_RoadKeyIndex?.Clear();
        touch.m_AllKeyPressedList?.Clear();
        touch.m_CurFingerIds?.Clear();
        touch.isPunch = false;
        touch.isReactAir = false;
        touch.isReactGround = false;
        touch.m_PressHardTime = default;
        touch.m_PunchHardTime = default;
        touch.m_JumpHardTime = default;
        GameGlobal.gTouch?.Reset();
    }

    private static int CountGameplayInputRoutes()
    {
        var routes = GameGlobal.gTouch?.m_CustomEvent;
        if (routes is null) return 0;
        var count = 0;
        for (var i = 0; i < routes.Count; i++)
            if (routes[i]?.id == InputRouteReset.GameplayEventId) count++;
        return count;
    }

    private static int RestoreSingleGameplayInputRoute()
    {
        var routes = GameGlobal.gTouch?.m_CustomEvent ?? throw new InvalidOperationException("No input routes");
        var removed = InputRouteReset.KeepNewestGameplayRoute(routes.Count,
            index => routes[index]?.id, index => routes.RemoveAt(index));
        if (CountGameplayInputRoutes() != 1)
            throw new InvalidOperationException("Expected exactly one gameplay input route after reset");
        var handlers = GTrigger.m_EventTriggers;
        if (handlers is null || handlers.Length <= InputRouteReset.GameplayEventId)
            throw new InvalidOperationException("No gameplay event handler table");
        var current = handlers[(int)InputRouteReset.GameplayEventId];
        for (var i = 0; i < routes.Count; i++)
            if (routes[i]?.id == InputRouteReset.GameplayEventId
                && (current is null || routes[i].Pointer != current.Pointer))
                throw new InvalidOperationException("Gameplay input route does not match the current handler");
        return removed;
    }

    private static float DecimalSeconds(Il2CppSystem.Decimal value) =>
        float.Parse(value.ToString(), CultureInfo.InvariantCulture);

    private static void MoveTimer(FixUpdateTimer? timer, int tick)
    {
        if (timer is null) return;
        timer.SetPassTick(tick);
        timer.prePassedTick = tick;
        timer.m_curPassedTick = tick;
    }

    private void DumpChart(StageBattleComponent stage)
    {
        var music = GameGlobal.gGameMusic;
        var scene = GameGlobal.gGameMusicScene;
        var data = stage.m_MusicTickData;
        var notes = new List<object>();
        if (data is not null)
        {
            for (var i = 0; i < Math.Min(data.Count, 8); i++)
            {
                var md = data[i];
                notes.Add(new { i, objId = md.objId, tick = md.tick.ToString(), showTick = md.showTick.ToString(), longPress = md.isLongPressType });
            }
            if (data.Count > 0) notes.Add(new { lastTick = data[data.Count - 1].tick.ToString(), count = data.Count });
        }
        Log("chart", new { notes, obj = Timer(music?.objTimer), step = Timer(music?.stepTimer), sceneObj = Timer(music?.sceneObjTimer), sceneStep = Timer(scene?.stepTimer) });
    }

    private static object? Timer(Il2CppDYUnityLib.FixUpdateTimer? timer) => timer is null ? null :
        new { timer.passedTick, timer.prePassedTick, timer.passedCount, timer.totalTick, precision = Il2CppDYUnityLib.FixUpdateTimer.precision, interval = Il2CppDYUnityLib.FixUpdateTimer.dInterval.ToString(), fInterval = Il2CppDYUnityLib.FixUpdateTimer.fInterval, timer.m_Index, timer.m_curPassedTick, timer.isPause };

    private void Probe(StageBattleComponent stage, AudioSource audio)
    {
        var music = GameGlobal.gGameMusic;
        var scene = GameGlobal.gGameMusicScene;
        Log("sample", new
        {
            practice = _enabled, tainted = Tainted, round = _round, rawTime = stage.timeFromMusicStart,
            decimalTime = stage.timeFromMusicStartDecimal.ToString(), stage.musicStartTime,
            audioTime = audio.time, samples = audio.timeSamples, audio.isPlaying, duration = audio.clip?.length,
            stopwatchMs = GameGlobal.stopwatch?.ElapsedMilliseconds,
            stage.lastTnoIndex, lastTnoTime = stage.lastTnoTime.ToString(), stage.isInGame, stage.isPause,
            musicIndex = music?.m_TnoIdx, lastMusicTick = music?.m_LastMusicTick,
            objTick = music?.objTimer?.passedTick, stepTick = music?.stepTimer?.passedTick,
            sceneObjTick = music?.sceneObjTimer?.passedTick, sceneStepTick = scene?.stepTimer?.passedTick,
            sceneIndex = scene?.passIdx, genIndex = BattleEnemyManager.instance?.m_CurrentGenIdx,
            sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle, clipId = audio.clip?.GetInstanceID(),
            score = TaskStageTarget.instance.m_Score, perfect = TaskStageTarget.instance.m_PerfectResult,
            great = TaskStageTarget.instance.m_GreatResult, miss = TaskStageTarget.instance.m_MissResult
        });
    }

    private void Arm()
    {
        if (!_ready) { _message = "Protection checks failed: practice disabled"; return; }
        if (!float.TryParse(_aText, NumberStyles.Float, CultureInfo.InvariantCulture, out var nextA)
            || !float.TryParse(_bText, NumberStyles.Float, CultureInfo.InvariantCulture, out var nextB)
            || !float.IsFinite(nextA) || !float.IsFinite(nextB) || nextA < 0 || nextB < nextA + 1)
        { _message = "Use seconds: A >= 0; B must be at least A + 1"; return; }
        if (_inBattle && DataHelper.selectedMusicUid?.StartsWith("album_") == true)
        { _message = "This prototype supports official charts only"; return; }
        Tainted = true;
        _a = nextA;
        _b = nextB;
        _enabled = true;
        _round = 0;
        _message = "Practice armed. This process will NOT save or upload scores.";
        Log("armed", new { a = _a, b = _b, loop = _loop });
        if (_inBattle) _restartPending = true;
    }

    private void PollCommand()
    {
        if (Time.realtimeSinceStartup < _nextCommand) return;
        _nextCommand = Time.realtimeSinceStartup + .25f;
        var path = Path.Combine(_root, "command.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            if (root.TryGetProperty("a", out var a)) _aText = a.GetSingle().ToString(CultureInfo.InvariantCulture);
            if (root.TryGetProperty("b", out var b)) _bText = b.GetSingle().ToString(CultureInfo.InvariantCulture);
            if (root.TryGetProperty("loop", out var loop)) _loop = loop.GetBoolean();
            if (root.TryGetProperty("fast", out var fast)) _fastLoop = fast.GetBoolean();
            switch (action)
            {
                case "arm": Arm(); break;
                case "restart": if (_enabled) _restartPending = true; break;
                case "exit": RequestExit(); break;
                case "stop": _enabled = false; _message = "Practice stopped. Restart the game for normal score saving."; break;
                case "show": _panel = true; break;
            }
            File.Delete(path); // Only this mod's fixed, one-shot diagnostic command file.
            Log("command", new { action });
        }
        catch (JsonException) { /* A writer may not have finished yet; retry next frame. */ }
    }

    private void RequestExit()
    {
        _enabled = false;
        _restartPending = false;
        _seekPending = false;
        _exitPending = _chartOpen;
        _message = "Practice stopped. Restart the game for normal score saving.";
        Log("exit_requested", new { inBattle = _inBattle, chartOpen = _chartOpen });
    }

    public override void OnGUI()
    {
        if (!_panel && !_enabled && !Tainted) return;
        try
        {
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true };
            var width = Math.Min(590, Screen.width - 24);
            GUI.Box(new Rect(12, 12, width, _panel ? 244 : 58), "");
            GUI.Label(new Rect(24, 20, width - 25, 30), _enabled ? $"PRACTICE - NO SCORES | {_position:F2}s" : Tainted ? "NO SCORES - Restart game for normal play" : "Muse Dash Practice Prototype", _label);
            if (!_panel) { GUI.Label(new Rect(24, 46, width - 25, 25), "F6 settings | F7 A | F8 B | F9 repeat"); return; }
            GUI.Label(new Rect(24, 57, 95, 24), "A (seconds)");
            _aText = GUI.TextField(new Rect(120, 57, 95, 26), _aText, 12);
            GUI.Label(new Rect(235, 57, 95, 24), "B (seconds)");
            _bText = GUI.TextField(new Rect(330, 57, 95, 26), _bText, 12);
            _loop = GUI.Toggle(new Rect(24, 94, 180, 26), _loop, "Repeat A-B");
            _fastLoop = GUI.Toggle(new Rect(220, 94, 300, 26), _fastLoop, "In-scene rewind (experimental)");
            if (GUI.Button(new Rect(24, 130, 165, 32), _enabled ? "Restart at A" : "Arm practice"))
                Arm();
            if (GUI.Button(new Rect(200, 130, 165, 32), "Exit practice chart")) RequestExit();
            if (GUI.Button(new Rect(378, 130, 175, 32), "Hide panel [F6]")) _panel = false;
            GUI.Label(new Rect(24, 174, width - 40, 62), _message, _label);
        }
        catch (Exception error) { if (_logged.Add("gui_error")) Log("gui_error", new { error = error.ToString() }); }
    }

    private void Log(string kind, object? details)
    {
        try { File.AppendAllText(Path.Combine(_root, "diagnostics.ndjson"), JsonSerializer.Serialize(new { utc = DateTime.UtcNow, kind, details }) + Environment.NewLine); }
        catch { /* A logging failure must not interrupt the game. */ }
    }
}
