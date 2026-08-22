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
/// Deliberately not a logging framework. One file, capped, no dependencies, and every
/// method silent on failure — logging must never become a source of faults itself.
/// </summary>
public static class LogService
{
    private const long MaxBytes = 1024 * 1024;   // rotate at 1 MB, keep one previous file

    private static readonly object Gate = new();

    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Refora", "Pulse", "logs");

    public static string LogPath      => Path.Combine(Directory, "pulse.log");
    private static string PreviousPath => Path.Combine(Directory, "pulse.previous.log");

    public static void Info (string source, string message)              => Write(LogLevel.Info,  source, message, null);
    public static void Warn (string source, string message)              => Write(LogLevel.Warn,  source, message, null);
    public static void Error(string source, string message, Exception e) => Write(LogLevel.Error, source, message, e);

    private static void Write(LogLevel level, string source, string message, Exception? error)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("  ").Append(level.ToString().ToUpperInvariant().PadRight(5))
                .Append("  ").Append(source.PadRight(18))
                .Append("  ").Append(message);

            // Type and message only. A full stack trace can carry file paths from the build
            // machine, and this file is meant to be pasteable into a public issue.
            if (error != null) line.Append("  [").Append(error.GetType().Name).Append(": ").Append(error.Message).Append(']');

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

    /// Caller holds <see cref="Gate"/>.
    private static void RotateIfNeeded()
    {
        try
        {
            var file = new FileInfo(LogPath);
            if (!file.Exists || file.Length < MaxBytes) return;

            if (File.Exists(PreviousPath)) File.Delete(PreviousPath);
            File.Move(LogPath, PreviousPath);
        }
        catch { }
    }

    /// <summary>
    /// Writes both log files, plus a short machine summary, to a single file the user can
    /// attach to a report. Returns the path, or null if it could not be produced.
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
                .AppendLine($"Version   : {UpdateService.CurrentVersionLabel}")
                .AppendLine($"Windows   : {Environment.OSVersion.Version}")
                .AppendLine($"Exe       : {Environment.ProcessPath}")
                .AppendLine($"Exported  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine();

            lock (Gate)
            {
                foreach (var (label, path) in new[] { ("previous", PreviousPath), ("current", LogPath) })
                {
                    if (!File.Exists(path)) continue;
                    report.AppendLine($"--- {label} log ---");
                    report.AppendLine(File.ReadAllText(path));
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
}
