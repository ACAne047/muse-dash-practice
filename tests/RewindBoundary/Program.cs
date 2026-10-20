using MuseDashPracticePrototype;

var checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition) throw new Exception(reason);
}
void Reject(Action action, string reason)
{
    var rejected = false;
    try { action(); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, reason);
}

var lead = new LeadInWindow();
Check(!lead.Contains(8), "Enabling practice or marking a range must not arm protection");
lead.Begin(7, 10);
Check(!lead.Contains(6.99f), "Protection started before the actual seek position");
Check(lead.Contains(7) && lead.Contains(9.999f), "Lead-in should protect the complete [target, A) interval");
for (var frame = 0; frame < 10; frame++)
    Check(lead.Contains(8), "Pausing at the same song time consumed wall-time protection");
Check(!lead.Contains(10) && !lead.Contains(10.01f), "Protection persisted at or after A");
Check(!lead.Contains(8), "A consumed lead-in was rearmed by an unexpected clock change");
foreach (var (target, a) in new[] { (0f, 0f), (10f, 10f), (0f, 2f), (8.5f, 10f) })
{
    lead.Begin(target, a);
    Check(lead.Contains(target) == (target < a), "Zero/clamped/fractional lead-in boundary was incorrect");
    Check(!lead.Contains(a), "A must always end protection, including A=0");
}
lead.Begin(7, 10);
lead.Cancel();
Check(!lead.Contains(8), "F7, stop, parameter pause or a new chart must cancel the old window");
foreach (var rate in new[] { .5f, .7f, 1f, 1.25f })
{
    lead.Begin(7, 10);
    var realLead = 3 / rate;
    Check(lead.Contains(7 + (realLead - .001f) * rate), "Protection duration did not follow source-song speed");
    Check(!lead.Contains(7 + realLead * rate), "Slower playback extended protection past source-song A");
}
for (var round = 0; round < 100; round++)
{
    lead.Begin(57, 60);
    Check(lead.Contains(57) && !lead.Contains(60), "Repeated loops lost their own lead-in boundary");
}
lead.Begin(7, 10);
Check(!lead.Contains(float.NaN) && !lead.Contains(8), "Invalid clocks left protection armed");
Reject(() => lead.Begin(-1, 10), "Negative seek target was accepted");
Reject(() => lead.Begin(10, 9), "Reversed protection interval was accepted");
Reject(() => lead.Begin(float.NaN, 10), "Invalid protection target was accepted");

var notes = new[]
{
    new RewindNoteWindow(1, 1, 2.1, 0),       // Fully expired.
    new RewindNoteWindow(2, 3, 4.1, 0, 6),    // Hold head, linked tail still ahead.
    new RewindNoteWindow(3, 4, 6.1, 0),       // Already spawned, still approaching hit.
    new RewindNoteWindow(4, 4.5, 4.6, 2),     // Burst/duration object across the cut.
    new RewindNoteWindow(5, 5, 7.1, 0),       // Exactly at target: warm once and skip its old event.
    new RewindNoteWindow(6, 6, 8.1, 0),       // Future hold tail must be generated later.
    new RewindNoteWindow(7, 7, 9.1, 0),       // Future ordinary note.
};
var plan = RewindBoundaryPlan.Build(notes, 5);
Check(plan.LastSkipped == 5, "Generation cursor no longer covers all skipped show events");
Check(plan.RestoreIds.SequenceEqual(new[] { 2, 3, 4, 5 }), "Invisible approaching notes, hold or duration object were not restored");
Check(!plan.RestoreIds.Contains(1), "Expired notes were revived");
Check(!plan.RestoreIds.Contains(6) && !plan.RestoreIds.Contains(7), "Future notes were generated early");
// Previous behavior advanced the cursor but activated no skipped objects. Object
// 3 would still be judged at 6 seconds even though its 4-second show was skipped.
Check(notes[2].Show < 5 && notes[2].Miss > 5 && plan.RestoreIds.Contains(3), "Did not cover the invisible-note regression");
Check(RewindBoundaryPlan.Build(notes.Reverse(), 5).RestoreIds.SequenceEqual(plan.RestoreIds), "Input order changed warm-start results");
Check(RewindBoundaryPlan.Build(notes.Concat(new[] { notes[1] }), 5).RestoreIds.SequenceEqual(plan.RestoreIds), "Duplicate IDs caused duplicate starts");
Check(RewindBoundaryPlan.Build(notes, 10).RestoreIds.Length == 0, "Fully expired holds or bursts were revived");
Check(RewindBoundaryPlan.Build(notes, 0).LastSkipped == 0 && RewindBoundaryPlan.Build(notes, 0).RestoreIds.Length == 0,
    "Seeking before the first note generated future content");
Check(RewindBoundaryPlan.Build(new[] { new RewindNoteWindow(1, 5, 5, 0) }, 5).RestoreIds.Single() == 1,
    "Exact show/miss boundary was dropped");
Check(RewindBoundaryPlan.Build(new[] { new RewindNoteWindow(1, 1, 2, 0, 999) }, 5).RestoreIds.Length == 0,
    "A missing tail extended a stale hold forever");
Check(RewindBoundaryPlan.Build(new[] { new RewindNoteWindow(1, 1, 2, -10) }, 1.5).RestoreIds.Single() == 1,
    "Negative optional duration shortened the native judgement lifetime");
foreach (var invalid in new[] { new RewindNoteWindow(-1, 1, 6, 0), new RewindNoteWindow(1, double.NaN, 6, 0),
    new RewindNoteWindow(1, 1, double.PositiveInfinity, 0), new RewindNoteWindow(1, 1, 6, double.NaN) })
    Check(RewindBoundaryPlan.Build(new[] { invalid }, 5).RestoreIds.Length == 0, "Invalid native window entered the restore set");
Reject(() => RewindBoundaryPlan.Build(notes, -1), "Negative warm-start target was accepted");
Reject(() => RewindBoundaryPlan.Build(notes, double.NaN), "Invalid warm-start target was accepted");
checks += PoseClockRegression.Run();
Console.WriteLine($"PASS: {checks} rewind-boundary assertions (lead-in/A, pause/speed, 100 loops, in-flight notes, pose age, callback restoration and cursors).");
