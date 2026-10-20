using System.Globalization;
using Il2CppAssets.Scripts.Database;
using Il2CppAssets.Scripts.GameCore.HostComponent;
using Il2CppGameLogic;
using Il2CppFormulaBase;
using UnityEngine;

namespace MuseDashPracticePrototype;

public sealed partial class PracticeMod
{
    private PresetStore? _presetStore;
    private PresetChart? _presetChart;
    private readonly List<PresetEntry> _presetEntries = new();
    private PresetEntry? _selectedPreset;
    private string? _presetTargetDocument;
    private bool _presetLoadPending;
    private bool _presetsExpanded = true;
    private string _presetName = "Preset 1";
    private string _presetStatus = "Open a chart to view its saved ranges.";
    private string _presetConfirmation = "";
    private int _presetPage;
    private Vector2 _presetScroll;
    private float PanelHeight => !_panel ? 58 : !_presetsExpanded ? 386 : Math.Max(386, Math.Min(728, Screen.height - 24));

    private void ClearPresetChart()
    {
        _presetChart = null;
        _presetLoadPending = false;
        _presetEntries.Clear();
        _selectedPreset = null;
        _presetTargetDocument = null;
        _presetConfirmation = "";
        _presetPage = 0;
        _presetName = "Preset 1";
        _presetScroll = default;
        _presetStatus = "Open a chart to view its saved ranges.";
    }

    private void BeginPresetChart()
    {
        ClearPresetChart();
        _presetLoadPending = true;
        _presetStatus = "Waiting for loaded chart data...";
    }

    private void PreparePresetChart(StageBattleComponent stage, AudioClip clip)
    {
        if (!_presetLoadPending || !_ready || !_enabled || _presetStore is null || (!stage.isInGame && !stage.isPause)
            || stage.m_MusicTickData is null || stage.m_MusicTickData.Count == 0 || clip.frequency <= 0 || clip.samples <= 0) return;
        var info = BattleHelper.MusicInfo();
        if (info is null || string.IsNullOrWhiteSpace(info.name)) return;
        _presetLoadPending = false;
        try
        {
            var notes = new List<ChartNote>();
            foreach (var note in stage.m_MusicTickData)
            {
                var config = note.configData ?? throw new InvalidDataException("Missing immutable note configuration.");
                notes.Add(new ChartNote(decimal.Parse(note.tick.ToString(), CultureInfo.InvariantCulture),
                    config.note_uid ?? "", config.pathway,
                    decimal.Parse(config.length.ToString(), CultureInfo.InvariantCulture), note.isLongPressStart, note.isLongPressEnd));
            }
            var duration = checked((int)((long)clip.samples * 1000 / clip.frequency));
            _presetChart = new PresetChart
            {
                Title = info.name, Author = info.author ?? "", Difficulty = DataHelper.selectedDifficulty,
                LastSeenUid = DataHelper.selectedMusicUid ?? "", DurationMs = duration,
                FingerprintVersion = ChartFingerprint.Version, Fingerprint = ChartFingerprint.Compute(notes, duration)
            };
            RefreshPresets();
            Log("preset_chart", new { _presetChart.Title, _presetChart.Difficulty, _presetChart.Fingerprint, notes = notes.Count });
        }
        catch (Exception error)
        {
            _presetChart = null;
            _presetStatus = "Presets unavailable for this chart; see diagnostics. Practice still works.";
            Log("preset_error", new { action = "identify", error = error.ToString() });
        }
    }

    private void RefreshPresets()
    {
        if (_presetChart is null || _presetStore is null) return;
        var catalog = _presetStore.Read(_presetChart);
        _presetEntries.Clear();
        _presetEntries.AddRange(catalog.Entries);
        if (_selectedPreset is { } selected)
            _selectedPreset = _presetEntries.FirstOrDefault(e => e.DocumentId == selected.DocumentId && e.Preset.Id == selected.Preset.Id);
        _presetPage = Math.Clamp(_presetPage, 0, Math.Max(0, (_presetEntries.Count - 1) / 6));
        var exact = _presetEntries.Count(e => e.Exact);
        _presetStatus = $"{exact} current / {_presetEntries.Count - exact} other-revision presets. Selecting a current preset fills fields; F9 starts.";
        if (catalog.Warnings.Count > 0)
        {
            _presetStatus += $" {catalog.Warnings.Count} file warning(s); backups are read-only. See diagnostics.";
            Log("preset_file_warnings", new { warnings = catalog.Warnings });
        }
    }

    private void PresetAction(string action, Action operation)
    {
        try { operation(); }
        catch (Exception error)
        {
            _presetStatus = error.Message;
            Log("preset_error", new { action, error = error.ToString() });
        }
    }

    private void SelectPreset(PresetEntry entry)
    {
        _selectedPreset = entry;
        _presetName = entry.Preset.Name;
        _presetConfirmation = "";
        if (!entry.Exact)
        {
            _presetStatus = "Different chart revision: review the range below, then use Copy here and confirm. Original presets are preserved.";
            return;
        }
        if (!entry.Recovered) _presetTargetDocument = entry.DocumentId;
        FillPreset(entry);
    }

    private void FillPreset(PresetEntry entry)
    {
        entry.Preset.Values().Validate(_presetChart!.DurationMs);
        // Selecting a preset must cancel the OLD loop without arming a new one,
        // resuming a paused game, or turning practice protection on or off.
        _range.Suspend();
        _restartPending = false;
        _seekPending = false;
        _reloadLoopPending = false;
        if (_enabled) _exitPending = false;
        var values = entry.Preset.Values();
        _aText = values.AText;
        _bText = values.BText;
        _leadText = values.LeadText;
        _presetName = entry.Preset.Name;
        _presetStatus = $"Loaded '{entry.Preset.Name}'. Old loop stopped; press F9 to start.";
        Log("preset_selected", new { entry.DocumentId, entry.Preset.Id, entry.Recovered });
    }

    private void SavePreset(bool update)
    {
        if (_presetChart is null || _presetStore is null) return;
        var values = PresetValues.Parse(_aText, _bText, _leadText, _presetChart.DurationMs);
        var entry = update && _selectedPreset is { Exact: true, Recovered: false } selected
            ? _presetStore.Update(_presetChart, selected.DocumentId, selected.Preset.Id, _presetName, values)
            : !update ? _presetStore.SaveNew(_presetChart, _presetName, values, _presetTargetDocument)
            : throw new InvalidOperationException("Select a writable current preset first, or use Save new.");
        _selectedPreset = entry;
        _presetTargetDocument = entry.DocumentId;
        _presetName = entry.Preset.Name;
        _presetConfirmation = "";
        RefreshPresets();
        _presetStatus = $"Saved '{entry.Preset.Name}' ({values.AText}-{values.BText}s).";
        Log("preset_saved", new { entry.DocumentId, entry.Preset.Id, update });
    }

    private void DeletePreset()
    {
        if (_selectedPreset is not { Exact: true, Recovered: false } selected || _presetChart is null || _presetStore is null) return;
        var confirmation = "delete:" + selected.DocumentId + ":" + selected.Preset.Id;
        if (_presetConfirmation != confirmation)
        {
            _presetConfirmation = confirmation;
            _presetStatus = $"Delete '{selected.Preset.Name}'? Click Confirm delete. Changing selection cancels.";
            return;
        }
        _presetStore.Delete(_presetChart, selected.DocumentId, selected.Preset.Id);
        _selectedPreset = null;
        _presetConfirmation = "";
        RefreshPresets();
        _presetStatus = "Preset deleted. Current A/B fields were kept; previous file is in .bak.";
    }

    private void CopyPreset()
    {
        if (_selectedPreset is not { } selected || _presetChart is null || _presetStore is null) return;
        var confirmation = "copy:" + selected.DocumentId + ":" + selected.Preset.Id;
        if (_presetConfirmation != confirmation)
        {
            _presetConfirmation = confirmation;
            _presetStatus = $"Copy '{selected.Preset.Name}' ({selected.Preset.Values().AText}-{selected.Preset.Values().BText}s) to this chart revision? Click Confirm copy.";
            return;
        }
        var entry = _presetStore.CopyTo(_presetChart, selected.DocumentId, selected.Preset.Id, _presetTargetDocument);
        _selectedPreset = entry;
        _presetTargetDocument = entry.DocumentId;
        _presetConfirmation = "";
        RefreshPresets();
        FillPreset(entry);
    }

    private void DrawPresetPanel(float width)
    {
        _presetsExpanded = GUI.Toggle(new Rect(24, 350, width - 40, 26), _presetsExpanded, "Saved ranges (local JSON)");
        if (!_presetsExpanded) return;
        _presetScroll = GUI.BeginScrollView(new Rect(24, 382, width - 36, PanelHeight - 382),
            _presetScroll, new Rect(0, 0, width - 58, 510));
        var originalEnabled = GUI.enabled;
        try
        {
            GUI.Label(new Rect(0, 0, width - 60, 38), _presetChart is null ? "Enable practice and open a chart to manage presets."
                : $"{_presetChart.Title} | difficulty {_presetChart.Difficulty}");
            var available = _presetChart is not null && _presetStore is not null && _inBattle;
            var stage = _inBattle ? StageBattleComponent.instance : null;
            // Alphabetic names must not dispatch gameplay inputs. Typing is only
            // enabled while the native game is paused; default names work live.
            GUI.enabled = originalEnabled && available && stage?.isPause == true;
            GUI.Label(new Rect(0, 42, 48, 26), "Name");
            _presetName = GUI.TextField(new Rect(50, 42, width - 280, 26), _presetName, 64);
            GUI.enabled = originalEnabled && available;
            if (GUI.Button(new Rect(width - 222, 42, 158, 26), "Pause to edit name"))
                PresetAction("pause_edit", () => { if (stage?.isInGame == true && !stage.isPause) stage.Pause(true); });
            if (GUI.Button(new Rect(0, 80, 100, 28), "Save new")) PresetAction("save_new", () => SavePreset(false));
            GUI.enabled = originalEnabled && available && _selectedPreset is { Exact: true, Recovered: false };
            if (GUI.Button(new Rect(108, 80, 100, 28), "Update")) PresetAction("update", () => SavePreset(true));
            if (GUI.Button(new Rect(216, 80, 128, 28), _presetConfirmation.StartsWith("delete:", StringComparison.Ordinal) ? "Confirm delete" : "Delete"))
                PresetAction("delete", DeletePreset);
            GUI.enabled = originalEnabled && available;
            if (GUI.Button(new Rect(0, 116, 100, 28), "Refresh"))
                PresetAction("refresh", () => { _presetConfirmation = ""; RefreshPresets(); });
            GUI.enabled = originalEnabled && available && _selectedPreset is { } candidate && (!candidate.Exact || candidate.Recovered);
            if (GUI.Button(new Rect(108, 116, 180, 28), _presetConfirmation.StartsWith("copy:", StringComparison.Ordinal) ? "Confirm copy" : "Copy here"))
                PresetAction("copy", CopyPreset);
            GUI.enabled = originalEnabled && available;
            GUI.Label(new Rect(0, 150, width - 60, 24), "Select a range (other revision = explicit copy required)");
            for (var row = 0; row < 6 && _presetPage * 6 + row < _presetEntries.Count; row++)
            {
                var entry = _presetEntries[_presetPage * 6 + row];
                var selected = _selectedPreset?.DocumentId == entry.DocumentId && _selectedPreset?.Preset.Id == entry.Preset.Id;
                var tag = !entry.Exact ? "[other revision] " : entry.Recovered ? "[backup] " : "";
                var values = entry.Preset.Values();
                var text = $"{(selected ? "> " : "")}{tag}{entry.Preset.Name} | {values.AText}-{values.BText}s | lead {values.LeadText}s";
                if (GUI.Button(new Rect(0, 180 + row * 32, width - 64, 28), text))
                    PresetAction("select", () => SelectPreset(entry));
            }
            var pages = Math.Max(1, (_presetEntries.Count + 5) / 6);
            GUI.enabled = originalEnabled && _presetPage > 0;
            if (GUI.Button(new Rect(0, 382, 90, 26), "Previous")) { _presetPage--; _presetConfirmation = ""; }
            GUI.enabled = originalEnabled && _presetPage + 1 < pages;
            if (GUI.Button(new Rect(100, 382, 90, 26), "Next")) { _presetPage++; _presetConfirmation = ""; }
            GUI.enabled = originalEnabled;
            GUI.Label(new Rect(204, 382, 170, 26), $"Page {_presetPage + 1}/{pages}");
            GUI.Label(new Rect(0, 418, width - 64, 84), _presetStatus, _label!);
        }
        finally { GUI.enabled = originalEnabled; GUI.EndScrollView(); }
    }
}
