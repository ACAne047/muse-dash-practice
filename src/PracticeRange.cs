using System.Globalization;

namespace MuseDashPracticePrototype;

internal sealed class PracticeRange
{
    public float A { get; private set; } = 20;
    public float B { get; private set; } = 30;
    public float LeadIn { get; private set; } = 3;
    public bool WaitingForB { get; private set; }
    public bool CanRepeat => !WaitingForB && B - A >= 1;
    public float RewindTarget => Math.Max(0, A - LeadIn);

    public void MarkA(float time)
    {
        if (!float.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        A = time;
        WaitingForB = true;
    }

    public bool MarkB(float time)
    {
        if (!float.IsFinite(time) || time - A < 1) return false;
        B = time;
        WaitingForB = false;
        return true;
    }

    public bool Apply(string aText, string bText, string leadText, out string error)
    {
        error = "Use seconds: A >= 0; B >= A + 1; lead-in >= 0";
        if (!Parse(aText, out var a) || !Parse(bText, out var b) || !Parse(leadText, out var lead)
            || a < 0 || b - a < 1 || lead < 0) return false;
        A = a;
        B = b;
        LeadIn = lead;
        WaitingForB = false;
        error = "";
        return true;
    }

    private static bool Parse(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
}
