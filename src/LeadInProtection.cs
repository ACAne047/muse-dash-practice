using System.Globalization;
using Il2CppAssets.Scripts.GameCore.HostComponent;
using Il2CppFormulaBase;
using Il2CppGameLogic;

namespace MuseDashPracticePrototype;

public sealed partial class PracticeMod
{
    private readonly LeadInWindow _leadInWindow = new();

    private void InstallLeadInProtection()
    {
        Patch(typeof(BattleRoleAttributeComponent), "Hurt", prefix: nameof(AllowLeadInHurt));
        Patch(typeof(BattleRoleAttributeComponent), "MissHardEffect", prefix: nameof(AllowLeadInHurt));
        Patch(typeof(BattleRoleAttributeComponent), "AddHp", prefix: nameof(AllowLeadInHpChange));
    }

    private bool IsLeadInProtected()
    {
        if (!_ready || !_enabled || !_inBattle || !_range.CanRepeat) { _leadInWindow.Cancel(); return false; }
        try
        {
            var stage = StageBattleComponent.instance;
            return stage is not null && (stage.isInGame || stage.isPause)
                && _leadInWindow.Contains(stage.timeFromMusicStart);
        }
        catch (Exception error)
        {
            _leadInWindow.Cancel();
            if (_logged.Add("lead_in_error")) Log("lead_in_error", new { error = error.Message });
            return false;
        }
    }

    private static bool AllowLeadInHurt(BattleRoleAttributeComponent __instance) =>
        __instance.Pointer != BattleRoleAttributeComponent.instance?.Pointer || Current?.IsLeadInProtected() != true;

    private static bool AllowLeadInHpChange(BattleRoleAttributeComponent __instance, int __0) =>
        __0 >= 0 || AllowLeadInHurt(__instance);

    private void BeginLeadIn(float target)
    {
        if (target < _a) _leadInWindow.Begin(target, _a);
        else _leadInWindow.Cancel();
        Log("lead_in", new { target, a = _a, protectedUntilA = target < _a });
    }

    private static RewindBoundaryPlan PlanRewindBoundary(StageBattleComponent stage, float target)
    {
        var notes = stage.m_MusicTickData;
        var windows = new List<RewindNoteWindow>(notes.Count);
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            windows.Add(new RewindNoteWindow(note.objId, Seconds(note.showTick), Seconds(note.missTick),
                Seconds(note.configData.length), note.isLongPressStart ? note.endIndex : -1));
        }
        return RewindBoundaryPlan.Build(windows, target);

        static double Seconds(Il2CppSystem.Decimal value) => double.Parse(value.ToString(), CultureInfo.InvariantCulture);
    }

    private static int RestoreBoundaryNotes(GameMusicScene scene, RewindBoundaryPlan plan)
    {
        var manager = BattleEnemyManager.instance;
        var cursor = manager.m_CurrentGenIdx;
        var passed = scene.passIdx;
        var previous = scene.prePassIdx;
        var restored = 0;
        try
        {
            foreach (var id in plan.RestoreIds)
            {
                var root = scene.GetPreLoadGameObject(id);
                if (root is null || !root || root.activeSelf) continue;
                // CreateBattleEnemy reuses the preloaded object and Node.Run.
                // Do not call controller.Init: that duplicates gameplay routes.
                manager.CreateBattleEnemy(id);
                restored++;
            }
        }
        finally
        {
            // Native creation changes these cursors; keep future scheduled
            // generation past ALL skipped show events, including expired ones.
            manager.m_CurrentGenIdx = cursor;
            scene.passIdx = passed;
            scene.prePassIdx = previous;
        }
        return restored;
    }
}
