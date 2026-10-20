using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuseDashPracticePrototype;

internal sealed record ChartNote(decimal Time, string Type, int Lane, decimal Length, bool HoldStart, bool HoldEnd);

internal static class ChartFingerprint
{
    public const int Version = 1;

    public static string Compute(IEnumerable<ChartNote> notes, int durationMs)
    {
        // Only immutable chart data: no spawn times, character speed, generated IDs,
        // score, held state, native pointers, or CustomAlbums registration numbers.
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write(Version);
            writer.Write(durationMs);
            var ordered = notes.OrderBy(n => n.Time).ThenBy(n => n.Lane)
                .ThenBy(n => n.Type, StringComparer.Ordinal).ThenBy(n => n.Length)
                .ThenBy(n => n.HoldStart).ThenBy(n => n.HoldEnd).ToArray();
            if (ordered.Length == 0) throw new InvalidDataException("The chart has no notes.");
            writer.Write(ordered.Length);
            foreach (var note in ordered)
            {
                writer.Write(note.Time.ToString("G29", CultureInfo.InvariantCulture));
                writer.Write(note.Type);
                writer.Write(note.Lane);
                writer.Write(note.Length.ToString("G29", CultureInfo.InvariantCulture));
                writer.Write(note.HoldStart);
                writer.Write(note.HoldEnd);
            }
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }
}

internal sealed class PresetChart
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public int Difficulty { get; set; }
    public string LastSeenUid { get; set; } = "";
    public int DurationMs { get; set; }
    public int FingerprintVersion { get; set; }
    public string Fingerprint { get; set; } = "";

    private static string Normalize(string text) => text.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    public bool SameSong(PresetChart other) => Difficulty == other.Difficulty
        && Normalize(Title) == Normalize(other.Title) && Normalize(Author) == Normalize(other.Author);
    public bool SameRevision(PresetChart other) => SameSong(other) && DurationMs == other.DurationMs
        && FingerprintVersion == other.FingerprintVersion && Fingerprint == other.Fingerprint;
}

internal readonly record struct PresetValues(int AMs, int BMs, int LeadInMs)
{
    public string AText => Seconds(AMs);
    public string BText => Seconds(BMs);
    public string LeadText => Seconds(LeadInMs);
    private static string Seconds(int ms) => (ms / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    public void Validate(int durationMs)
    {
        if (AMs < 0 || (long)BMs - AMs < 1000 || LeadInMs < 0
            || AMs >= (long)durationMs - 1000 || BMs > (long)durationMs - 250)
            throw new InvalidDataException("A >= 0; B >= A + 1s; lead-in >= 0; B must be below the song end.");
    }

    public static PresetValues Parse(string a, string b, string lead, int durationMs)
    {
        static decimal Seconds(string text)
        {
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || value < 0 || value > int.MaxValue / 1000m)
                throw new InvalidDataException("Enter valid A, B and lead-in values in seconds.");
            return value;
        }
        var aSeconds = Seconds(a);
        var bSeconds = Seconds(b);
        var leadSeconds = Seconds(lead);
        if (bSeconds - aSeconds < 1 || aSeconds * 1000 >= (long)durationMs - 1000 || bSeconds * 1000 > (long)durationMs - 250)
            throw new InvalidDataException("Range must be at least one second and stay below the song end.");
        static int Milliseconds(decimal value) => decimal.ToInt32(decimal.Round(value * 1000m, 0, MidpointRounding.AwayFromZero));
        var result = new PresetValues(Milliseconds(aSeconds), Milliseconds(bSeconds), Milliseconds(leadSeconds));
        result.Validate(durationMs);
        return result;
    }
}

internal sealed class RangePreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int AMs { get; set; }
    public int BMs { get; set; }
    public int LeadInMs { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public PresetValues Values() => new(AMs, BMs, LeadInMs);
}

internal sealed class PresetDocument
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public PresetChart Chart { get; set; } = new();
    public List<RangePreset> Presets { get; set; } = new();
}

internal sealed record PresetEntry(string DocumentId, PresetChart Chart, RangePreset Preset, bool Exact, bool Recovered);
internal sealed record PresetFile(PresetDocument Document, bool Recovered);
internal sealed record PresetCatalog(List<PresetEntry> Entries, List<string> Warnings, List<PresetFile> Files);

internal sealed class PresetStore
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxPresets = 1000;
    private readonly string _directory;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PresetStore(string directory) => _directory = Path.GetFullPath(directory);

    public PresetCatalog Read(PresetChart current)
    {
        ValidateChart(current);
        var entries = new List<PresetEntry>();
        var warnings = new List<string>();
        var files = new List<PresetFile>();
        if (!Directory.Exists(_directory)) return new(entries, warnings, files);
        // Include a surviving backup when the primary was lost. Temporary writes
        // and arbitrary filenames are never treated as preset documents.
        var ids = Directory.EnumerateFiles(_directory, "*.json*")
            .Select(Path.GetFileName).Where(name => name is not null)
            .Select(name => name!.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase) ? name[..^9]
                : name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : "")
            .Where(IsId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            try
            {
                var (document, recovered) = Load(id);
                files.Add(new(document, recovered));
                if (recovered) warnings.Add($"{id}: reading backup; the original file is preserved and read-only.");
                if (!document.Chart.SameSong(current)) continue;
                entries.AddRange(document.Presets.Select(p => new PresetEntry(id, document.Chart, p,
                    document.Chart.SameRevision(current), recovered)));
            }
            catch (Exception error) when (IsFileError(error)) { warnings.Add($"{id}: {error.Message}"); }
        }
        return new(entries.OrderByDescending(e => e.Exact).ThenBy(e => e.Preset.Name, StringComparer.OrdinalIgnoreCase).ToList(), warnings, files);
    }

    public PresetEntry SaveNew(PresetChart chart, string name, PresetValues values, string? preferredDocument = null)
    {
        ValidateChart(chart);
        name = ValidateName(name);
        values.Validate(chart.DurationMs);
        var catalog = Read(chart);
        var ids = catalog.Files.Where(f => !f.Recovered && f.Document.Chart.SameRevision(chart))
            .Select(f => f.Document.Id).Distinct().ToArray();
        var chosen = preferredDocument is not null && ids.Contains(preferredDocument) ? preferredDocument
            : ids.Length == 1 ? ids[0] : null;
        if (chosen is null && ids.Length > 1) throw new InvalidDataException("Multiple matching files: select a preset in the target file first.");
        var document = chosen is null ? new PresetDocument { SchemaVersion = 1, Id = Guid.NewGuid().ToString("N"), Chart = chart }
            : LoadWritable(chosen, chart);
        var preset = new RangePreset { Id = Guid.NewGuid().ToString("N"), Name = name };
        SetValues(preset, values);
        document.Presets.Add(preset);
        document.Chart = chart;
        Write(document);
        return new(document.Id, chart, preset, true, false);
    }

    public PresetEntry Update(PresetChart chart, string documentId, string presetId, string name, PresetValues values)
    {
        name = ValidateName(name);
        values.Validate(chart.DurationMs);
        var document = LoadWritable(documentId, chart);
        var preset = Find(document, presetId);
        preset.Name = name;
        SetValues(preset, values);
        document.Chart = chart;
        Write(document);
        return new(documentId, chart, preset, true, false);
    }

    public void Delete(PresetChart chart, string documentId, string presetId)
    {
        var document = LoadWritable(documentId, chart);
        document.Presets.Remove(Find(document, presetId));
        // Keep an empty primary: deleting the file would make its .bak reappear.
        Write(document);
    }

    public PresetEntry CopyTo(PresetChart current, string sourceDocumentId, string sourcePresetId, string? preferredDocument = null)
    {
        var (source, _) = Load(sourceDocumentId);
        var preset = Find(source, sourcePresetId);
        return SaveNew(current, preset.Name, preset.Values(), preferredDocument);
    }

    private static void SetValues(RangePreset preset, PresetValues values)
    {
        preset.AMs = values.AMs;
        preset.BMs = values.BMs;
        preset.LeadInMs = values.LeadInMs;
        preset.UpdatedUtc = DateTime.UtcNow;
    }

    private static RangePreset Find(PresetDocument document, string id) =>
        document.Presets.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("This preset was removed; refresh the list.");
    private static bool IsId(string id) => Guid.TryParseExact(id, "N", out _);
    private string PathFor(string id) => IsId(id) ? Path.Combine(_directory, id + ".json")
        : throw new InvalidDataException("Invalid preset document ID.");
    private static bool IsFileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException;

    private (PresetDocument Document, bool Recovered) Load(string id)
    {
        var path = PathFor(id);
        try { return (ReadFile(path, id), false); }
        // A newer schema is deliberately NOT replaced by an older backup.
        catch (Exception error) when (IsFileError(error) && error is not NotSupportedException)
        {
            if (!File.Exists(path + ".bak")) throw;
            return (ReadFile(path + ".bak", id), true);
        }
    }

    private PresetDocument LoadWritable(string id, PresetChart chart)
    {
        var (document, recovered) = Load(id);
        if (recovered) throw new InvalidDataException("Recovered backup is read-only. Use Save new to preserve the damaged file.");
        if (!document.Chart.SameRevision(chart)) throw new InvalidDataException("Chart revision changed. Confirm a copy to this chart instead.");
        return document;
    }

    private static PresetDocument ReadFile(string path, string expectedId)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Preset file exceeds 4 MiB.");
        var document = JsonSerializer.Deserialize<PresetDocument>(stream, Json) ?? throw new InvalidDataException("Empty preset document.");
        ValidateDocument(document);
        if (document.Id != expectedId) throw new InvalidDataException("Document ID must match the filename.");
        return document;
    }

    private void Write(PresetDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Preset file exceeds 4 MiB.");
        Directory.CreateDirectory(_directory);
        var path = PathFor(document.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64 || name.Any(char.IsControl))
            throw new InvalidDataException("Preset name must contain 1-64 characters, without control characters.");
        return name.Trim();
    }

    private static void ValidateChart(PresetChart chart)
    {
        if (chart is not null && chart.FingerprintVersion != ChartFingerprint.Version)
            throw new NotSupportedException("Unsupported chart fingerprint version; file preserved.");
        if (chart is null || string.IsNullOrWhiteSpace(chart.Title) || chart.Title.Length > 512
            || chart.Author is null || chart.Author.Length > 512 || chart.LastSeenUid is null || chart.LastSeenUid.Length > 512
            || chart.Difficulty < 0 || chart.DurationMs <= 1000
            || chart.Fingerprint is null || chart.Fingerprint.Length != 71 || !chart.Fingerprint.StartsWith("sha256:", StringComparison.Ordinal)
            || chart.Fingerprint[7..].Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid or unsupported chart identity.");
    }

    private static void ValidateDocument(PresetDocument document)
    {
        if (document.SchemaVersion != 1) throw new NotSupportedException("Unsupported preset schema; file preserved.");
        if (!IsId(document.Id) || document.Presets is null || document.Presets.Count > MaxPresets)
            throw new InvalidDataException("Invalid preset document.");
        ValidateChart(document.Chart);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in document.Presets)
        {
            if (preset is null || !IsId(preset.Id) || !ids.Add(preset.Id)) throw new InvalidDataException("Invalid or duplicate preset ID.");
            _ = ValidateName(preset.Name);
            preset.Values().Validate(document.Chart.DurationMs);
        }
    }
}
