using System.Runtime.InteropServices;

namespace Pulse.Services;

/// <summary>
/// Notices when this process itself stopped running, by a timer that should have ticked and
/// did not.
/// </summary>
/// <remarks>
/// AwakeClock answers "was the machine asleep?" for classic sleep and hibernation, which is what
/// Microsoft documents it for. It does not answer it everywhere. On a laptop with Modern Standby
/// Windows counts itself as awake while its Desktop Activity Moderator suspends every thread of
/// every desktop app, Pulse and its sensor host included, and the sleep notification may not be
/// sent at all. Nothing documents whether AwakeClock counts that time, so the watchdog could
/// come back from standby still believing the host had gone quiet on its own.
///
/// This does not need to know which kind of pause it was. A watchdog that ticks every two
/// seconds and finds forty minutes since its last tick was not running, and neither was
/// anything it watches, so their silence is not theirs to answer for. It also covers a paused
/// virtual machine and a machine frozen solid, which no sleep notification describes.
///
/// Measured with GetTickCount64, called by name, because the point here is a clock that does
/// count sleep. Environment.TickCount64 happens to be that clock today, and relying on what it
/// happens to be is how the watchdogs got this wrong in the first place.
/// </remarks>
public sealed class PauseDetector
{
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private readonly long _thresholdMs;
    private long _lastTick;

    /// <param name="threshold">A gap between two checks longer than this is a pause. Several
    /// times the timer's own interval, so a thread pool slow to run a callback on a busy machine
    /// is not mistaken for one.</param>
    public PauseDetector(TimeSpan threshold)
    {
        _thresholdMs = (long)threshold.TotalMilliseconds;
        _lastTick    = (long)GetTickCount64();
    }

    /// <summary>
    /// Call from the timer. Returns how long this process was not running since the previous
    /// call, or zero when it ran on time.
    /// </summary>
    public TimeSpan Check()
    {
        long now  = (long)GetTickCount64();
        long last = Interlocked.Exchange(ref _lastTick, now);
        long gap  = now - last;
        return gap > _thresholdMs ? TimeSpan.FromMilliseconds(gap) : TimeSpan.Zero;
    }
}
