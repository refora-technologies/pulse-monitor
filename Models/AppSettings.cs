using System.IO;
using Newtonsoft.Json;

namespace Pulse.Models;

public class AppSettings
{
    public List<string> ActiveTileIds { get; set; } = new() { "cpu_usage", "cpu_temp", "gpu_usage", "gpu_temp", "ram_used" };

    /// Every known tile id in the user's chosen display order, including ones that are
    /// currently switched off. Empty means "use the catalog order". Tiles missing from
    /// this list (because a newer version added them) are appended on load.
    public List<string> TileOrder { get; set; } = new();
    public double OverlayOpacity { get; set; } = 0.85;
    public string OverlayPosition { get; set; } = "TopRight"; // TopLeft, TopRight, BottomLeft, BottomRight, Custom
    public double OverlayCustomX { get; set; } = -1;
    public double OverlayCustomY { get; set; } = -1;
    public double PollingIntervalSeconds { get; set; } = 2;
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;

    // v1.1 — Redesign additions
    public bool IsCompactMode { get; set; } = false;
    public bool IsDragEnabled { get; set; } = false;
    public int SelectedMonitorIndex { get; set; } = 0;
    public bool ShowMaxValues { get; set; } = false;
    public double OverlayScale { get; set; } = 1.0;

    /// LibreHardwareMonitor identifier of the GPU to monitor. Empty means auto-detect.
    public string SelectedGpuId { get; set; } = "";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Refora", "Pulse", "settings.json");

    /// <summary>
    /// Newtonsoft defaults to ObjectCreationHandling.Auto, which reuses the list created
    /// by a property initialiser and *appends* the saved values to it instead of
    /// replacing them. That silently re-added the five default tiles on every launch, so
    /// those tiles could never be turned off and ActiveTileIds grew on every run.
    /// Replace is the correct behaviour for settings.
    /// </summary>
    private static readonly JsonSerializerSettings LoadSettings = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json     = File.ReadAllText(SettingsPath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json, LoadSettings) ?? new AppSettings();

                // Existing installs already have duplicates written to disk from the old
                // behaviour, so clean them up on the way in.
                if (settings.ActiveTileIds.Count > 0)
                    settings.ActiveTileIds = settings.ActiveTileIds.Distinct().ToList();

                return settings;
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
