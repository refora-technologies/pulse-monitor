namespace Pulse.Models;

/// <summary>
/// The frame rate and the 1% low, from frame times in milliseconds.
/// </summary>
/// <remarks>
/// Part of the shared core (Core/Pulse.Core.csproj): the arithmetic only, so every version of
/// Pulse computes these the same way whatever it measures frames with. The Windows frame capture
/// (Services/FpsService.cs) keeps the windowing and the choice of swap chain, which are about
/// PresentMon; it hands the numbers here. Moved here unchanged on 18 Nov 2026.
/// </remarks>
public static class FrameMath
{
    /// <summary>
    /// Frames per second from the total of some frame times and how many there were: the mean
    /// frame time, inverted. Null below two frames, because a single frame time is noise, not a rate.
    /// </summary>
    public static float? Fps(double totalMs, int count)
    {
        if (count < 2) return null;
        double mean = totalMs / count;
        return mean > 0 ? (float)(1000.0 / mean) : null;
    }

    /// <summary>
    /// The 1% low: the frame rate at the 99th percentile of the frame times. Sorts the first
    /// <paramref name="count"/> of <paramref name="times"/> in place.
    /// </summary>
    /// <remarks>
    /// The percentile method, which is what RTSS, MSI Afterburner and CapFrameX report and what
    /// people mean when they compare 1% lows.
    /// </remarks>
    public static float? OnePercentLow(double[] times, int count)
    {
        if (count <= 0) return null;

        // Sorted shortest frame time to longest, so the stutters are at the end. Frame
        // times, never per-frame rates: computing a rate for each frame and then taking the
        // lowest 1% of those is a different and wrong calculation, and it is the mistake
        // this metric is most often implemented with.
        Array.Sort(times, 0, count);

        // Nearest rank: the 99th percentile is the value at rank ceil(0.99 * n), which is
        // one lower as a zero-based index. Truncating 0.99 * n instead, as some
        // implementations do, picks the next sample along; the two agree except when the
        // frame count is an exact multiple of a hundred, and then by a single sample.
        int boundary = Math.Clamp((int)Math.Ceiling(0.99 * count) - 1, 0, count - 1);
        double boundaryMs = times[boundary];

        // Converted to a rate exactly once, here at the end.
        return boundaryMs > 0 ? (float)(1000.0 / boundaryMs) : null;
    }
}
