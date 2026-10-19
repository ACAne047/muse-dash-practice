using System.Globalization;

namespace MuseDashPracticePrototype;

internal sealed class PracticeRange
{
    public float A { get; private set; }
    public float? B { get; private set; }
    public float LeadIn { get; private set; } = 3;
    public bool IsRunning { get; private set; }
    public bool WaitingForB => !B.HasValue;
    public bool CanRepeat => IsRunning && B.HasValue && B.Value - A >= 1;
    public float RewindTarget => Math.Max(0, A - LeadIn);

    // A new chart waits for a new range. Only our own full-reload fallback may
    // continue an already running range across a chart load.
    public bool BeginChart(bool resumeLoop)
    {
        if (resumeLoop && CanRepeat) return true;
        A = 0;
        B = null;
        IsRunning = false;
        return false;
    }

    public void Suspend() => IsRunning = false;

    public void MarkA(float time)
    {
        if (!float.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        A = time;
        B = null;
        IsRunning = false;
    }

    public bool MarkB(float time)
    {
        if (!float.IsFinite(time) || time - A < 1) return false;
        B = time;
        IsRunning = true;
        return true;
    }

    public bool Apply(string aText, string bText, string leadText, out string error)
    {
        IsRunning = false;
        error = "Set A, B and lead-in: A >= 0; B >= A + 1; lead-in >= 0";
        if (!Parse(aText, out var a) || !Parse(bText, out var b) || !Parse(leadText, out var lead)
            || a < 0 || b - a < 1 || lead < 0) return false;
        A = a;
        B = b;
        LeadIn = lead;
        IsRunning = true;
        error = "";
        return true;
    }

    private static bool Parse(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
}
