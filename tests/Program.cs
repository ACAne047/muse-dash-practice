using MuseDashPracticePrototype;

// Reproduce the inspected native dispatch contract: a route stores an event id,
// and EACH list entry invokes the handler currently registered for that id.
var checks = 0;
void Assert(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}
int Prune(List<Route?> routes) => InputRouteReset.KeepNewestGameplayRoute(routes.Count,
    index => routes[index]?.Id, index => routes.RemoveAt(index));
void Dispatch(List<Route?> routes, Dictionary<uint, Action> handlers)
{
    foreach (var route in routes)
        if (route is not null && handlers.TryGetValue(route.Id, out var handler)) handler();
}

// One input consumed three close notes after two resets in the old path.
var oldRoutes = new List<Route?> { new(2, 0), new(2, 1), new(2, 2) };
var oldNotes = new Queue<int>(new[] { 11, 12, 13 });
var oldHits = 0;
Dispatch(oldRoutes, new() { [2] = () => { if (oldNotes.TryDequeue(out _)) oldHits++; } });
Assert(oldHits == 3, "Control must reproduce repeated judgement from one physical input");

// The fix must retain the latest native handler and leave other routes intact.
var otherFirst = new Route(7, 0);
var otherSecond = new Route(7, 1);
var newest = new Route(2, 2);
var routes = new List<Route?> { otherFirst, new(2, 0), null, new(2, 1), otherSecond, newest };
Assert(Prune(routes) == 2, "Two stale gameplay routes must be removed");
Assert(routes.SequenceEqual(new Route?[] { otherFirst, null, otherSecond, newest }),
    "Preserve the newest route, unrelated routes, null entries and ordering");
var notes = new Queue<int>(new[] { 11, 12, 13 });
var hits = 0;
var otherCalls = 0;
Dispatch(routes, new() { [2] = () => { if (notes.TryDequeue(out _)) hits++; }, [7] = () => otherCalls++ });
Assert(hits == 1 && notes.Count == 2, "One input must no longer consume the three neighbouring notes");
Assert(otherCalls == 2, "Unrelated event dispatch must be preserved");
Assert(Prune(routes) == 0, "Repeated cleanup must be idempotent");

// Repeated rewinds must not amplify input; this does not impose a frame cap.
for (var round = 3; round <= 102; round++)
{
    var latest = new Route(2, round);
    routes.Add(latest);
    Assert(Prune(routes) == 1, "Each rewind removes exactly its old gameplay route");
    Assert(routes.Count(route => route?.Id == 2) == 1 && ReferenceEquals(routes.Last(), latest),
        "Exactly one route must target the newly registered handler");
    var calls = 0;
    var handler = new Dictionary<uint, Action> { [2] = () => calls++ };
    Dispatch(routes, handler);
    Dispatch(routes, handler);
    Assert(calls == 2, "Two distinct physical inputs must both be delivered");
}
Assert(Prune(new List<Route?>()) == 0, "No route must not be invented");
Assert(Prune(new List<Route?> { new(7, 0), new(7, 1) }) == 0, "Unrelated ids must not be deduplicated");
Console.WriteLine($"PASS: {checks} assertions; old multi-hit reproduced, 100 resets retain one gameplay dispatch, separate inputs preserved.");

sealed record Route(uint Id, int Generation);
