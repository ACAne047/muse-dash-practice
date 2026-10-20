using System.Globalization;
using System.Text.Json.Nodes;
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
    try { action(); }
    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException) { rejected = true; }
    Check(rejected, reason);
}
var fixtureParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
var root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Subdir(string name) => Path.Combine(root, name);
PresetChart Chart(string uid = "999-1", int difficulty = 2, string? fingerprint = null, string title = "测试曲目") => new()
{
    Title = title, Author = "作者", Difficulty = difficulty, LastSeenUid = uid, DurationMs = 120000,
    FingerprintVersion = 1, Fingerprint = fingerprint ?? "sha256:" + new string('a', 64)
};
string FileFor(string directory, string id) => Path.Combine(directory, id + ".json");
var values = new PresetValues(72400, 98100, 3000);
var chart = Chart();

try
{
    var notes = new[] { new ChartNote(2m, "hold", 1, 3m, true, false), new ChartNote(5m, "holdEnd", 1, 0, false, true) };
    var hash = ChartFingerprint.Compute(notes, 120000);
    Check(hash.Length == 71 && hash.StartsWith("sha256:"), "Fingerprint format");
    Check(hash == ChartFingerprint.Compute(notes.Reverse(), 120000), "Enumeration order changed fingerprint");
    var oldCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        Check(hash == ChartFingerprint.Compute(notes, 120000), "Fingerprint depends on locale");
        Check(values.AText == "72.4" && PresetValues.Parse("72.4", "98.1", "3", 120000) == values, "Locale changed saved times");
    }
    finally { CultureInfo.CurrentCulture = oldCulture; }
    foreach (var changed in new[]
    {
        notes.Select((n, i) => i == 0 ? n with { Time = 2.01m } : n),
        notes.Select((n, i) => i == 0 ? n with { Lane = 0 } : n),
        notes.Select((n, i) => i == 0 ? n with { Type = "gear" } : n),
        notes.Select((n, i) => i == 0 ? n with { Length = 4m } : n),
        notes.Select((n, i) => i == 0 ? n with { HoldStart = false } : n)
    }) Check(hash != ChartFingerprint.Compute(changed, 120000), "Changed chart data kept fingerprint");
    Check(hash != ChartFingerprint.Compute(notes, 120100), "Audio duration omitted from fingerprint");
    Reject(() => ChartFingerprint.Compute(Array.Empty<ChartNote>(), 120000), "Empty chart accepted");

    Check(PresetValues.Parse("0", "1", "0", 120000) == new PresetValues(0, 1000, 0), "Default A / zero lead-in rejected");
    Check(PresetValues.Parse("1.2345", "3.0004", "0.0005", 120000) == new PresetValues(1235, 3000, 1), "Milliseconds must round consistently");
    foreach (var invalid in new[] { "", "NaN", "Infinity", "-0.001", "1e100", "abc", "1,5" })
    {
        Reject(() => PresetValues.Parse(invalid, "90", "3", 120000), "Invalid A accepted: " + invalid);
        Reject(() => PresetValues.Parse("0", invalid, "3", 120000), "Invalid B accepted: " + invalid);
        Reject(() => PresetValues.Parse("0", "90", invalid, 120000), "Invalid lead-in accepted: " + invalid);
    }
    Reject(() => PresetValues.Parse("20", "20.5", "3", 120000), "Too short range accepted");
    Reject(() => PresetValues.Parse("0", "0.9996", "3", 120000), "Rounding bypassed minimum range length");
    Reject(() => PresetValues.Parse("0", "120", "3", 120000), "Range beyond playable end accepted");
    Reject(() => PresetValues.Parse("119", "120", "3", 120000), "A beyond playable end accepted");

    var directory = Subdir("normal");
    var store = new PresetStore(directory);
    Check(store.Read(chart).Entries.Count == 0 && !Directory.Exists(directory), "Reading unsaved chart created files");
    var first = store.SaveNew(chart, "副歌连打", values);
    var path = FileFor(directory, first.DocumentId);
    var bytesBeforeRead = File.ReadAllBytes(path);
    Check(Guid.TryParseExact(first.DocumentId, "N", out _) && Guid.TryParseExact(first.Preset.Id, "N", out _), "UUID identities missing");
    Check(File.ReadAllText(path).Contains("副歌连打") && File.ReadAllText(path).Contains("\"aMs\": 72400"), "JSON must be readable Unicode and integer milliseconds");
    Check(!File.Exists(path + ".bak"), "Initial save made a misleading backup");
    Check(new PresetStore(directory).Read(Chart("999-88")).Entries.Single().Exact, "Changing temporary UID lost the preset");
    Check(File.ReadAllBytes(path).SequenceEqual(bytesBeforeRead), "Reading wrote user data");
    Check(store.Read(Chart(difficulty: 1)).Entries.Count == 0, "Difficulty isolation failed");
    Check(store.Read(Chart(title: "另一首曲目")).Entries.Count == 0, "Title isolation failed");
    var otherAuthor = Chart(); otherAuthor.Author = "其他作者";
    Check(store.Read(otherAuthor).Entries.Count == 0, "Author isolation failed");
    var altered = Chart(fingerprint: "sha256:" + new string('b', 64));
    Check(!store.Read(altered).Entries.Single().Exact, "Changed revision automatically matched");
    Reject(() => store.Update(altered, first.DocumentId, first.Preset.Id, "wrong", values), "Old revision overwritten without migration");
    Reject(() => store.Delete(altered, first.DocumentId, first.Preset.Id), "Old revision deleted from new chart");

    var second = store.SaveNew(chart, "结尾", new(100000, 110000, 1500));
    Check(second.DocumentId == first.DocumentId && second.Preset.Id != first.Preset.Id, "Presets should share chart file but have distinct IDs");
    Check(File.ReadAllBytes(path + ".bak").SequenceEqual(bytesBeforeRead), "Backup is not the exact previous file");
    var updated = store.Update(chart, first.DocumentId, first.Preset.Id, "副歌修订", new(73000, 98000, 0));
    Check(updated.Preset.Id == first.Preset.Id && updated.Preset.Name == "副歌修订" && updated.Preset.LeadInMs == 0, "Update/rename lost identity or values");
    Check(store.Read(chart).Entries.Count == 2, "Update duplicated a preset");
    foreach (var invalidName in new[] { "", "   ", new string('x', 65), "bad\nname" })
        Reject(() => store.SaveNew(chart, invalidName, values), "Invalid name accepted");
    var originalHash = File.ReadAllBytes(path);
    var copied = store.CopyTo(altered, first.DocumentId, first.Preset.Id);
    Check(copied.DocumentId != first.DocumentId && copied.Preset.Id != first.Preset.Id && copied.Exact, "Migration must make new identities");
    Check(File.ReadAllBytes(path).SequenceEqual(originalHash), "Migration changed the source file");
    Check(store.Read(altered).Entries.Count(e => e.Exact) == 1 && store.Read(chart).Entries.Count(e => e.Exact) == 2, "Revision isolation failed after copy");
    store.Delete(chart, first.DocumentId, first.Preset.Id);
    Check(store.Read(chart).Entries.Count(e => e.Exact) == 1, "Delete removed the wrong entries");
    store.Delete(chart, second.DocumentId, second.Preset.Id);
    Check(store.Read(chart).Entries.All(e => !e.Exact), "Backup resurrected deleted presets");
    Check(File.Exists(path), "Empty primary must remain to prevent backup resurrection");
    var third = store.SaveNew(chart, "新段落", values);
    Check(third.DocumentId == first.DocumentId, "An empty chart file was not reused");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary writes leaked");
    Reject(() => store.Delete(chart, "../../outside", third.Preset.Id), "Path traversal accepted");
    if (OperatingSystem.IsWindows())
    {
        var beforeFailedWrite = File.ReadAllBytes(path);
        var beforeFailedBackup = File.ReadAllBytes(path + ".bak");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => store.Update(chart, third.DocumentId, third.Preset.Id, "locked", values), "Replacement ignored an exclusive-delete lock");
        Check(File.ReadAllBytes(path).SequenceEqual(beforeFailedWrite) && File.ReadAllBytes(path + ".bak").SequenceEqual(beforeFailedBackup),
            "Failed atomic replacement damaged the primary or backup");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed write leaked temporary file");
    }

    var imported = JsonNode.Parse(File.ReadAllText(path))!;
    var importId = Guid.NewGuid().ToString("N");
    imported["id"] = importId;
    File.WriteAllText(FileFor(directory, importId), imported.ToJsonString());
    Reject(() => store.SaveNew(chart, "ambiguous", values), "Ambiguous imported files silently chosen");
    Check(store.SaveNew(chart, "chosen", values, third.DocumentId).DocumentId == third.DocumentId, "Explicit file selection not respected");

    var recoveryDir = Subdir("recovery");
    var recovery = new PresetStore(recoveryDir);
    var saved = recovery.SaveNew(chart, "original", values);
    recovery.Update(chart, saved.DocumentId, saved.Preset.Id, "updated", values);
    var damaged = FileFor(recoveryDir, saved.DocumentId);
    var previous = File.ReadAllBytes(damaged + ".bak");
    File.WriteAllText(damaged, "{broken");
    var recovered = recovery.Read(chart);
    Check(recovered.Entries.Single().Recovered && recovered.Entries.Single().Preset.Name == "original", "Backup fallback failed");
    Check(recovered.Warnings.Count == 1, "Recovery must report a warning");
    Reject(() => recovery.Update(chart, saved.DocumentId, saved.Preset.Id, "oops", values), "Damaged primary overwritten");
    Reject(() => recovery.Delete(chart, saved.DocumentId, saved.Preset.Id), "Recovered entry deleted in place");
    var recoveredCopy = recovery.CopyTo(chart, saved.DocumentId, saved.Preset.Id);
    Check(recoveredCopy.DocumentId != saved.DocumentId, "Backup copy overwrote the damaged primary");
    Check(File.ReadAllText(damaged) == "{broken" && File.ReadAllBytes(damaged + ".bak").SequenceEqual(previous), "Recovery destroyed evidence");
    File.Delete(damaged);
    Check(recovery.Read(chart).Entries.Any(e => e.DocumentId == saved.DocumentId && e.Recovered), "Orphan backup not found");

    var futureDir = Subdir("future");
    var future = new PresetStore(futureDir);
    var futureEntry = future.SaveNew(chart, "future", values);
    var futureFile = FileFor(futureDir, futureEntry.DocumentId);
    File.Copy(futureFile, futureFile + ".bak");
    var json = JsonNode.Parse(File.ReadAllText(futureFile))!;
    json["schemaVersion"] = 99;
    File.WriteAllText(futureFile, json.ToJsonString());
    Check(future.Read(chart).Entries.Count == 0 && future.Read(chart).Warnings.Count == 1, "Future schema incorrectly downgraded to backup");
    Reject(() => future.Update(chart, futureEntry.DocumentId, futureEntry.Preset.Id, "overwrite", values), "Future schema overwritten");
    Check(JsonNode.Parse(File.ReadAllText(futureFile))!["schemaVersion"]!.GetValue<int>() == 99, "Future schema changed");
    json["schemaVersion"] = 1;
    json["chart"]!["fingerprintVersion"] = 99;
    File.WriteAllText(futureFile, json.ToJsonString());
    Check(future.Read(chart).Entries.Count == 0 && future.Read(chart).Warnings.Count == 1, "Future fingerprint incorrectly downgraded to backup");

    var invalidDir = Subdir("invalid");
    var invalidStore = new PresetStore(invalidDir);
    var invalidEntry = invalidStore.SaveNew(chart, "valid", values);
    var invalidFile = FileFor(invalidDir, invalidEntry.DocumentId);
    json = JsonNode.Parse(File.ReadAllText(invalidFile))!;
    json["presets"]![0]!["bMs"] = 1;
    File.WriteAllText(invalidFile, json.ToJsonString());
    Check(invalidStore.Read(chart).Entries.Count == 0 && invalidStore.Read(chart).Warnings.Count == 1, "Invalid stored range was offered");
    File.WriteAllText(invalidFile, new string(' ', 4 * 1024 * 1024 + 1));
    Check(invalidStore.Read(chart).Warnings.Count == 1, "Oversized document accepted");

    var range = new PracticeRange();
    Check(range.A == 0 && range.B is null && !range.IsRunning, "Preset storage changed default range state");
    range.Apply("20", "30", "3", out _);
    range.Suspend();
    Check(!range.CanRepeat && !range.IsRunning, "Preset selection must suspend the prior loop");
    Check(range.Apply(values.AText, values.BText, values.LeadText, out _) && range.CanRepeat, "Loaded values cannot be started through F9 path");
    Console.WriteLine($"PASS: {checks} preset assertions (storage, identity, revision migration, recovery, validation; no game runtime).");
}
finally
{
    // The fixture is a fresh GUID directory beneath our fixed temporary root.
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == fixtureParent) Directory.Delete(root, true);
}
