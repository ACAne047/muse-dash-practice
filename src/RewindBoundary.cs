namespace MuseDashPracticePrototype;

// One successful seek owns one [target, A) window. Passing A consumes it, so
// merely editing parameters or moving clocks cannot re-arm protection.
internal sealed class LeadInWindow
{
    private float _start;
    private float _end;
    private bool _armed;

    public void Begin(float target, float a)
    {
        if (!float.IsFinite(target) || !float.IsFinite(a) || target < 0 || a < target)
            throw new ArgumentOutOfRangeException(nameof(target));
        _start = target;
        _end = a;
        _armed = target < a;
    }

    public void Cancel() => _armed = false;

    public bool Contains(float songTime)
    {
        if (!_armed) return false;
        if (!float.IsFinite(songTime) || songTime >= _end) { Cancel(); return false; }
        return songTime >= _start;
    }
}

internal readonly record struct RewindNoteWindow(int Id, double Show, double Miss, double Duration, int EndId = -1);

internal sealed record RewindBoundaryPlan(int LastSkipped, int[] RestoreIds)
{
    public static RewindBoundaryPlan Build(IEnumerable<RewindNoteWindow> source, double target)
    {
        if (!double.IsFinite(target) || target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        var notes = new Dictionary<int, RewindNoteWindow>();
        foreach (var note in source)
            if (note.Id >= 0 && double.IsFinite(note.Show) && double.IsFinite(note.Miss)
                && double.IsFinite(note.Duration)) notes[note.Id] = note;
        var cursor = 0;
        var restore = new SortedSet<int>();
        foreach (var note in notes.Values)
        {
            if (note.Show > target) continue;
            cursor = Math.Max(cursor, note.Id);
            var end = note.Miss + Math.Max(0, note.Duration);
            // Native hold heads refer to a separately judged tail. A head may
            // already be past its own hit window while its body is still live.
            if (note.EndId > note.Id && notes.TryGetValue(note.EndId, out var tail)) end = Math.Max(end, tail.Miss);
            if (end >= target) restore.Add(note.Id);
        }
        return new RewindBoundaryPlan(cursor, restore.ToArray());
    }
}
