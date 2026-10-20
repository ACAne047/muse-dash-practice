using System.Reflection;

namespace MuseDashPracticePrototype;

internal static class BoundaryPoseClock
{
    // Input is SOURCE-song time. Explicit animation sampling must not multiply
    // by playback speed again or clamp a nearly-arrived note to its first frame.
    public static float Elapsed(float songTime, float showTime)
    {
        if (!float.IsFinite(songTime) || !float.IsFinite(showTime))
            throw new ArgumentOutOfRangeException(nameof(songTime));
        return (float)Math.Clamp((double)songTime - showTime, 0, float.MaxValue);
    }
}

// Suppress callbacks only on the restored note's animation state and tracks.
// Native queue draining/pool cleanup remains active; do not restore listeners
// onto tracks that have been disposed while crossing queued clip boundaries.
internal sealed class ScopedCallbackMute : IDisposable
{
    private static readonly string[] Names = { "Start", "Interrupt", "End", "Dispose", "Complete", "Event" };
    private readonly List<(object Owner, PropertyInfo Property, object Value)> _saved = new();
    private readonly Func<object, bool> _stillLive;
    private bool _disposed;

    public ScopedCallbackMute(IEnumerable<object> owners, Func<object, bool> stillLive)
    {
        _stillLive = stillLive;
        try
        {
            foreach (var owner in owners)
                foreach (var name in Names)
                {
                    var property = owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                        ?? throw new MissingMemberException(owner.GetType().FullName, name);
                    if (!property.CanRead || !property.CanWrite) throw new InvalidOperationException("Read-only animation callback: " + name);
                    if (property.GetValue(owner) is not { } value) continue;
                    _saved.Add((owner, property, value));
                    property.SetValue(owner, null);
                }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        for (var i = _saved.Count - 1; i >= 0; i--)
        {
            var saved = _saved[i];
            try { if (_stillLive(saved.Owner)) saved.Property.SetValue(saved.Owner, saved.Value); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Animation callback restoration failed", failures);
    }
}
