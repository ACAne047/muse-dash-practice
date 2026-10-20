using Il2Cpp;
using Il2CppGameLogic;
using Il2CppFormulaBase;
using Il2CppSpine;
using Il2CppSpine.Unity;
using UnityEngine;

namespace MuseDashPracticePrototype;

public sealed partial class PracticeMod
{
    private static object CatchUpBoundaryPose(GameMusicScene scene, int id, GameObject root, float songTime)
    {
        var action = scene.GetSpineCtrl(id);
        var fallbackShow = action ? action.ShowTick : DecimalSeconds(StageBattleComponent.instance.GetMusicDataByIdx(id).showTick);
        var elapsed = BoundaryPoseClock.Elapsed(songTime, fallbackShow);
        var animators = 0;
        var skeletons = 0;
        var legacy = 0;
        var before = VisualCenter(root);

        foreach (var animator in root.GetComponentsInChildren<Animator>(true))
        {
            if (!animator.gameObject.activeInHierarchy || !animator.runtimeAnimatorController) continue;
            var events = animator.fireEvents;
            var ownAction = animator.GetComponentInParent<SpineActionController>();
            var age = BoundaryPoseClock.Elapsed(songTime, ownAction ? ownAction.ShowTick : fallbackShow);
            try
            {
                animator.fireEvents = false;
                animator.Update(0); // Apply the native Play request before sampling.
                animator.Update(age);
                animators++;
            }
            finally { animator.fireEvents = events; }
        }
        foreach (var animation in root.GetComponentsInChildren<UnityEngine.Animation>(true))
        {
            if (!animation.gameObject.activeInHierarchy || !animation.isPlaying) continue;
            foreach (UnityEngine.AnimationState entry in animation)
                if (entry.enabled) entry.time += elapsed * entry.speed;
            animation.Sample();
            legacy++;
        }
        foreach (var component in root.GetComponentsInChildren<SkeletonAnimation>(true))
        {
            if (!component.gameObject.activeInHierarchy || component.AnimationState is not { } state) continue;
            var ownAction = component.GetComponentInParent<SpineActionController>();
            var age = BoundaryPoseClock.Elapsed(songTime, ownAction ? ownAction.ShowTick : fallbackShow);
            var entries = LiveTracks(state);
            HashSet<long>? after = null;
            var owners = new List<object> { state };
            owners.AddRange(entries);
            using var mute = new ScopedCallbackMute(owners, owner => owner is Il2CppSpine.AnimationState
                || after is null || after.Contains(((TrackEntry)owner).Pointer.ToInt64()));
            var mode = component.UpdateMode;
            try
            {
                // Old off-screen culling modes can update events without a pose.
                component.UpdateMode = UpdateMode.FullUpdate;
                component.Update(age);
                // Spine transitions queued clips using the last applied time.
                // Flush transitions at zero delta; never advance elapsed twice.
                for (var pass = 0; pass < entries.Count + 1; pass++)
                {
                    var current = CurrentTrackIds(state);
                    component.Update(0);
                    if (CurrentTrackIds(state).SequenceEqual(current)) break;
                }
                component.ForceUpdate(true); // Zero delta + world pose + mesh now.
                skeletons++;
            }
            finally
            {
                try { after = LiveTracks(state).Select(track => track.Pointer.ToInt64()).ToHashSet(); }
                finally { component.UpdateMode = mode; }
            }
        }
        return new { id, songTime, showTime = fallbackShow, elapsed, animators, skeletons, legacy,
            before, after = VisualCenter(root) };
    }

    private static long[] CurrentTrackIds(Il2CppSpine.AnimationState state)
    {
        var result = new long[state.tracks?.Count ?? 0];
        for (var i = 0; i < result.Length; i++) result[i] = state.GetCurrent(i)?.Pointer.ToInt64() ?? 0;
        return result;
    }

    private static List<TrackEntry> LiveTracks(Il2CppSpine.AnimationState state)
    {
        var result = new List<TrackEntry>();
        var seen = new HashSet<long>();
        void Add(TrackEntry? track)
        {
            if (track is null || !seen.Add(track.Pointer.ToInt64())) return;
            result.Add(track);
            Add(track.next);
            Add(track.mixingFrom);
            Add(track.mixingTo);
        }
        var tracks = state.tracks;
        if (tracks is not null)
            for (var i = 0; i < tracks.Count; i++) Add(tracks.Items[i]);
        return result;
    }

    private static object? VisualCenter(GameObject root)
    {
        var renderer = root.GetComponentsInChildren<Renderer>(true)
            .FirstOrDefault(candidate => candidate.enabled && candidate.gameObject.activeInHierarchy);
        if (renderer is null) return null;
        var center = renderer.bounds.center;
        return new { center.x, center.y, center.z };
    }
}
