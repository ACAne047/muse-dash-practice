using System.Globalization;
using System.Reflection;
using Il2Cpp;
using Il2CppDG.Tweening;
using Il2CppGameLogic;
using Il2CppSpine.Unity;
using Il2CppAssets.Scripts.GameCore.GameObjectLogics.GameObjectControl;
using UnityEngine;

namespace MuseDashPracticePrototype;

// A snapshot of initialized, never-played notes. Native ObjInit only activates
// the object and calls NodeInitController.Run; it does not restore prefab state.
internal sealed class NoteResetSnapshot
{
    private readonly long _scene;
    private readonly List<GameObject> _roots;
    private readonly HashSet<int> _rootIds;
    private readonly Dictionary<int, TransformState> _transforms = new();
    private readonly List<AnimatorState> _animators = new();
    private readonly List<SkeletonAnimation> _skeletons = new();
    private readonly List<RendererState> _renderers = new();
    private readonly List<MaterialState> _materials = new();
    private readonly List<ParticleSystem> _particles = new();
    private readonly List<MonoBehaviour> _scripts = new();
    private readonly List<Animation> _legacyAnimations = new();
    private readonly List<SpineActionController> _actions = new();
    private readonly List<LongPressNoteVisibleController> _holdVisibility = new();
    private readonly List<MultHitEnemyController> _multiHits = new();
    private readonly BurstPresentationReset? _burstPresentation;
    private readonly List<FieldState> _fields = new();
    private readonly HashSet<long> _componentPointers = new();
    private readonly List<Il2CppAssets.Scripts.GameCore.Interface.IUpdate> _initialUpdates = new();
    private readonly List<Il2CppAssets.Scripts.GameCore.Interface.IUpdate> _initialFixedUpdates = new();
    private int _holds;

    public int RootCount => _roots.Count;

    public NoteResetSnapshot(GameMusicScene scene)
    {
        _scene = scene.Pointer.ToInt64();
        _roots = NoteRoots(scene);
        if (_roots.Count == 0) throw new InvalidOperationException("No preloaded notes to snapshot");
        _rootIds = _roots.Select(obj => obj.GetInstanceID()).ToHashSet();
        foreach (var root in _roots) CaptureTransform(root.transform);

        // Include detached long-note stars as well as the ordinary note hierarchy.
        foreach (var state in _transforms.Values.ToArray())
        {
            foreach (var component in state.Transform.gameObject.GetComponents<MonoBehaviour>())
            {
                var sac = component.TryCast<SpineActionController>();
                if (sac is null) continue;
                if (sac.m_StartStar) CaptureTransform(sac.m_StartStar.transform);
                if (sac.m_EndStar) CaptureTransform(sac.m_EndStar.transform);
            }
        }
        foreach (var state in _transforms.Values)
        {
            var obj = state.Transform.gameObject;
            foreach (var component in obj.GetComponents<Component>())
            {
                if (component is null || !component || !_componentPointers.Add(component.Pointer.ToInt64())) continue;
                CaptureComponent(component);
            }
        }
        if (_multiHits.Count > 0) _burstPresentation = new BurstPresentationReset();
        var database = DataHelperScene();
        CaptureUpdates(database?.m_Updates, _initialUpdates);
        CaptureUpdates(database?.m_FixedUpdates, _initialFixedUpdates);
        void CaptureUpdates(Il2CppSystem.Collections.Generic.List<Il2CppAssets.Scripts.GameCore.Interface.IUpdate>? source,
            List<Il2CppAssets.Scripts.GameCore.Interface.IUpdate> target)
        {
            if (source is null) return;
            foreach (var update in source)
                if (update is not null && _componentPointers.Contains(update.Pointer.ToInt64())) target.Add(update);
        }
    }

    public object Describe() => new
    {
        roots = RootCount, transforms = _transforms.Count, animators = _animators.Count,
        skeletons = _skeletons.Count, holds = _holds, multiHits = _multiHits.Count, fields = _fields.Count,
        burstPresentation = _burstPresentation?.Describe()
    };

    public bool CanRestore(GameMusicScene? scene)
    {
        if (scene is null || scene.Pointer.ToInt64() != _scene) return false;
        if (_roots.Any(obj => !obj) || _transforms.Values.Any(state => !state.Transform || !state.ParentIsIntact)) return false;
        return _rootIds.SetEquals(NoteRoots(scene).Select(obj => obj.GetInstanceID()));
    }

    public void Restore()
    {
        foreach (var root in _roots) root.SetActive(false);
        foreach (var script in _scripts) if (script) script.StopAllCoroutines();

        if (_multiHits.Count > 0)
        {
            // Completed bursts keep both per-object state and shared mode flags.
            // Rewind the owned shake before killing it; the native shake methods
            // create a new sequence when this reference is null.
            if (MultHitEnemyController.m_HittingCameraSequence is { } sequence && TweenExtensions.IsActive(sequence))
            {
                TweenExtensions.Rewind(sequence, true);
                TweenExtensions.Kill(sequence, false);
            }
            MultHitEnemyController.m_HittingCameraSequence = null;
            MultHitEnemyController.isMulHitEnding = false;
            MultHitEnemyController.isBanEmptyAction = false;
            MultHitEnemyController.hitCount = 0;
            MultHitEnemyController.isHitting = false;
        }

        // A hold's star has a game-manager loop independent of activeSelf.
        foreach (var action in _actions)
        {
            if (action.actionMode == 12)
                SpineActionController.s_UnityGameManger?.UnregLoop(action.m_MusicData.objId.ToString(CultureInfo.InvariantCulture));
            action.ClearTracks();
        }
        KillTweens(LongActionController.s_StarTweeners);
        LongActionController.PlayLongPressEffect(false, false);
        LongActionController.PlayLongPressEffect(false, true);
        StopParticles(LongActionController.s_GroundClipParticles);
        StopParticles(LongActionController.s_AirClipParticles);
        StopParticles(Il2CppAssets.Scripts.GameCore.GameObjectLogics.GameObjectManager.AttackEffectManager.instance?.m_ParticleLongPress);
        foreach (var visible in _holdVisibility)
        {
            KillTweens(visible.m_Tweeners);
            visible.IsSliderDisappearing = false;
        }
        foreach (var particle in _particles)
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        // Cancel only note-owned tweens, never the game's global tween list.
        foreach (var state in _transforms.Values)
            ShortcutExtensions.DOKill(state.Transform, false);
        foreach (var state in _renderers)
            ShortcutExtensions.DOKill(state.Renderer, false);
        foreach (var state in _materials) ShortcutExtensions.DOKill(state.Material, false);

        RemoveNoteUpdates();
        foreach (var animation in _legacyAnimations) animation.Stop();
        foreach (var state in _animators)
        {
            state.Animator.Rebind();
            state.Animator.speed = state.Speed;
            state.Animator.enabled = state.Enabled;
        }
        foreach (var skeleton in _skeletons)
        {
            skeleton.AnimationState?.ClearTracks();
            skeleton.Skeleton?.SetToSetupPose();
            skeleton.Skeleton?.UpdateWorldTransform();
        }
        foreach (var field in _fields) field.Restore();

        // Animator.Rebind and old hold callbacks can alter transforms: restore pose
        // after cancelling animation/tweens, with original parents and local space.
        foreach (var state in _transforms.Values) state.RestorePose();
        foreach (var state in _renderers) state.Restore();
        foreach (var state in _materials) state.Restore();
        foreach (var state in _transforms.Values)
            if (!_rootIds.Contains(state.Transform.gameObject.GetInstanceID()))
                state.Transform.gameObject.SetActive(state.Active);
        foreach (var root in _roots) root.SetActive(false);

        _burstPresentation?.Restore();

        // Fail closed if a restore is incomplete, before advancing chart time.
        foreach (var state in _transforms.Values)
            if ((state.Transform.localPosition - state.Position).sqrMagnitude > .000001f)
                throw new InvalidOperationException("Note pose restore did not match its baseline");
        if (_actions.Any(action => action.m_HasDestroy || action.m_HasAlpha))
            throw new InvalidOperationException("Long-note destruction flags were not reset");
        if (_multiHits.Any(note => note.m_IsFlyAway || note.m_HitCount != 0))
            throw new InvalidOperationException("Multi-hit completion state was not reset");
    }

    private void CaptureTransform(Transform transform)
    {
        if (!transform || _transforms.ContainsKey(transform.GetInstanceID())) return;
        _transforms.Add(transform.GetInstanceID(), new TransformState(transform));
        for (var i = 0; i < transform.childCount; i++) CaptureTransform(transform.GetChild(i));
    }

    private void CaptureComponent(Component component)
    {
        if (component.TryCast<MonoBehaviour>() is { } script) _scripts.Add(script);
        if (component.TryCast<Animator>() is { } animator) _animators.Add(new AnimatorState(animator));
        if (component.TryCast<Animation>() is { } animation) _legacyAnimations.Add(animation);
        if (component.TryCast<SkeletonAnimation>() is { } skeleton)
        {
            _skeletons.Add(skeleton);
            CaptureFields(skeleton, "timeScale");
        }
        if (component.TryCast<ParticleSystem>() is { } particle) _particles.Add(particle);
        if (component.TryCast<Renderer>() is { } renderer) _renderers.Add(new RendererState(renderer));

        if (component.TryCast<NodeInitController>() is { } node) CaptureFields(node, "countdown");
        if (component.TryCast<SpineActionController>() is { } sac)
        {
            _actions.Add(sac);
            CaptureFields(sac, "m_ProtectLevel", "m_CurrentActionName", "m_CurrentSpineActionName",
                "curActionKey", "m_IsPause", "m_HasDestroy", "m_HasAlpha", "m_Length", "m_MusicData");
            if (sac.m_Mtrl && sac.m_Mtrl.HasProperty("_Color")) _materials.Add(new MaterialState(sac.m_Mtrl));
        }
        if (component.TryCast<BaseSpineObjectController>() is { } controller)
            CaptureFields(controller, "m_IsVisible", "isIn");
        if (component.TryCast<BaseEnemyObjectController>() is { } enemy)
            CaptureFields(enemy, "m_MusicData", "m_HasMiss", "m_IsBeAttacked", "m_HasNoteResult",
                "nextNoteResult", "m_IsBoom", "m_ReleaseFlag", "attackedDoNothing");
        if (component.TryCast<LongPressController>() is { } hold)
        {
            _holds++;
            CaptureFields(hold, "m_IsActive", "m_IsHurt", "m_FingerId", "m_PMusicData");
        }
        if (component.TryCast<MultHitEnemyController>() is { } multiHit)
        {
            _multiHits.Add(multiHit);
            // isDead reads m_IsFlyAway; isOver compares m_HitCount with the high
            // threshold. Restoring HP alone leaves both of these getters stale.
            CaptureFields(multiHit, "m_IsFlyAway", "m_HitCount", "m_AtkTime", "m_HitTime",
                "m_MulHitLowThreshold", "m_MulHitMidThreshold", "m_MulHitHighThreshold",
                "m_IsPlayBossHitAnimatorState");
        }
        if (component.TryCast<LongPressNoteVisibleController>() is { } visible) _holdVisibility.Add(visible);
    }

    // These are explicit native field-backed properties, not arbitrary component
    // properties or Init calls. Boxed IL2CPP value wrappers retain their own copy.
    private void CaptureFields(object target, params string[] names)
    {
        foreach (var name in names)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(target.GetType().FullName, name);
            if (!property.CanRead || !property.CanWrite) throw new InvalidOperationException($"Read-only reset field: {name}");
            _fields.Add(new FieldState(target, property, property.GetValue(target)));
        }
    }

    private static void KillTweens(Il2CppSystem.Collections.Generic.List<Tweener>? tweens)
    {
        if (tweens is null) return;
        for (var i = tweens.Count - 1; i >= 0; i--)
            if (tweens[i] is { } tween) TweenExtensions.Kill(tween, false);
        tweens.Clear();
    }

    private static void StopParticles(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ParticleSystem>? particles)
    {
        if (particles is null) return;
        foreach (var particle in particles)
            if (particle) particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void RemoveNoteUpdates()
    {
        var database = DataHelperScene();
        if (database is null) return;
        Remove(database.m_Updates, _initialUpdates);
        Remove(database.m_FixedUpdates, _initialFixedUpdates);
        void Remove(Il2CppSystem.Collections.Generic.List<Il2CppAssets.Scripts.GameCore.Interface.IUpdate>? updates,
            List<Il2CppAssets.Scripts.GameCore.Interface.IUpdate> initial)
        {
            if (updates is null) return;
            for (var i = updates.Count - 1; i >= 0; i--)
                if (updates[i] is { } update && _componentPointers.Contains(update.Pointer.ToInt64())) updates.RemoveAt(i);
            foreach (var update in initial) updates.Add(update);
        }
    }

    private static Il2CppAssets.Scripts.Database.DBScene? DataHelperScene() =>
        Il2CppAssets.Scripts.Database.GlobalDataBase.dbScene;

    private static List<GameObject> NoteRoots(GameMusicScene scene)
    {
        var result = new List<GameObject>();
        var seen = new HashSet<int>();
        void Add(GameObject? obj) { if (obj is not null && obj && seen.Add(obj.GetInstanceID())) result.Add(obj); }
        if (scene.preloads is not null) foreach (var obj in scene.preloads) Add(obj);
        if (scene.preloads1 is not null)
            foreach (var list in scene.preloads1)
                if (list is not null) foreach (var obj in list) Add(obj);
        return result;
    }

    private sealed record FieldState(object Target, PropertyInfo Property, object? Value)
    {
        public void Restore() => Property.SetValue(Target, Value);
    }

    private sealed class TransformState
    {
        public readonly Transform Transform;
        public readonly Transform? Parent;
        private readonly bool _hadParent;
        public bool ParentIsIntact => !_hadParent || Parent;
        public readonly Vector3 Position;
        private readonly Quaternion _rotation;
        private readonly Vector3 _scale;
        public readonly bool Active;
        public TransformState(Transform transform)
        {
            Transform = transform;
            Parent = transform.parent;
            _hadParent = Parent;
            Position = transform.localPosition;
            _rotation = transform.localRotation;
            _scale = transform.localScale;
            Active = transform.gameObject.activeSelf;
        }
        public void RestorePose()
        {
            if (Transform.parent != Parent) Transform.SetParent(Parent, false);
            Transform.localPosition = Position;
            Transform.localRotation = _rotation;
            Transform.localScale = _scale;
        }
    }

    private sealed record AnimatorState(Animator Animator, float Speed, bool Enabled)
    {
        public AnimatorState(Animator animator) : this(animator, animator.speed, animator.enabled) { }
    }

    private sealed record MaterialState(Material Material, Color Color)
    {
        public MaterialState(Material material) : this(material, material.color) { }
        public void Restore() => Material.color = Color;
    }

    private sealed class RendererState
    {
        public readonly Renderer Renderer;
        private readonly bool _enabled;
        private readonly int _sortingOrder;
        private readonly SpriteRenderer? _sprite;
        private readonly Color _color;
        public RendererState(Renderer renderer)
        {
            Renderer = renderer;
            _enabled = renderer.enabled;
            _sortingOrder = renderer.sortingOrder;
            _sprite = renderer.TryCast<SpriteRenderer>();
            _color = _sprite is null ? default : _sprite.color;
        }
        public void Restore()
        {
            Renderer.enabled = _enabled;
            Renderer.sortingOrder = _sortingOrder;
            if (_sprite is not null) _sprite.color = _color;
        }
    }
}
