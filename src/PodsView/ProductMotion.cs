namespace PodsView;

/// <summary>The atlas and its preview use this same deterministic timing contract.</summary>
internal static class ProductMotion
{
    internal const int FrameCount = 61;
    internal const int FrameSize = 256;
    internal const int Columns = 8;
    internal const double PeriodSeconds = 18;
    internal static int FrameAt(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return FrameCount / 2;
        double phase = (seconds % PeriodSeconds) / PeriodSeconds;
        double position = 0.5 + 0.5 * Math.Sin(2 * Math.PI * phase);
        return Math.Clamp((int)Math.Round(position * (FrameCount - 1)), 0, FrameCount - 1);
    }
}
