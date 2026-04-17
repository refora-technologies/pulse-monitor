using Microsoft.Win32;
using Pulse.Models;

namespace Pulse.Services;

public class SettingsService
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (enabled)
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location
                    .Replace(".dll", ".exe");
                key?.SetValue("PulseMonitor", $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue("PulseMonitor", false);
            }
        }
        catch { }
        Save();
    }
}
