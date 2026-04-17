using System.IO;
using Newtonsoft.Json;

namespace Pulse.Models;

public class AppSettings
{
    public List<string> ActiveTileIds { get; set; } = new() { "cpu_temp", "gpu_temp", "gpu_power", "ram_used", "gpu_usage" };
    public double OverlayOpacity { get; set; } = 0.85;
    public string OverlayPosition { get; set; } = "TopRight"; // TopLeft, TopRight, BottomLeft, BottomRight, Custom
    public double OverlayCustomX { get; set; } = -1;
    public double OverlayCustomY { get; set; } = -1;
    public double PollingIntervalSeconds { get; set; } = 2;
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;

    // v1.1 — Redesign additions
    public bool IsCompactMode { get; set; } = false;   // MSI Afterburner-style HUD
    public bool IsDragEnabled { get; set; } = false;    // Unlock overlay for repositioning

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Refora", "Pulse", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}
