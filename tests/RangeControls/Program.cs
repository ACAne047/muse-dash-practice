using MuseDashPracticePrototype;

var checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition) throw new Exception(reason);
}

var range = new PracticeRange();
Check(range.Apply("20", "30", "3", out _), "Initial segment rejected");
range.MarkA(25);
Check(range.WaitingForB && !range.CanRepeat, "Old B must stop repeating after F7");
Check(!range.Apply("25", "", "3", out _), "F9 must not accept the old B after F7");
Check(range.WaitingForB && !range.CanRepeat, "Invalid edits must keep the wait active");
range.MarkA(35);
Check(!range.MarkB(35.5f) && range.WaitingForB, "Too-short B must not resume a loop");
Check(range.Apply("35", "45", "1.5", out _), "New B and decimal lead-in rejected");
Check(range.CanRepeat && !range.WaitingForB && range.RewindTarget == 33.5f, "New range did not resume with its lead-in");
Check(range.Apply("2", "8", "5", out _) && range.RewindTarget == 0, "Lead-in before the song must clamp to zero");
Check(range.Apply("20", "30", "0", out _) && range.RewindTarget == 20, "Zero lead-in should target A");
foreach (var invalid in new[] { "", "-1", "NaN", "Infinity", "1e100", "abc" })
{
    Check(!range.Apply("20", "30", invalid, out _), $"Accepted invalid lead-in: {invalid}");
    Check(range.A == 20 && range.B == 30 && range.LeadIn == 0, "Invalid lead-in changed the applied range");
}
foreach (var (a, b) in new[] { ("-1", "10"), ("20", "20"), ("20", "20.99"), ("NaN", "30"), ("20", "Infinity") })
    Check(!range.Apply(a, b, "3", out _), "Accepted invalid A/B");
range.MarkA(40);
Check(range.MarkB(41) && range.CanRepeat, "An exact one-second range should be valid");
Console.WriteLine($"PASS: {checks} range-control assertions (no Unity/game runtime).");
