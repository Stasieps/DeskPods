namespace PodsView;

/// <summary>Presentation estimate from the broadcast ten-point step, not extra radio precision.</summary>
internal static class BatteryFormat
{
    internal static int Midpoint(int step)
    {
        int value = Math.Clamp(step, 0, 100);
        return value is 0 or 100 ? value : Math.Min(100, value + 5);
    }
    internal static int Value(int value) => Midpoint(value);
    internal static string Percent(int value) => Value(value) + " %";
    internal static double Bar(int value) => Value(value) / 100d;
    internal static bool ShouldAlert(int? raw, bool charging, bool cached, int threshold)
        => raw is int value && !charging && !cached && Value(value) <= threshold;
}
