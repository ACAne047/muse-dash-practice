using MuseDashPracticePrototype;

internal static class PoseClockRegression
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
        // Reproduce the missed step from 0.3.12: a note almost at its hit point
        // was made visible at animation time zero, while judgement kept its age.
        foreach (var progress in new[] { .1f, .5f, .9f, .99f, 1f })
        foreach (var rate in new[] { .5f, .7f, 1f, 1.25f })
        {
            const float birth = 20;
            const float duration = 2;
            var wallElapsed = duration * progress / rate;
            var songNow = birth + wallElapsed * rate;
            var continuous = new LinearClip();
            continuous.Update(songNow - birth);
            var restored = new LinearClip();
            Check(Math.Abs(restored.X - continuous.X) > .05f, "Did not reproduce the zero-pose display/judgement mismatch");
            restored.Update(BoundaryPoseClock.Elapsed(songNow, birth));
            Check(Math.Abs(restored.X - continuous.X) < .0001f, "Restored pose differs from uninterrupted playback at the same song time");
        }
        Check(BoundaryPoseClock.Elapsed(1, 2) == 0, "An animation scheduled in the future advanced backwards");
        Check(BoundaryPoseClock.Elapsed(30, 20) == 10, "Catch-up was clamped to one frame or to the beginning of a clip");
        var invalid = false;
        try { BoundaryPoseClock.Elapsed(float.NaN, 0); } catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "Invalid pose clocks were accepted");

        var playedEvents = 0;
        Action callback = () => playedEvents++;
        var owned = new Callbacks(callback);
        var other = new Callbacks(callback);
        using (new ScopedCallbackMute(new[] { owned }, _ => true))
        {
            owned.Event?.Invoke(); owned.Complete?.Invoke(); owned.End?.Invoke();
            Check(playedEvents == 0, "Historical events fired while the note was sampled");
            other.Event?.Invoke();
            Check(playedEvents == 1, "Another animation state's listeners were muted");
        }
        Check(owned.AllAre(callback), "Live callback delegates were not restored");
        owned.Event?.Invoke();
        Check(playedEvents == 2, "Future gameplay events remained disabled after catch-up");

        var disposed = new Callbacks(callback);
        var live = new Callbacks(callback);
        using (new ScopedCallbackMute(new[] { disposed, live }, owner => owner != disposed)) { }
        Check(disposed.AllAre(null), "Listeners were restored onto a disposed/pooled track");
        Check(live.AllAre(callback), "A live queued track lost its listeners");
        try
        {
            using var scope = new ScopedCallbackMute(new[] { live }, _ => true);
            throw new InvalidOperationException("sample failed");
        }
        catch (InvalidOperationException) { }
        Check(live.AllAre(callback), "An exception left animation callbacks muted");
        var incomplete = new MissingEventCallback { Start = callback, Complete = callback };
        var rejected = false;
        try { using var scope = new ScopedCallbackMute(new object[] { owned, incomplete }, _ => true); }
        catch (MissingMemberException) { rejected = true; }
        Check(rejected && owned.AllAre(callback) && incomplete.Start == callback && incomplete.Complete == callback,
            "A partial callback snapshot did not unwind safely");
        return checks;
    }

    private sealed class LinearClip
    {
        private float _time;
        public float X => 10 * (1 - Math.Clamp(_time / 2, 0, 1));
        public void Update(float delta) => _time += delta;
    }
    private sealed class Callbacks
    {
        public Action? Start { get; set; }
        public Action? Interrupt { get; set; }
        public Action? End { get; set; }
        public Action? Dispose { get; set; }
        public Action? Complete { get; set; }
        public Action? Event { get; set; }
        public Callbacks(Action value) => Start = Interrupt = End = Dispose = Complete = Event = value;
        public bool AllAre(Action? value) => Start == value && Interrupt == value && End == value && Dispose == value && Complete == value && Event == value;
    }
    private sealed class MissingEventCallback
    {
        public Action? Start { get; set; }
        public Action? Interrupt { get; set; }
        public Action? End { get; set; }
        public Action? Dispose { get; set; }
        public Action? Complete { get; set; }
    }
}
