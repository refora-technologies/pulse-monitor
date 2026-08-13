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

    public void UpdateStartWithWindows(bool enabled)
    {
        Settings.StartWithWindows = enabled;

        // Pulse runs elevated, so an HKCU Run entry would trigger a UAC prompt on every
        // logon. A scheduled task with highest privileges starts it silently instead.
        RemoveLegacyRunEntry();

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                RunSchTasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --startup\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
        {
            RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        }

        Save();
    }

    /// <summary>
    /// Corrects Settings.StartWithWindows if it disagrees with the actual scheduled task —
    /// e.g. the installer's "Start Pulse when Windows starts" checkbox creates the task
    /// directly without touching settings.json. Only updates our own record; never touches
    /// the task itself, so it can't change real startup behavior.
    /// </summary>
    public void SyncStartWithWindowsFromSystem()
    {
        bool exists = TaskExists();
        if (Settings.StartWithWindows != exists)
        {
            Settings.StartWithWindows = exists;
            Save();
        }
    }

    private static bool TaskExists()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName        = "schtasks.exe",
                Arguments       = $"/Query /TN \"{TaskName}\"",
                CreateNoWindow  = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
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

    private static void RunSchTasks(string arguments)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName        = "schtasks.exe",
                Arguments       = arguments,
                CreateNoWindow  = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(5000);
        }
        catch { }
    }
}
