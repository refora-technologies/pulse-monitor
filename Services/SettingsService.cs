using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Models;

namespace Pulse.Services;

public class SettingsService
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private const string TaskName = "PulseMonitor";

    public AppSettings Settings { get; private set; } = AppSettings.Load();

    public event EventHandler? SettingsChanged;

    public void Save()
    {
        Settings.Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns whether the change actually took effect. The stored setting now reflects what
    /// Windows really did rather than what was asked for, so the toggle cannot sit there
    /// claiming Pulse starts with Windows when creating the task failed.
    /// </summary>
    public bool UpdateStartWithWindows(bool enabled)
    {
        // Pulse runs elevated, so an HKCU Run entry would trigger a UAC prompt on every
        // logon. A scheduled task with highest privileges starts it silently instead.
        RemoveLegacyRunEntry();

        bool succeeded;

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            succeeded = !string.IsNullOrEmpty(exePath) &&
                RunSchTasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --startup\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
        {
            succeeded = RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        }

        Settings.StartWithWindows = succeeded ? enabled : !enabled;
        Save();
        return succeeded;
    }

    /// <summary>
    /// Reconciles settings.json with the real scheduled task, and repairs the task when it
    /// points at a build that is no longer here.
    ///
    /// Every version of Pulse shares one task name and the task stores an absolute exe path,
    /// so whichever build last had the toggle switched on owned startup permanently — even
    /// after being replaced or deleted. Users saw an old version launching at logon, or
    /// nothing launching at all, while Pulse still reported startup as enabled.
    /// </summary>
    /// Safe to call from a background thread, and meant to be: it shells out to schtasks
    /// twice, which has no business sitting on the startup path. Returns true when the
    /// caller should Save() — done by the caller so SettingsChanged still fires on the UI
    /// thread, where its subscribers touch bound collections.
    public bool ReconcileStartupTask()
    {
        var taskPath = GetTaskTargetPath();
        bool exists  = taskPath != null;

        if (exists)
        {
            // Repoint a task left behind by another install at the build actually running.
            var current = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(current) &&
                taskPath!.IndexOf(current, StringComparison.OrdinalIgnoreCase) < 0)
            {
                RunSchTasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{current}\\\" --startup\" /SC ONLOGON /RL HIGHEST /F");
            }
        }

        if (Settings.StartWithWindows == exists) return false;

        Settings.StartWithWindows = exists;
        return true;
    }

    /// <summary>
    /// The command line the startup task runs, or null when there is no such task.
    ///
    /// /V /FO LIST rather than /XML because /XML comes back as UTF-16, which does not
    /// survive being read as redirected standard output here.
    /// </summary>
    private static string? GetTaskTargetPath()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName        = "schtasks.exe",
                Arguments       = $"/Query /TN \"{TaskName}\" /V /FO LIST",
                CreateNoWindow  = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });

            if (process is null) return null;

            // Read before waiting: a full pipe buffer would otherwise block the child and
            // leave us waiting out the timeout for output that is already there.
            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("PulseMonitor", false);
        }
        catch { }
    }

    /// <summary>
    /// Returns whether schtasks actually succeeded. Callers used to assume it did and record
    /// the requested state either way, so a failure left Pulse claiming "start with Windows"
    /// was on when no task existed.
    /// </summary>
    private static bool RunSchTasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName        = "schtasks.exe",
                Arguments       = arguments,
                CreateNoWindow  = true,
                UseShellExecute = false,
            });

            if (process is null) return false;

            if (!process.WaitForExit(5000))
            {
                // Never leave an orphan holding a console handle.
                try { process.Kill(true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
