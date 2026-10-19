using Il2Cpp;
using Il2CppAssets.Scripts.UI.GameMain;
using Il2CppDG.Tweening;
using Il2CppPeroPeroGames.GlobalDefines;
using UnityEngine;

namespace MuseDashPracticePrototype;

// The burst controller's mode flags do not own the HUD or the camera's zoom.
// Keep their pre-play view separately from the note-object snapshots.
internal sealed class BurstPresentationReset
{
    private readonly Camera? _camera;
    private readonly float _normalSize;
    private readonly Vector3 _position;
    private readonly Quaternion _rotation;
    private int _lastCounters;
    private float? _lastSizeBefore;
    private float? _lastSizeAfter;

    internal BurstPresentationReset()
    {
        var camera = MultHitEnemyController.m_Camera;
        if (!camera) return;
        _camera = camera;
        // Init records this value before ChangeViewWhenNoteHitStart shrinks it.
        var originSize = MultHitEnemyController.m_OriginSize;
        _normalSize = float.IsFinite(originSize) && originSize > 0 ? originSize : camera.orthographicSize;
        _position = camera.transform.localPosition;
        _rotation = camera.transform.localRotation;
    }

    internal void Restore()
    {
        _lastCounters = 0;
        foreach (var counter in Resources.FindObjectsOfTypeAll<CountAnimator>())
        {
            // Ignore prefab assets and counters for combo/score; cover live UI skins,
            // including inactive scene instances that might otherwise reappear stale.
            if (!counter || !counter.gameObject.scene.IsValid() || counter.states is null
                || counter.GetEventType() != EventDefine.on_multi_hit_changed) continue;
            counter.OnCountChanged(0);
            // State.Execute uses the previous count to choose an end animation.
            // A second zero settles it in the off state without waiting on that animation.
            counter.OnCountChanged(0);
            _lastCounters++;
        }

        if (_camera is null || !_camera) return;
        _lastSizeBefore = _camera.orthographicSize;
        // The zoom tween targets Camera; the owned shake sequence alone cannot stop it.
        ShortcutExtensions.DOKill(_camera, false);
        ShortcutExtensions.DOKill(_camera.transform, false);
        _camera.orthographicSize = _normalSize;
        _camera.transform.localPosition = _position;
        _camera.transform.localRotation = _rotation;
        _lastSizeAfter = _camera.orthographicSize;
        if (Math.Abs(_lastSizeAfter.Value - _normalSize) > .001f)
            throw new InvalidOperationException("Burst camera zoom did not return to its baseline");
    }

    internal object Describe() => new
    {
        hasCamera = _camera is not null && _camera, normalSize = _normalSize,
        countersReset = _lastCounters, sizeBefore = _lastSizeBefore, sizeAfter = _lastSizeAfter
    };
}
