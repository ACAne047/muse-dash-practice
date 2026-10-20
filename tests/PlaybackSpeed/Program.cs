using MuseDashPracticePrototype;

var assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new Exception(message);
}
void Reject(Action operation, string message)
{
    var rejected = false;
    try { operation(); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, message);
}

const long frequency = 10_000_000;
foreach (var rate in new[] { .5, .6, .75, .9, 1, 1.1, 1.25 })
{
    var clock = new PlaybackClock();
    clock.Seek(60 * frequency, 60 * frequency, rate);
    Check(clock.Read(60 * frequency) == 60 * frequency, "Attaching speed moved the song");
    var raw = 60 * frequency;
    for (var second = 1; second <= 20; second++)
    {
        raw += frequency;
        var expected = 60 * frequency + (long)(second * frequency * rate);
        Check(Math.Abs(clock.Read(raw) - expected) <= 1, "Song speed differs from audio source progress");
    }
    // Native Stop/Start does not add paused wall time to accumulated raw ticks.
    var beforePause = clock.Read(raw);
    for (var pausedFrame = 0; pausedFrame < 10; pausedFrame++)
        Check(clock.Read(raw) == beforePause, "Paused song clock advanced");
    raw += frequency;
    Check(Math.Abs(clock.Read(raw) - beforePause - (long)(frequency * rate)) <= 1, "Resume skipped elapsed pause time");
    var beforeChange = clock.Read(raw);
    clock.ChangeRate(raw, .5);
    Check(clock.Read(raw) == beforeChange, "Changing rate jumped the song position");
    raw += 2 * frequency;
    Check(clock.Read(raw) == beforeChange + frequency, "Changed rate did not take effect");
    var beforeNormal = clock.Read(raw);
    clock.ChangeRate(raw, 1);
    Check(clock.Read(raw) == beforeNormal, "Returning to 1x jumped back to raw stopwatch time");
    Check(clock.Read(raw + frequency) == beforeNormal + frequency, "Returning to 1x did not resume normal progression");

    // Seek to source-song A-lead, independently of both old rate and old raw ticks.
    for (var round = 0; round < 100; round++)
    {
        clock.Seek(57 * frequency, 57 * frequency, rate);
        Check(clock.Read(57 * frequency) == 57 * frequency, "Loop seek changed the source-song position");
        Check(Math.Abs(clock.Read(77 * frequency) - (57 * frequency + (long)(20 * frequency * rate))) <= 1,
            "Repeated loops accumulated clock drift");
    }
}

foreach (var text in new[] { "0.5", "0.75", "1", "1.25", " 0.90 ", "5e-1" })
    Check(PlaybackClock.TryParseRate(text, out _), "Valid speed was rejected");
foreach (var text in new[] { "", "NaN", "Infinity", "-1", "0", "0.49", "1.251", "0,75", "text" })
    Check(!PlaybackClock.TryParseRate(text, out _), "Invalid speed was accepted");
var edge = new PlaybackClock();
foreach (var rate in new[] { double.NaN, double.PositiveInfinity, .49, 1.26 })
    Reject(() => edge.Seek(0, 0, rate), "Clock accepted invalid speed");
Reject(() => edge.Seek(-1, 0, 1), "Clock accepted a negative raw anchor");
Reject(() => edge.Seek(0, -1, 1), "Clock accepted a negative chart anchor");
edge.Seek(100, 100, .75);
var resetDetected = false;
try { edge.Read(0); } catch (InvalidOperationException) { resetDetected = true; }
Check(resetDetected, "Unexpected native stopwatch reset was hidden");
// The 0.3.9 regression: the song and Unity advance 7 seconds in 10 seconds at
// 0.7x, while Spine's independent GlobalTimer previously advanced all 10 seconds.
var regression = new PlaybackClock();
regression.Seek(0, 0, .7);
Check(Math.Abs(10 - regression.Read(10 * frequency) / (double)frequency) > 2.9,
    "Did not reproduce the independent animation timer mismatch");
foreach (var rate in new[] { .5, .7, .75, 1, 1.25 })
{
    var animationClock = new PlaybackClock();
    animationClock.Seek(0, 0, rate);
    double animationTime = 0;
    for (var frame = 1; frame <= 1000; frame++)
    {
        animationTime += animationClock.ScaleAnimationDelta(.01f);
        if (frame % 50 == 0)
            Check(Math.Abs(animationTime - animationClock.Read(frame * frequency / 100) / (double)frequency) < .0001,
                "Spine animation and song time diverged at a non-unit playback rate");
    }
    // Changing rate between loops must update both clocks together. A component's
    // own animation/track rate stays native; do not multiply it by playback again.
    animationClock.ChangeRate(10 * frequency, .5);
    var before = animationTime;
    for (var frame = 0; frame < 100; frame++) animationTime += animationClock.ScaleAnimationDelta(.01f);
    Check(Math.Abs(animationTime - before - .5) < .0001, "Animation rate did not follow the new song rate");
    Check(Math.Abs(animationClock.ScaleAnimationDelta(.02f) - .01f) < 1e-6, "Wall delta was scaled more than once");
    Check(animationClock.ScaleAnimationDelta(0) == 0, "Zero animation delta advanced animation");
}
foreach (var delta in new[] { -1f, float.NaN, float.PositiveInfinity })
    Check(regression.ScaleAnimationDelta(delta) == 0, "Invalid animation delta reached the skeleton");
Console.WriteLine($"PASS: {assertions} speed-clock assertions (rate changes, pause/resume, source-song seeks, 700 loop resets, invalid inputs).");
