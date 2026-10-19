using Il2Cpp;
using Il2CppDG.Tweening;
using Il2CppDG.Tweening.Core;
using Il2CppDG.Tweening.Plugins.Options;
using UnityEngine;

namespace MuseDashPracticePrototype;

// Hold ribbons retain clipping and miss opacity in shader floats. Restoring
// Material.color alone cannot undo either change. These property names come
// from this supported game's Clip, SetAlpha, SetLength and NoteTransparency.
// Preserve their pre-play values, including each ribbon's own length ratio.
internal sealed class HoldMaterialState
{
    private static readonly string[] FloatNames =
        { "_ClipValue", "_Alpha", "_LengthRatio", "_GradientFadeClip", "_GradientWidth" };
    public Material Material { get; }
    private readonly Shader _shader;
    private readonly List<(int Id, float Value)> _floats = new();
    private readonly Color? _color;
    private readonly string[] _keywords;
    public int FloatCount => _floats.Count;

    public HoldMaterialState(Material material)
    {
        Material = material;
        _shader = material.shader;
        if (!_shader) throw new InvalidOperationException("Hold material has no shader");
        _keywords = material.shaderKeywords.ToArray();
        if (material.HasProperty("_Color")) _color = material.GetColor("_Color");
        foreach (var name in FloatNames)
        {
            if (!material.HasProperty(name)) continue;
            var id = Shader.PropertyToID(name);
            _floats.Add((id, material.GetFloat(id)));
        }
    }

    public void Restore()
    {
        if (!Material || Material.shader != _shader)
            throw new InvalidOperationException("Hold material changed since preload");
        // NoteTransparency also enables a shader keyword on the ribbon.
        Material.shaderKeywords = _keywords;
        foreach (var (id, value) in _floats) Material.SetFloat(id, value);
        if (_color is { } color) Material.SetColor("_Color", color);
        foreach (var (id, value) in _floats)
            if (!Material.GetFloat(id).Equals(value))
                throw new InvalidOperationException("Hold shader value was not restored");
    }
}

internal static class HoldFadeReset
{
    public static int Cancel(IEnumerable<SpineActionController> actions)
    {
        var owners = actions.Where(action => action.actionMode == 12)
            .Select(action => action.Pointer.ToInt64()).ToHashSet();
        if (owners.Count == 0) return 0;

        // Native SetAlpha uses DOTween.To without a target. DOKill(material) or
        // DOKill(renderer) does not find those fades. Inspect both active groups,
        // then kill only callbacks bound to this chart's hold action controllers.
        var pending = new List<Tween>();
        Collect(DOTween.PlayingTweens(null));
        Collect(DOTween.PausedTweens(null));
        foreach (var tween in pending) TweenExtensions.Kill(tween, false);
        return pending.Count;

        void Collect(Il2CppSystem.Collections.Generic.List<Tween>? tweens)
        {
            if (tweens is null) return;
            foreach (var tween in tweens)
            {
                if (tween is null || !TweenExtensions.IsActive(tween)) continue;
                var owner = tween.TryCast<TweenerCore<float, float, FloatOptions>>()?.setter?.Target
                    ?? tween.TryCast<TweenerCore<Color, Color, ColorOptions>>()?.setter?.Target;
                if (owner is not null && owners.Contains(owner.Pointer.ToInt64())) pending.Add(tween);
            }
        }
    }
}
