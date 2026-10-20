using System.Globalization;

namespace MuseDashPracticePrototype;

// Input is the native stopwatch's accumulated RUNNING ticks, so native Stop /
// Start already excludes paused time. Do not feed scaled ticks back into this.
internal sealed class PlaybackClock
{
    private long _rawAnchor;
    private long _chartAnchor;
    public double Rate { get; private set; } = 1;

    public static bool TryParseRate(string text, out double rate) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
        && double.IsFinite(rate) && rate >= .5 && rate <= 1.25;

    public void Seek(long rawTicks, long chartTicks, double rate)
    {
        if (rawTicks < 0 || chartTicks < 0) throw new ArgumentOutOfRangeException(nameof(rawTicks));
        if (!double.IsFinite(rate) || rate < .5 || rate > 1.25) throw new ArgumentOutOfRangeException(nameof(rate));
        _rawAnchor = rawTicks;
        _chartAnchor = chartTicks;
        Rate = rate;
    }

    public long Read(long rawTicks)
    {
        if (rawTicks < _rawAnchor) throw new InvalidOperationException("Native chart stopwatch reset without a speed rebase");
        return checked(_chartAnchor + (long)((rawTicks - _rawAnchor) * Rate));
    }

    public void ChangeRate(long rawTicks, double rate) => Seek(rawTicks, Read(rawTicks), rate);
}
