using Newtonsoft.Json;

namespace Pulse.Models;

/// <summary>
/// Every setting, its default, and the repair of values nobody could have meant.
/// </summary>
/// <remarks>
/// Split in two on 18 Nov 2026 for the Linux version. This half is the shared core
/// (Core/Pulse.Core.csproj): the settings themselves and the file format, so every Pulse reads
/// and writes the same settings.json. AppSettings.Windows.cs is where the file lives on Windows,
/// how it is read patiently and saved safely, and the keyboard shortcuts, whose key names are
/// WPF's.
/// </remarks>
public partial class AppSettings
{
    public List<string> ActiveTileIds { get; set; } = new() { "cpu_usage", "cpu_temp", "gpu_usage", "gpu_temp", "ram_used" };

    /// Every known tile id in the user's chosen display order, including ones that are
    /// currently switched off. Empty means "use the catalog order". Tiles missing from
    /// this list (because a newer version added them) are appended on load.
    public List<string> TileOrder { get; set; } = new();
    public double OverlayOpacity { get; set; } = 0.85;

    /// <summary>
    /// How solid the overlay's panel is, separately from OverlayOpacity which fades the whole
    /// thing including the readings. Taken to zero this leaves the values floating on the
    /// screen with no panel behind them. 1 is the original look.
    /// </summary>
    public double OverlayBackgroundOpacity { get; set; } = 1.0;

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
    /// The bounds of the display the overlay was placed on, as "left,top,width,height".
    ///
    /// A second way to recognise that display, because the first one is not dependable.
    /// OverlayMonitorId holds a Windows device name like \\.\DISPLAY1, and Windows renumbers
    /// those when the set of graphics adapters changes: a reporter's log shows the same panel
    /// going from DISPLAY1 to DISPLAY11 and back across a switch between integrated and
    /// discrete graphics. Once the name stops matching, the overlay falls back to whichever
    /// display it happens to be on, which on a multi-monitor machine means it moves.
    /// </summary>
    public string OverlayMonitorBounds { get; set; } = "";

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

    /// <summary>
    /// The bounds of the display chosen for a corner position, as "left,top,width,height".
    /// </summary>
    /// <remarks>
    /// Corner positions used to find their display by SelectedMonitorIndex alone, an index into
    /// Screen.AllScreens. That list is in Windows' own order, and Windows renumbers displays
    /// when the graphics adapters change, so the index can land on a different screen. Dragged
    /// positions were already protected against this with OverlayMonitorBounds; this does the
    /// same for corners. Empty in settings from before 1.3.2, which then behave as they always
    /// did.
    /// </remarks>
    public string PresetMonitorBounds { get; set; } = "";

    /// <summary>
    /// Whether Pulse registers its keyboard shortcuts with Windows.
    /// </summary>
    /// <remarks>
    /// Off out of the box, and the defaults below are still filled in. That combination is
    /// deliberate: the suggested combinations are visible so the feature is discoverable, and
    /// nothing is taken from the system until someone asks for it. A global shortcut is not a
    /// preference like a colour — it stops that combination reaching every other application on
    /// the machine — so it is not ours to switch on for someone who never went looking.
    ///
    /// Tools that live inside a game launcher do ship defaults, and monitoring tools that run
    /// all day do not. Pulse is the second kind.
    /// </remarks>
    public bool ShortcutsEnabled { get; set; } = false;

    /// <summary>
    /// The combinations, written the way they are shown: "Alt+Shift+X".
    /// </summary>
    /// Text rather than a packed number so that someone opening settings.json can read what
    /// their shortcuts are, and repair one by hand if it comes to that. An empty string means
    /// the row is deliberately unassigned, which is different from the key being missing.
    public string ShortcutToggleOverlay { get; set; } = "Alt+Shift+X";
    public string ShortcutToggleControlPanel { get; set; } = "Alt+Shift+V";
    public string ShortcutToggleCompactMode { get; set; } = "Alt+Shift+C";

    public bool ShowMaxValues { get; set; } = false;

    public double OverlayScale { get; set; } = 1.0;

    /// <summary>
    /// Show network speed in megabits per second rather than megabytes.
    /// </summary>
    /// <remarks>
    /// Both are the same measurement written differently, and which one somebody wants depends
    /// entirely on what they are comparing against. Task Manager, speed tests and internet
    /// plans all speak megabits; browsers, game launchers and file copies all speak megabytes.
    /// Whichever Pulse picked on its own, half its users would be doing arithmetic in their
    /// head, and the eight times difference reads like a bug rather than a unit.
    ///
    /// Off by default, so an update never changes the numbers somebody is already used to.
    /// </remarks>
    public bool NetworkUnitBits { get; set; } = false;

    /// LibreHardwareMonitor identifier of the GPU to monitor. Empty means auto-detect.
    public string SelectedGpuId { get; set; } = "";

    // ── In-game overlay (1.3.5) ─────────────────────────────────────────────────────────
    //
    // Pulse's readings drawn inside games, so they stay visible in full-screen games where the
    // window overlay cannot. Off by default: turning it on puts a small part of Pulse into the
    // game, and the player says yes to that first. Never in games with anti-cheat.

    /// Whether Pulse draws its overlay inside games.
    public bool InGameOverlayEnabled { get; set; } = false;

    /// Off: the in-game overlay follows the window overlay's position, size and opacity. On: it
    /// uses the InGame* values below. On by default, with the owner's look (4 Nov 2026): one
    /// straight line at the top centre of the screen, flush with its edge like a camera notch.
    public bool InGameOwnLook { get; set; } = true;

    /// "List" (one reading per line, as the window overlay's compact mode) or "Bar" (all on one
    /// straight line).
    public string InGameLayout { get; set; } = "Bar";

    /// Where it sits on the game's screen, as a fraction of the free space: 0 left or top, 1 right
    /// or bottom, the same idea as OverlayAnchorFx. Top centre by default.
    public double InGameAnchorX { get; set; } = 0.5;
    public double InGameAnchorY { get; set; } = 0;

    /// The same three as the window overlay's, with the same defaults: slightly see-through, so
    /// the game shows through.
    public double InGameOpacity { get; set; } = 0.85;
    public double InGameBackgroundOpacity { get; set; } = 1.0;
    public double InGameScale { get; set; } = 1.0;

    /// Off: the same readings as the window overlay. On: InGameTileIds, a list of its own, so a
    /// gaming set does not have to be the desktop set. On by default with five gaming readings:
    /// the default bar runs across the top of the screen, and the window overlay's list could be
    /// too long for one line.
    public bool InGameOwnTiles { get; set; } = true;
    public List<string> InGameTileIds { get; set; } = new() { "fps", "fps_1low", "gpu_temp", "gpu_usage", "cpu_temp" };

    /// The order of every reading in that list, chosen or not, as the panel shows it; any not
    /// named follow in the window overlay's order. Frame rate first by default. Empty (after
    /// "Reset order") means the window overlay's order.
    public List<string> InGameTileOrder { get; set; } = new() { "fps", "fps_1low", "gpu_temp", "gpu_usage", "cpu_temp" };

    /// Games the player turned it off for, by executable name ("game.exe"), compared without case.
    public List<string> InGameExcludedGames { get; set; } = new();

    /// Games Pulse did not recognise and the player said are games ("Show Pulse in this game"),
    /// by executable name. Anti-cheat still keeps the overlay out of them.
    public List<string> InGameIncludedGames { get; set; } = new();

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

    /// <summary>
    /// True when a settings file exists but could not be read, so these are defaults standing
    /// in for it. Such settings are never written to disk; see Load.
    /// </summary>
    [JsonIgnore]
    public bool ReadFailed { get; private set; }

    /// <summary>
    /// Settings from the text of a settings file, repaired, or null when the text holds none.
    /// </summary>
    /// Throws when the text is not JSON at all; the caller says which file that was.
    public static AppSettings? FromJson(string text)
    {
        var settings = JsonConvert.DeserializeObject<AppSettings>(text, LoadSettings);
        settings?.Sanitise();
        return settings;
    }

    /// The settings as the file holds them: indented, so someone can read and repair it by hand.
    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

    /// The parts of the repair that belong to one platform: on Windows, the keyboard shortcuts.
    partial void SanitisePlatform();

    /// <summary>
    /// Repairs anything the app cannot sensibly run with. A hand-edited, truncated or
    /// downgrade-written settings file used to be able to produce a zero polling interval,
    /// an invisible overlay or a null tile list, and the resulting failure surfaced far away
    /// from the cause.
    /// </summary>
    private void Sanitise()
    {
        // Ids that match no tile are deliberately kept rather than dropped.
        //
        // They are already harmless: the overlay looks each one up and skips what it cannot
        // find. Removing them here would look tidier and would quietly destroy something —
        // a file written by a newer Pulse, opened once by an older one, would come back with
        // the newer build's tiles gone for good. Blank entries are removed because those are
        // never anyone's selection.
        ActiveTileIds    = ActiveTileIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();
        TileOrder        = TileOrder?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList()     ?? new List<string>();
        SelectedGpuId  ??= "";
        OverlayMonitorId ??= "";
        PresetMonitorBounds ??= "";

        // Anything outside 0..1 is not a fraction we wrote, so treat the position as unset
        // rather than trying to salvage it.
        if (!IsFraction(OverlayAnchorFx) || !IsFraction(OverlayAnchorFy))
        {
            OverlayAnchorFx = -1;
            OverlayAnchorFy = -1;
        }

        static bool IsFraction(double v) => double.IsFinite(v) && v >= 0 && v <= 1;

        OverlayOpacity         = Clamp(OverlayOpacity, 0.15, 1.0, 0.85);
        // Unlike OverlayOpacity this is allowed all the way to zero: a fully transparent
        // panel is the point of it, and the readings stay visible on their own.
        OverlayBackgroundOpacity = Clamp(OverlayBackgroundOpacity, 0.0, 1.0, 1.0);
        OverlayScale           = Clamp(OverlayScale, 0.5, 3.0, 1.0);
        PollingIntervalSeconds = Clamp(PollingIntervalSeconds, 0.5, 60.0, 2.0);

        if (SelectedMonitorIndex < 0) SelectedMonitorIndex = 0;

        // Keyboard shortcuts are Windows key names, so the Windows half repairs them.
        SanitisePlatform();

        if (OverlayPosition is not ("TopLeft" or "TopRight" or "BottomLeft" or "BottomRight" or "Custom"))
            OverlayPosition = "TopRight";

        if (InGameLayout is not ("List" or "Bar")) InGameLayout = "List";
        InGameAnchorX           = Clamp(InGameAnchorX, 0.0, 1.0, 0.0);
        InGameAnchorY           = Clamp(InGameAnchorY, 0.0, 1.0, 0.0);
        InGameOpacity           = Clamp(InGameOpacity, 0.15, 1.0, 0.85);
        InGameBackgroundOpacity = Clamp(InGameBackgroundOpacity, 0.0, 1.0, 1.0);
        InGameScale             = Clamp(InGameScale, 0.5, 3.0, 1.0);
        InGameTileIds           = InGameTileIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();
        InGameTileOrder         = InGameTileOrder?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();
        InGameExcludedGames     = InGameExcludedGames?.Where(n => !string.IsNullOrWhiteSpace(n))
                                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        InGameIncludedGames     = InGameIncludedGames?.Where(n => !string.IsNullOrWhiteSpace(n))
                                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
    }

    /// Out-of-range values fall back to the default rather than to the nearest bound: a 0 or
    /// NaN opacity means the file is wrong, not that the user wanted an invisible overlay.
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) && value >= min && value <= max ? value : fallback;


}
