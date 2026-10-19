namespace MuseDashPracticePrototype;

internal static class InputRouteReset
{
    // In the fingerprinted game build, GameTouchPlay.Init registers event 2.
    // TouchScript dispatches by id through GTrigger's current handler table,
    // so stale objects with the same id also dispatch the NEW handler again.
    internal const uint GameplayEventId = 2;

    internal static int KeepNewestGameplayRoute(int count, Func<int, uint?> eventIdAt, Action<int> removeAt)
    {
        var keptNewest = false;
        var removed = 0;
        for (var i = count - 1; i >= 0; i--)
        {
            if (eventIdAt(i) != GameplayEventId) continue;
            if (!keptNewest) { keptNewest = true; continue; }
            removeAt(i);
            removed++;
        }
        return removed;
    }
}
