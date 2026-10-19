using Il2Cpp;
using Il2CppDG.Tweening;
using UnityEngine;

namespace MuseDashPracticePrototype;

internal sealed class HoldStarAnimationReset
{
    private readonly List<Tweener> _loops = new();
    private readonly HashSet<long> _loopIds = new();
    private readonly HashSet<long> _targets = new();
    public int Count => _loops.Count;
    public bool CanRestore => _loops.All(TweenExtensions.IsActive);

    public HoldStarAnimationReset(HashSet<long> noteComponents)
    {
        var source = LongActionController.s_StarTweeners;
        if (source is null) return;
        foreach (var tween in source)
        {
            var target = tween?.target;
            if (tween is null || target is null || !noteComponents.Contains(target.Pointer.ToInt64())
                || !TweenExtensions.IsActive(tween) || !_loopIds.Add(tween.Pointer.ToInt64())) continue;
            _loops.Add(tween);
            _targets.Add(target.Pointer.ToInt64());
        }
    }

    public void Rewind()
    {
        if (!CanRestore) throw new InvalidOperationException("Preloaded hold star animations were destroyed");
        foreach (var tween in _loops) TweenExtensions.Rewind(tween, true);
    }

    public void KillTransientTweens(UnityEngine.Object target)
    {
        if (!_targets.Contains(target.Pointer.ToInt64())) { DOTween.Kill(target, false); return; }
        var tweens = DOTween.TweensByTarget(target, false, null);
        if (tweens is null) return;
        foreach (var tween in tweens)
            if (tween is not null && !_loopIds.Contains(tween.Pointer.ToInt64())) TweenExtensions.Kill(tween, false);
    }

    public void Restart()
    {
        foreach (var tween in _loops) TweenExtensions.Restart(tween, true, -1f);
    }
}
