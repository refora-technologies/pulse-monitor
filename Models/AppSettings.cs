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

    /// <summary>
    /// Legacy absolute position in WPF device-independent units. Superseded by
    /// OverlayMonitorId + OverlayAnchorFx/Fy and migrated once on first run; -1 means retired.
    /// </summary>
    public double OverlayCustomX { get; set; } = -1;
    public double OverlayCustomY { get; set; } = -1;

    /// <summary>
    /// Device name of the monitor a custom position belongs to, e.g. \\.\DISPLAY1. Stored
    /// instead of an index into Screen.AllScreens, whose order shifts when displays are
    /// added, removed or reordered — which used to move the overlay to a different monitor.
    /// </summary>
    public string OverlayMonitorId { get; set; } = "";

    /// <summary>
    /// Where the overlay sits within the space it has to move in, as a fraction from 0 to 1.
    /// -1 means no custom position has been set.
    ///
    /// Deliberately a fraction of (work area - overlay size) rather than a pixel offset.
    /// A pixel offset does not survive a resolution change: a position 1800px across a
    /// 2560-wide screen has to be clamped to the edge at 1280 wide, so the overlay comes
    /// back somewhere unrelated to where it was put. Storing the fraction of available room
    /// keeps 0 pinned to the left edge, 1 pinned to the right, and everything between
    /// proportionally where the user left it — at any resolution, and at any overlay size.
    /// </summary>
    public double OverlayAnchorFx { get; set; } = -1;
    public double OverlayAnchorFy { get; set; } = -1;
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

    private static string BackupPath => SettingsPath + ".bak";

    public static AppSettings Load()
    {
        // The backup is only reached if the main file is missing or unreadable, which is
        // what an interrupted write leaves behind.
        return TryLoad(SettingsPath) ?? TryLoad(BackupPath) ?? new AppSettings();
    }

    private static AppSettings? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path), LoadSettings);
            if (settings is null) return null;

            settings.Sanitise();
            return settings;
        }
        catch (Exception ex)
        {
            // Worth recording: this is the path where a user silently loses every
            // preference, and without a trace it looks like Pulse simply forgot them.
            Services.LogService.Error(nameof(AppSettings), $"Could not read settings from {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Repairs anything the app cannot sensibly run with. A hand-edited, truncated or
    /// downgrade-written settings file used to be able to produce a zero polling interval,
    /// an invisible overlay or a null tile list, and the resulting failure surfaced far away
    /// from the cause.
    /// </summary>
    private void Sanitise()
    {
        ActiveTileIds    = ActiveTileIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();
        TileOrder        = TileOrder?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList()     ?? new List<string>();
        SelectedGpuId  ??= "";
        OverlayMonitorId ??= "";

        // Anything outside 0..1 is not a fraction we wrote, so treat the position as unset
        // rather than trying to salvage it.
        if (!IsFraction(OverlayAnchorFx) || !IsFraction(OverlayAnchorFy))
        {
            OverlayAnchorFx = -1;
            OverlayAnchorFy = -1;
        }

        static bool IsFraction(double v) => double.IsFinite(v) && v >= 0 && v <= 1;

        OverlayOpacity         = Clamp(OverlayOpacity, 0.15, 1.0, 0.85);
        OverlayScale           = Clamp(OverlayScale, 0.5, 3.0, 1.0);
        PollingIntervalSeconds = Clamp(PollingIntervalSeconds, 0.5, 60.0, 2.0);

        if (SelectedMonitorIndex < 0) SelectedMonitorIndex = 0;

        if (OverlayPosition is not ("TopLeft" or "TopRight" or "BottomLeft" or "BottomRight" or "Custom"))
            OverlayPosition = "TopRight";
    }

    /// Out-of-range values fall back to the default rather than to the nearest bound: a 0 or
    /// NaN opacity means the file is wrong, not that the user wanted an invisible overlay.
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) && value >= min && value <= max ? value : fallback;

    /// <summary>
    /// Writes via a temporary file and File.Replace, so a crash or power loss mid-write
    /// cannot leave a truncated settings file — previously the only copy was overwritten in
    /// place, and losing it reset every preference silently.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);

            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            var temp = SettingsPath + ".tmp";

            File.WriteAllText(temp, json);

            if (File.Exists(SettingsPath))
                File.Replace(temp, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, SettingsPath);
        }
        catch (Exception ex)
        {
            Services.LogService.Error(nameof(AppSettings), "Could not save settings", ex);
        }
    }
}
