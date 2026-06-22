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
                RunSchTasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
        {
            RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        }

        Save();
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
