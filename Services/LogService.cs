using System.IO;
using System.Text;

namespace Pulse.Services;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// A small rotating log, written beside the settings file.
///
/// Pulse swallows almost every exception — a deliberate choice, since a monitoring overlay
/// should never interrupt what you are doing because one sensor misbehaved. The cost was
/// that failures left no trace at all: the GDI leak took a bisect to find, and a user
/// reporting "it stopped reading my GPU" gave us nothing to work from. Swallowing the
/// exception is still right; throwing away the evidence was not.
///
/// Deliberately not a logging framework. A handful of files, capped, no dependencies, and
/// every method silent on failure — logging must never become a source of faults itself.
///
/// Two properties matter more than they look:
///
///   Every line is on disk before Write returns. AppendAllText opens, writes and closes, so
///   nothing waits in a buffer. When the process is killed outright — by a driver fault, by
///   Task Scheduler, by anything that never reaches managed code — the last line written is
///   still there to read afterwards. That is the only way to see such a death at all.
///
///   History is kept across runs and across upgrades. It lives under %APPDATA%, which an
///   upgrade does not touch, and old files are aged out rather than discarded, so a problem
///   reported today can still be traced back through earlier sessions.
/// </summary>
public static class LogService
{
    private const long MaxBytes    = 1024 * 1024;   // rotate the active file at 1 MB
    private const int  MaxArchives = 5;             // keep this many older files

    private static readonly object Gate = new();

    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Refora", "Pulse", "logs");

    public static string LogPath => Path.Combine(Directory, "pulse.log");

    /// <summary>
    /// Records that a session is in progress and what it was last doing.
    ///
    /// Deleted on a clean exit, so finding it at startup means the last run ended without
    /// one. That is the whole crash detector: a native fault, a forced termination or a
    /// power-off never gets to run any managed code, but it also never gets to delete this.
    /// </summary>
    private static string SessionStatePath => Path.Combine(Directory, "session.state");

    private static string ArchivePath(int index) => Path.Combine(Directory, $"pulse.{index}.log");

    public static void Info (string source, string message)              => Write(LogLevel.Info,  source, message, null);
    public static void Warn (string source, string message)              => Write(LogLevel.Warn,  source, message, null);
    public static void Error(string source, string message, Exception e) => Write(LogLevel.Error, source, message, e);

    /// An error that arrived as text rather than as an exception, which is what a crash in
    /// another process looks like from here.
    public static void Error(string source, string message) => Write(LogLevel.Error, source, message, null);

    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Replaces the user's profile directory with %USERPROFILE%.
    ///
    /// Messages routinely include file paths, and on Windows those carry the account name —
    /// so a log meant to be pasted into a public issue would have disclosed the user's
    /// Windows username. Nothing else in these files identifies anyone.
    /// </summary>
    private static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (string.IsNullOrEmpty(UserProfile)) return text;

        return text.Replace(UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(LogLevel level, string source, string message, Exception? error)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("  ").Append(level.ToString().ToUpperInvariant().PadRight(5))
                .Append("  ").Append((source ?? "").PadRight(18))
                .Append("  ").Append(Redact(message));

            // Type and message, and then the same for whatever it was thrown over.
            //
            // No stack trace still: it can carry paths from the build machine, and this file is
            // meant to be pasteable into a public issue. The inner exceptions carry no such
            // thing and are where the answer usually is.
            //
            // That distinction was learned the hard way. A user reported that Pulse sometimes
            // did not start, and his log said, nine times across two versions:
            //
            //     Unhandled exception on the UI thread  [XamlParseException: Provide value on
            //     'System.Windows.Baml2006.TypeConverterMarkupExtension' threw an exception.]
            //
            // Which says that converting some value in the markup failed and not one word about
            // which value or why. The inner exception is the whole of the useful content there,
            // and we were throwing it away to keep the line short.
            if (error != null)
            {
                line.Append("  [").Append(error.GetType().Name).Append(": ").Append(Redact(error.Message));

                // Bounded. A deeply wrapped exception should not be able to write a paragraph
                // into a log somebody has to read.
                var inner = error.InnerException;
                for (int depth = 0; inner != null && depth < 4; depth++, inner = inner.InnerException)
                    line.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(Redact(inner.Message));

                line.Append(']');
            }

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                RotateIfNeeded();
                File.AppendAllText(LogPath, line.AppendLine().ToString());
            }
        }
        catch
        {
            // A failure to log is not worth surfacing, and must never propagate into the
            // caller's error handling.
        }
    }

    /// <summary>
    /// Ages the log files along by one when the active file is full.
    ///
    /// pulse.log becomes pulse.1.log, pulse.1.log becomes pulse.2.log and so on, and only the
    /// oldest is dropped. Keeping a single previous file was not enough: a fault that takes a
    /// few sessions to reproduce would have had its own evidence overwritten by the sessions
    /// spent reproducing it.
    ///
    /// Caller holds <see cref="Gate"/>.
    /// </summary>
    private static void RotateIfNeeded()
    {
        try
        {
            var file = new FileInfo(LogPath);
            if (!file.Exists || file.Length < MaxBytes) return;

            // The oldest falls off the end. Everything else shifts up one, working backwards
            // so nothing is overwritten before it has been moved.
            var oldest = ArchivePath(MaxArchives);
            if (File.Exists(oldest)) File.Delete(oldest);

            for (int i = MaxArchives - 1; i >= 1; i--)
            {
                var from = ArchivePath(i);
                if (File.Exists(from)) File.Move(from, ArchivePath(i + 1), overwrite: true);
            }

            File.Move(LogPath, ArchivePath(1), overwrite: true);
        }
        catch
        {
            // Deliberately silent, and it has to be. This runs from inside Write, holding the
            // same lock, and the file is still over the size limit — so logging the failure
            // would call Write, which would call this again, which would fail again. The
            // consequence of staying quiet is a log file that grows past its limit, which is
            // visible on disk and harmless next to that recursion.
        }
    }

    // ── Sessions ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a session, and reports on the previous one if it never closed.
    ///
    /// Returns true when the last run ended unexpectedly, so the caller can note it. The
    /// distinction matters: until now a crash and an ordinary exit left identical logs, which
    /// is exactly why a user reporting "it just disappears" gave us nothing to work with.
    /// </summary>
    public static bool BeginSession(string version)
    {
        bool previousCrashed = false;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (File.Exists(SessionStatePath))
            {
                previousCrashed = true;

                string details;
                try   { details = File.ReadAllText(SessionStatePath).Replace(Environment.NewLine, " | ").Trim(); }
                catch { details = "(unreadable)"; }

                Warn(nameof(LogService),
                     $"Previous session ended without shutting down. It was: {details}");
            }

            Info(nameof(LogService), $"Session started. Pulse {version} on Windows {Environment.OSVersion.Version}.");
            RecordActivity("starting up");
        }
        catch
        {
            // Nowhere to report a logging failure except the log. The cost is that this run
            // will look like a crash next time, since the marker was never written, which is
            // the safe direction to be wrong in.
        }

        return previousCrashed;
    }

    /// Closes the session cleanly. Anything that skips this is treated as a crash next time.
    public static void EndSession()
    {
        try
        {
            Info(nameof(LogService), "Session ended cleanly.");
            lock (Gate)
            {
                if (File.Exists(SessionStatePath)) File.Delete(SessionStatePath);
            }
        }
        catch
        {
            // Same as above, and this runs during shutdown where there may be no time to write
            // anything anyway. A marker left behind reports a crash that did not happen, which
            // is the direction to err in for a crash detector.
        }
    }

    /// <summary>
    /// Notes what Pulse is doing, so a death that reaches no managed code still leaves a
    /// clue about where it happened.
    ///
    /// Overwrites rather than appends, so it costs one small write and never grows. Meant for
    /// moments worth naming — opening sensors, re-reading hardware after a GPU change — and
    /// deliberately not for every poll, which would be thousands of writes an hour and would
    /// age the real log out within one.
    /// </summary>
    public static void RecordActivity(string activity)
    {
        try
        {
            var text = new StringBuilder()
                .Append("started=").Append(SessionStart.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine()
                .Append("activity=").Append(Redact(activity)).AppendLine()
                .Append("at=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).AppendLine()
                .ToString();

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(SessionStatePath, text);
            }
        }
        catch
        {
            // Records what Pulse was doing, for the next run to report if this one dies. A
            // failure costs the detail, not the detection, and it cannot be logged for the
            // same reason as the rest of this file.
        }
    }

    private static readonly DateTime SessionStart = DateTime.Now;

    // ── Export ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes every log file we still hold, plus a summary of the machine and what Pulse
    /// currently believes about it, to one file the user can attach to a report.
    ///
    /// The summary matters as much as the logs. Half the questions asked in a bug report are
    /// "which GPU was it reading" and "was startup actually registered", and answering those
    /// by asking costs a day each time.
    /// </summary>
    public static string? Export()
    {
        try
        {
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"pulse-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var report = new StringBuilder()
                .AppendLine("Pulse diagnostics")
                .AppendLine("=================")
                .AppendLine($"Version   : {Safe(() => UpdateService.CurrentVersionLabel)}")
                .AppendLine($"Windows   : {Environment.OSVersion.Version}")
                .AppendLine($"Exe       : {Redact(Environment.ProcessPath)}")
                .AppendLine($"Exported  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"Running   : {Safe(() => (DateTime.Now - SessionStart).ToString(@"hh\:mm\:ss"))}");

            AppendHardware(report);
            AppendStartup(report);

            report.AppendLine();

            lock (Gate)
            {
                // Everything older than the recent detail, counted rather than reprinted.
                //
                // This used to paste all six log files in whole. That is up to six megabytes,
                // which is an awkward thing to ask somebody to send and a worse thing to read:
                // the one report that arrived this way carried thirteen days of routine lines,
                // and the nine crashes buried in it only came to light by scrolling.
                //
                // Counting them is strictly better for finding a pattern. "9 x Unhandled
                // exception on the UI thread, first 28 Aug, last 4 Sep" is the finding itself,
                // where the same nine lines spread across a megabyte are a needle in a haystack.
                var older = new List<string>();
                for (int i = MaxArchives; i >= 1; i--) older.Add(ArchivePath(i));

                AppendProblemSummary(report, older);

                // And the recent detail in full, because a summary cannot show sequence, and
                // sequence is what says whether the crash came before or after the thing that
                // caused it.
                AppendRecent(report, LogPath, "log (current)");

                if (File.Exists(SessionStatePath))
                {
                    report.AppendLine("--- session in progress ---");
                    report.AppendLine(Safe(() => File.ReadAllText(SessionStatePath)));
                }
            }

            File.WriteAllText(target, report.ToString());
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How much of the active log is reproduced word for word in an export.
    /// </summary>
    /// <remarks>
    /// Enough to hold several sessions, small enough to attach to an email without thinking
    /// about it. Anything older is counted instead, by AppendProblemSummary.
    /// </remarks>
    private const int ExportTailBytes = 256 * 1024;

    /// How many distinct problems the summary lists before it stops.
    private const int MaxSummaryLines = 30;

    /// <summary>
    /// Appends the end of a log file, and says so when it had to cut.
    /// </summary>
    private static void AppendRecent(StringBuilder report, string path, string label)
    {
        try
        {
            if (!File.Exists(path)) return;

            var text = File.ReadAllText(path);
            bool trimmed = text.Length > ExportTailBytes;

            if (trimmed)
            {
                text = text[^ExportTailBytes..];

                // From a line boundary, so the export never opens mid-sentence.
                int newline = text.IndexOf('\n');
                if (newline >= 0 && newline < text.Length - 1) text = text[(newline + 1)..];
            }

            report.AppendLine(trimmed
                ? $"--- {label}, most recent {ExportTailBytes / 1024} KB; anything older is counted above ---"
                : $"--- {label} ---");

            report.AppendLine(text);
        }
        catch (Exception ex)
        {
            report.AppendLine($"--- {label} ---");
            report.AppendLine($"(could not be read: {ex.GetType().Name}: {Redact(ex.Message)})");
            report.AppendLine();
        }
    }

    /// <summary>
    /// Counts the warnings and errors in the older log files, grouped by what they say.
    /// </summary>
    /// <remarks>
    /// Numbers are replaced with a # before grouping, so "stopped after 112s" and "stopped
    /// after 2971s" are recognised as the same event happening twice rather than as two
    /// separate curiosities. Timestamps, process ids and durations are exactly the parts that
    /// differ every time and never change what the line means.
    /// </remarks>
    private static void AppendProblemSummary(StringBuilder report, List<string> paths)
    {
        var seen  = new Dictionary<string, (int Count, string First, string Last, string Text)>(StringComparer.Ordinal);
        var runs  = new Dictionary<string, int>(StringComparer.Ordinal);
        int files = 0;
        string earliest = "", latest = "";

        foreach (var path in paths)
        {
            string[] lines;
            try
            {
                if (!File.Exists(path)) continue;
                lines = File.ReadAllLines(path);
                files++;
            }
            catch
            {
                continue;   // an unreadable archive is not worth failing the export over
            }

            foreach (var line in lines)
            {
                if (line.Length < 26) continue;

                var when = line[..Math.Min(19, line.Length)];
                if (when.Length == 19 && when[4] == '-' && when[7] == '-')
                {
                    if (earliest.Length == 0) earliest = when;
                    latest = when;
                }

                // Which versions ran, and how many times. This is INFO rather than a problem
                // and it is worth keeping anyway: a report covering an upgrade needs to say so,
                // and a fault that only appears in one version is only visible if the versions
                // are visible. One user's history spanned 1.1.3 and 1.2.0 and that mattered.
                int marker = line.IndexOf("Session started. Pulse ", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var tail    = line[(marker + 23)..];
                    int space   = tail.IndexOf(' ');
                    var version = space > 0 ? tail[..space] : tail;

                    runs[version] = runs.TryGetValue(version, out var n) ? n + 1 : 1;
                }

                if (line.IndexOf("  WARN ", StringComparison.Ordinal) < 0
                 && line.IndexOf("  ERROR", StringComparison.Ordinal) < 0) continue;

                var stamp = when;
                var rest  = line[Math.Min(25, line.Length)..].Trim();

                var key = DigitRuns.Replace(rest, "#");
                if (key.Length > 160) key = key[..160];

                if (seen.TryGetValue(key, out var at))
                    seen[key] = (at.Count + 1, at.First, stamp, at.Text);
                else
                    seen[key] = (1, stamp, stamp, rest.Length > 160 ? rest[..160] + "..." : rest);
            }
        }

        if (files == 0) return;

        report.AppendLine(earliest.Length > 0
            ? $"--- {files} older log file(s), {earliest} to {latest}, counted ---"
            : $"--- {files} older log file(s), counted ---");

        if (runs.Count > 0)
        {
            var versions = string.Join(", ", runs.OrderByDescending(r => r.Value)
                                                 .Select(r => $"{r.Key} x {r.Value}"));
            report.AppendLine($"sessions: {runs.Values.Sum()} ({versions})");
        }

        if (seen.Count == 0)
        {
            report.AppendLine("warnings and errors: none");
            report.AppendLine();
            return;
        }

        report.AppendLine("warnings and errors:");

        int shown = 0;
        foreach (var entry in seen.Values.OrderByDescending(v => v.Count))
        {
            if (shown++ == MaxSummaryLines)
            {
                report.AppendLine($"... and {seen.Count - MaxSummaryLines} more kinds, each rarer than these");
                break;
            }

            report.AppendLine($"{entry.Count,5} x  {entry.Text}");
            if (entry.Count > 1) report.AppendLine($"         first {entry.First}, last {entry.Last}");
        }

        report.AppendLine();
    }

    /// Runs of digits, so two occurrences of the same event group together whatever the
    /// duration, process id or exit code happened to be that time.
    private static readonly System.Text.RegularExpressions.Regex DigitRuns =
        new(@"\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void AppendFile(StringBuilder report, string path, string label)
    {
        try
        {
            if (!File.Exists(path)) return;
            report.AppendLine($"--- {label} ---");
            report.AppendLine(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // Into the report rather than the log. This runs to build the file someone attaches
            // to a bug report, so a section that is quietly absent is worse than useless: the
            // reader assumes there was nothing to say. Saying the file could not be read is
            // itself a useful piece of evidence, and it is a locked or unreadable file that
            // usually explains the rest.
            report.AppendLine($"--- {label} ---");
            report.AppendLine($"(could not be read: {ex.GetType().Name}: {Redact(ex.Message)})");
            report.AppendLine();
        }
    }

    /// What Pulse currently thinks the hardware is. Wrapped tightly: a diagnostics export
    /// must never fail because the thing it is describing is broken.
    private static void AppendHardware(StringBuilder report)
    {
        // Read from settings, not from the sensor layer, so the user's GPU choice is still
        // reported when the sensor layer is itself the thing that has failed. Losing the whole
        // section exactly when hardware is broken would drop the detail most worth having.
        var pinned = Safe(() => SettingsService.Instance.Settings.SelectedGpuId);
        report.AppendLine($"GPU choice: {(string.IsNullOrEmpty(pinned) ? "automatic" : pinned)}");

        // Asked directly rather than through the sensor layer, for the same reason as above.
        // When a graphics card is switched off this is the line that shows it happened, and it
        // is worth having even in a report where everything else about the hardware failed.
        // With their memory, because that is what decides whether an adapter reads as discrete
        // and it is the number a user can check against Task Manager themselves.
        report.AppendLine($"Adapters  : {Safe(DescribeAdapters)}");

        try
        {
            var hardware = HardwareService.Instance;

            report.AppendLine($"Sensors   : {(hardware.IsHardwareReady ? "ready" : "not ready")}"
                            + (hardware.HardwareFault is { Length: > 0 } fault ? $" ({Redact(fault)})" : ""));

            report.AppendLine($"Reading   : {Redact(hardware.ActiveGpuName is { Length: > 0 } active ? active : "no GPU selected")}");

            // The sensor host is where a hardware fault now lands, so its state is the first
            // thing worth knowing. A restart count above zero says a driver faulted, which is
            // invisible from the readings themselves once it has recovered.
            report.AppendLine($"Host      : {hardware.SensorHostStatus}");

            var gpus = hardware.AvailableGpus;
            report.AppendLine($"GPUs seen : {gpus.Count}");
            foreach (var gpu in gpus)
                report.AppendLine($"          - {gpu.Name} ({(gpu.IsDiscrete ? "discrete" : "integrated")}) [{gpu.Id}]");
        }
        catch (Exception ex)
        {
            report.AppendLine($"Sensors   : could not be read ({ex.GetType().Name})");
            report.AppendLine("GPUs seen : unavailable, because the sensor layer could not be reached");
        }
    }

    /// The startup task as Windows actually has it, not as settings claim.
    private static void AppendStartup(StringBuilder report)
    {
        try
        {
            var task = StartupTask.Query();
            var wanted = Safe(() => SettingsService.Instance.Settings.StartWithWindows.ToString());

            // The four states are spelled out, because "present" was the answer for a task
            // that had been switched off and for one that was working, and those are the two
            // cases a report of "Pulse does not start with Windows" has to be told apart.
            var described = task.Presence switch
            {
                StartupTask.TaskPresence.Missing    => "absent",
                StartupTask.TaskPresence.Enabled    => "present and enabled",
                StartupTask.TaskPresence.Disabled   => "present but DISABLED, so nothing will start",
                _                                   => "could not be read",
            };

            report.AppendLine($"Startup   : setting={wanted}, task={described}"
                            + (task.Exists ? $", settings {(task.SettingsCorrect ? "correct" : "OUTDATED")}" : ""));

            if (task.Exists) report.AppendLine($"          - runs {Redact(task.CommandPath)}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"Startup   : could not be read ({ex.GetType().Name})");
        }
    }


    /// Each graphics adapter Windows knows about, with the dedicated video memory it reports.
    private static string DescribeAdapters()
    {
        var adapters = DisplayAdapters.All();
        if (adapters.Count == 0) return "unknown";

        return string.Join(", ", adapters.Select(a =>
            $"{a.Description} ({a.DedicatedVideoMemoryMb:F0} MB)"));
    }

    private static string Safe(Func<string> read)
    {
        try   { return read() ?? ""; }
        catch { return "(unavailable)"; }
    }
}
