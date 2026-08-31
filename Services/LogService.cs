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

            // Type and message only. A full stack trace can carry file paths from the build
            // machine, and this file is meant to be pasteable into a public issue.
            if (error != null) line.Append("  [").Append(error.GetType().Name).Append(": ").Append(Redact(error.Message)).Append(']');

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
        catch { }
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
        catch { }

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
        catch { }
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
        catch { }
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
                // Oldest first, so the file reads forwards in time.
                for (int i = MaxArchives; i >= 1; i--) AppendFile(report, ArchivePath(i), $"log {i} (older)");
                AppendFile(report, LogPath, "log (current)");

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

    private static void AppendFile(StringBuilder report, string path, string label)
    {
        try
        {
            if (!File.Exists(path)) return;
            report.AppendLine($"--- {label} ---");
            report.AppendLine(File.ReadAllText(path));
        }
        catch { }
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
        report.AppendLine($"Adapters  : {Safe(() => DisplayAdapters.Describe(DisplayAdapters.Signature()))}");

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

            report.AppendLine($"Startup   : setting={wanted}, task={(task.Exists ? "present" : "absent")}"
                            + (task.Exists ? $", settings {(task.SettingsCorrect ? "correct" : "OUTDATED")}" : ""));

            if (task.Exists) report.AppendLine($"          - runs {Redact(task.CommandPath)}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"Startup   : could not be read ({ex.GetType().Name})");
        }
    }

    private static string Safe(Func<string> read)
    {
        try   { return read() ?? ""; }
        catch { return "(unavailable)"; }
    }
}
