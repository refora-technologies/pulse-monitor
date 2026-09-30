using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pulse.Services;

/// <summary>
/// Stops the frame capture trace sessions left behind by Pulse 1.2.0 and earlier.
/// </summary>
/// <remarks>
/// Those builds named their session after the process id, <c>Pulse_12084</c>, and a killed
/// PresentMon releases nothing, so every launch registered one more and none was ever stopped.
/// This machine reached fifty-one of them, at which point Windows was dropping around a hundred
/// and thirty-six thousand events per capture and frame rate measurement was dead for the whole
/// machine, NVIDIA's own overlay included.
///
/// Current builds use one constant name and hand it back on the next launch, so the leak is
/// fixed going forward. What that does not do is clear what earlier versions already left: the
/// sessions are named per process id, so nothing that ships now knows they exist. Upgrading
/// would leave the damage in place on exactly the machines that suffered it.
///
/// The sessions do not survive a restart, so this only matters to somebody who upgrades without
/// rebooting. That is most people, and the count is highest on machines left running for weeks,
/// which is the same set.
/// </remarks>
internal static class TraceSessionCleanup
{
    /// <summary>
    /// A session this Pulse should stop: the old naming, and only the old naming.
    /// </summary>
    /// <remarks>
    /// Anchored at both ends and digits only, so it cannot match the session current builds use
    /// (<c>PulseMonitor</c>, which does not even have the underscore), a session belonging to
    /// something else that merely starts the same way, or a name we might choose later.
    /// Stopping the wrong session would break another program's capture, which is the exact
    /// harm this exists to undo.
    /// </remarks>
    private static readonly Regex LeakedSessionName =
        new(@"^Pulse_[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// Long enough for a busy machine, short enough that nothing waits on it meaningfully.
    private const int TimeoutMs = 10_000;

    /// The full path, not the name. Pulse runs elevated, so anything it starts starts elevated
    /// too, and resolving that through a search path we do not control is a decision handed to
    /// whoever can write to one of its directories. Same reasoning as StartupTask.SchTasks.
    private static readonly string Logman =
        System.IO.Path.Combine(Environment.SystemDirectory, "logman.exe");

    /// <summary>
    /// A sanity limit, not an expectation. Fifty-one were seen in the wild; a number far past
    /// that means the output is being misread, and spawning a process per line would then be a
    /// way to turn a parsing bug into a hung startup.
    /// </summary>
    private const int MostToStop = 200;

    private static int _alreadyRun;

    /// <summary>
    /// Clears the leftovers, once per process, and says nothing when there are none.
    /// </summary>
    /// <remarks>
    /// Silent in the ordinary case on purpose. Every machine that has never run an affected
    /// build reaches this, finds nothing, and should not write a line about it every launch.
    /// </remarks>
    public static void Run()
    {
        if (Interlocked.Exchange(ref _alreadyRun, 1) != 0) return;

        try
        {
            var listing = Capture("query -ets");

            // Judged on whether a listing came back, not on what logman returned.
            //
            // logman exits 0x80071068, ERROR_WMI_GUID_NOT_FOUND, when it meets a session it
            // cannot resolve while enumerating, and prints every session it could read anyway.
            // Reading the exit code threw that listing away and left the leftovers in place,
            // and it happened on a real machine: "logman failed (-2147020696): query -ets"
            // with nothing at all on its error stream.
            //
            // A partial listing is safe to act on. The pattern below can only match the old
            // per-process naming, so the worst a missing line can do is leave one session for
            // the next launch to find. It can never cause the wrong thing to be stopped.
            if (!listing.Ran || listing.Output.Trim().Length == 0)
            {
                if (listing.Ran)
                    LogService.Warn(nameof(TraceSessionCleanup),
                        $"logman listed no trace sessions (exit code {listing.ExitCode}); nothing was cleared.");
                return;
            }

            var leaked = Leaked(listing.Output);
            if (leaked.Count == 0) return;

            LogService.Info(nameof(TraceSessionCleanup),
                $"Found {leaked.Count} trace session(s) left by an earlier version: {string.Join(", ", leaked)}.");

            int stopped = 0;
            foreach (var name in leaked)
            {
                // Quoted although the pattern above cannot admit a space, because the argument
                // is built from text that came out of another program.
                //
                // This one is judged on the exit code, unlike the query above, and the
                // difference is not an inconsistency. A listing either arrived or it did not,
                // and its own text is the evidence. A stop produces no output to be evidence
                // of anything, so what it returned is all there is to go on.
                var stop = Capture($"stop \"{name}\" -ets");
                if (stop.Ran && stop.ExitCode == 0) stopped++;
            }

            if (stopped == leaked.Count)
            {
                LogService.Info(nameof(TraceSessionCleanup),
                    $"Stopped all {stopped}. Frame capture on this machine should work again.");
            }
            else
            {
                // Worth saying plainly. The user's symptom is that frame rates do not work in
                // any tool, and the cause is invisible without this line.
                LogService.Warn(nameof(TraceSessionCleanup),
                    $"Stopped {stopped} of {leaked.Count}. The rest will clear at the next restart.");
            }
        }
        catch (Exception ex)
        {
            // Housekeeping. Failing leaves the machine exactly as it was, which is the state
            // every earlier version left it in, so this must never be allowed to affect startup.
            LogService.Error(nameof(TraceSessionCleanup), "Could not clear old trace sessions", ex);
        }
    }

    /// <summary>
    /// The session names in logman's listing that belong to an earlier Pulse.
    /// </summary>
    /// <remarks>
    /// Internal so the tests can read it directly rather than by starting real trace sessions,
    /// which needs administrator rights and a machine willing to have them created.
    ///
    /// Only the first column is considered. The listing is a table whose headings are
    /// translated on a localised Windows, so nothing here depends on reading them; a session
    /// name is not translated, and a heading cannot look like one.
    /// </remarks>
    internal static List<string> Leaked(string listing)
    {
        var found = new List<string>();

        foreach (var line in listing.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            int space = trimmed.IndexOf(' ');
            var name = space < 0 ? trimmed : trimmed[..space];

            if (!LeakedSessionName.IsMatch(name)) continue;
            if (found.Contains(name)) continue;

            found.Add(name);
            if (found.Count >= MostToStop) break;
        }

        return found;
    }

    /// <summary>
    /// What came back from logman: whether it ran, what it exited with, and what it said.
    /// </summary>
    /// <remarks>
    /// Three facts rather than "output or null". Collapsing them is what made a listing that
    /// arrived intact indistinguishable from one that never came, on the strength of an exit
    /// code that did not mean what it was taken to mean.
    /// </remarks>
    private readonly record struct Reply(bool Ran, int ExitCode, string Output)
    {
        public static Reply Failed => new(false, -1, "");
    }

    /// <summary>
    /// Runs logman and returns what came back.
    /// </summary>
    /// <remarks>
    /// The pipe handling is deliberate and is the same shape as StartupTask.RunCapture, for the
    /// same reason: reading a pipe to the end before the process is known to have exited waits
    /// forever on a child that hangs, and the timeout below never gets evaluated. Both pipes are
    /// started asynchronously, the wait is bounded, and a child that outlives it is killed.
    /// </remarks>
    private static Reply Capture(string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName               = Logman,
            Arguments              = arguments,
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var process = Process.Start(info);
        if (process is null) return Reply.Failed;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask  = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }   // nothing useful follows
            LogService.Warn(nameof(TraceSessionCleanup), $"logman timed out: {arguments}");
            return Reply.Failed;
        }

        if (!Task.WhenAll(outputTask, errorTask).Wait(2_000))
        {
            LogService.Warn(nameof(TraceSessionCleanup), $"logman output could not be read: {arguments}");
            return Reply.Failed;
        }

        // Reported without a verdict attached. A non-zero code from a query is not a failure
        // to list, as the caller explains; a non-zero code from a stop is worth knowing about,
        // and stopping a session that has already gone is an ordinary answer rather than a
        // fault, since another Pulse may have cleared it in between.
        if (process.ExitCode != 0)
        {
            LogService.Info(nameof(TraceSessionCleanup),
                $"logman exited {process.ExitCode} for '{arguments}'"
              + $"{(errorTask.Result.Trim().Length > 0 ? " :: " + errorTask.Result.Trim() : "")}"
              + $" ({outputTask.Result.Trim().Length} characters of output).");
        }

        return new Reply(true, process.ExitCode, outputTask.Result);
    }
}
