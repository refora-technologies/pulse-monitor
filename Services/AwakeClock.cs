using System.Runtime.InteropServices;

namespace Pulse.Services;

/// <summary>
/// Milliseconds the machine has spent awake, for every "has this gone quiet?" decision.
/// </summary>
/// <remarks>
/// The watchdogs used Environment.TickCount64, which keeps counting while the machine sleeps.
/// So every wake looked like a sensor host that had stopped answering for as long as the lid had
/// been shut, and it was replaced as hung. One reporter's log had 52 of those warnings and 51
/// were wakes, and one host was killed 26 ms after it started "for 5916s" of silence it had never
/// had a chance to break. The frame capture watchdog made the same mistake and then blamed
/// abandoned trace sessions from other tools.
///
/// Listening for Windows' wake notification would not have been enough on its own. Windows fires
/// timers that fell due during sleep the moment it wakes, so the watchdog runs before the
/// notification arrives. A clock that does not count sleep makes the question right in the first
/// place: silence is only silence if the machine was awake for it.
///
/// QueryUnbiasedInterruptTime is documented as excluding time spent in sleep and hibernation.
/// It counts in 100 ns units from boot, so it never wraps in the life of a machine.
/// </remarks>
public static class AwakeClock
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public static long Milliseconds =>
        QueryUnbiasedInterruptTime(out var ticks)
            ? (long)(ticks / 10_000)
            : Environment.TickCount64;   // cannot fail with a valid pointer; the old clock is the fallback
}
