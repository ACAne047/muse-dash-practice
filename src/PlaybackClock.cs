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

    // GlobalTimer supplies unscaled wall-time deltas to Spine.Update(). Unity's
    // timeScale does not reach that separate stopwatch. Scale that source once;
    // explicit SkeletonAnimation.Update(songDelta) calls must stay untouched.
    public float ScaleAnimationDelta(float wallDelta) =>
        float.IsFinite(wallDelta) && wallDelta >= 0 ? wallDelta * (float)Rate : 0;

    // Native music sync uses (song + chartDelay - calibration) in source seconds.
    // Calibration measures wall-time device latency; only that term follows Rate.
    // Add a correction to the native sample instead of scaling the whole position
    // or changing the saved preference, chart delay, or note/judgement timestamps.
    public int AdjustMusicOffsetSamples(int nativeSample, double calibrationSeconds, int frequency)
    {
        if (!double.IsFinite(calibrationSeconds)) throw new ArgumentOutOfRangeException(nameof(calibrationSeconds));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        return (int)Math.Clamp(Math.Round(nativeSample + calibrationSeconds * (1 - Rate) * frequency),
            int.MinValue, int.MaxValue);
    }
}
