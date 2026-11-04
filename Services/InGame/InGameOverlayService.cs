using System.Diagnostics;
using System.Runtime.InteropServices;

using Pulse.Models;
using Pulse.ViewModels;

namespace Pulse.Services.InGame;

/// <summary>
/// The in-game overlay, from Pulse's side: sends it the readings, decides which game it goes into,
/// loads it there once, and hides the window overlay while the in-game one is drawing in the game
/// in front, so there is always one overlay, never none and never two.
/// </summary>
/// <remarks>
/// Twice a second, off the UI thread except for a quick look at the tiles. The slow checks (a
/// game's libraries, its folder) run once per game and are remembered.
/// </remarks>
public sealed class InGameOverlayService : IDisposable
{
    private static readonly Lazy<InGameOverlayService> LazyInstance = new(() => new InGameOverlayService());
    public static InGameOverlayService Instance => LazyInstance.Value;

    /// <summary>What the status line under the toggle says. Empty while the feature is off.</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>The executable of the game in front, when there is one: for "turn off for this game".</summary>
    public string? CurrentGame { get; private set; }

    public event EventHandler? StatusChanged;

    private readonly InGameBlockWriter _writer = new();
    private readonly Dictionary<int, (GameInfo Info, DateTime CheckedAt)> _checked = new();
    private readonly HashSet<int> _loaded = new();
    private readonly HashSet<int> _loadFailed = new();
    private readonly Dictionary<int, DateTime> _firstSeen = new();
    private readonly List<TileViewModel> _ownTiles = new();
    private string _ownTileKey = "";
    private System.Threading.Timer? _timer;
    private bool _suppressing;
    private GameInfo? _lastGame;
    private (int Pid, IntPtr Handle)? _shownWindow;
    private int _ticking;
    private volatile bool _disposed;

    private InGameOverlayService()
    {
        // A reading that becomes readable, or stops, changes which of the in-game list can show.
        SensorDriver.Instance.Changed += (_, _) => _ownTileKey = "";
    }

    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
    }

    private void Tick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;  // never two at once
        try { TickOnce(); }
        catch (Exception ex) { LogService.Error(nameof(InGameOverlayService), "The in-game overlay's update failed", ex); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void TickOnce()
    {
        var settings = SettingsService.Instance.Settings;
        var app = System.Windows.Application.Current as App;
        if (app == null) return;

        if (!settings.InGameOverlayEnabled)
        {
            // Told to draw nothing, in case a game still has it loaded from earlier.
            _writer.Write(Empty);
            Suppress(app, false);
            _lastGame = null;
            SetStatus("", null);
            return;
        }

        // The tiles and the player's show/hide choice live on the UI thread; a quick look.
        (InGameFrame Frame, bool Visible)? snapshot = null;
        // Hidden content (Suppress) leaves the window shown, so this stays the player's own choice.
        app.Dispatcher.Invoke(() => snapshot = (BuildFrame(settings), app.IsOverlayVisible));
        var (frame, visible) = snapshot!.Value;

        int pid = ForegroundProcessId();
        GameInfo? found = pid == 0 || pid == Environment.ProcessId ? null : CheckGame(pid, settings);
        if (found is not { Verdict: not (Verdict.NotAGame or Verdict.NotOpenable) } info)
        {
            // Nothing that counts as a game is in front: the desktop, a browser, this panel, a
            // click on the other screen. The game the overlay was showing in keeps it for as long
            // as the game is still on screen (owner, 4 Nov): the window overlay comes back only
            // once the game is minimised or hidden.
            if (_lastGame is { Verdict: Verdict.Allowed } shown && _shownWindow is { } window && window.Pid == shown.Pid
                && !IsExcluded(shown, settings) && IsRunning(shown.Pid) && OnScreen(window.Handle))
            {
                DrawIn(app, frame, visible, shown);
                return;
            }

            // No game draws, and the window overlay shows.
            _writer.Write(frame with { Enabled = false });
            Suppress(app, false);

            // The last game is still spoken of while it runs, so the panel opened over it can
            // say what happened there and offer to turn the overlay off for it.
            if (_lastGame is { } last && IsRunning(last.Pid))
            {
                bool excluded = IsExcluded(last, settings);
                bool goesIn = last.Verdict is Verdict.Allowed or Verdict.Excluded;
                SetStatus(excluded ? $"Turned off for {last.Name}"
                        : goesIn ? $"Ready for {last.Name}: it shows when you go back to the game"
                        : NotInText(last), goesIn || excluded ? ExeName(last) : null);
            }
            else
            {
                _lastGame = null;
                SetStatus("Waiting for a game", null);
            }
            return;
        }

        _lastGame = info;
        if (info.Verdict != Verdict.Allowed)
        {
            _writer.Write(frame with { Enabled = false });
            Suppress(app, false);
            // Offered only where it would change something: a game it already stays out of has
            // nothing to turn off.
            // A game loads DirectX a while after it starts: not yet is not "does not".
            bool starting = info.Verdict == Verdict.NoDirectX && _firstSeen.TryGetValue(pid, out var seen)
                            && DateTime.UtcNow - seen < TimeSpan.FromMinutes(1);
            SetStatus(starting ? $"Waiting for {info.Name} to start" : NotInText(info),
                      info.Verdict == Verdict.Excluded ? ExeName(info) : null);
            return;
        }

        if (!_loaded.Contains(pid) && !_loadFailed.Contains(pid))
        {
            var problem = Load(pid);
            if (problem == null) _loaded.Add(pid);
            else
            {
                _loadFailed.Add(pid);
                LogService.Warn(nameof(InGameOverlayService), $"Could not put the in-game overlay into {info.Name}: {problem}.");
            }
        }

        if (_loadFailed.Contains(pid))
        {
            _writer.Write(frame with { Enabled = false });
            Suppress(app, false);
            SetStatus($"Not in {info.Name}: Pulse could not add it to this game", null);
            return;
        }

        // Remembered, to keep the overlay in this game while it stays on screen behind something else.
        _shownWindow = (pid, GetForegroundWindow());
        DrawIn(app, frame, visible, info);
    }

    /// <summary>
    /// The game that draws: only this one, any other with the overlay loaded keeps quiet. The
    /// window overlay hides once the game says it is drawing, and not before.
    /// </summary>
    private void DrawIn(App app, InGameFrame frame, bool visible, GameInfo info)
    {
        int pid = info.Pid;
        _writer.Write(frame with { Enabled = visible, TargetPid = (uint)pid });

        var status = InGameLoader.ReadStatus(pid);
        bool fresh = status is { AgeMs: < 1500 };
        bool drawing = fresh && status!.Value.State == InGameLoader.State.Drawing;
        Suppress(app, drawing);
        SetStatus(!fresh ? $"Starting in {info.Name}" : status!.Value.State switch
        {
            InGameLoader.State.Drawing => $"Showing in {info.Name}",
            InGameLoader.State.Hidden => $"Ready in {info.Name}",
            InGameLoader.State.NotDirectX => $"Not in {info.Name}: it does not use DirectX 11 or 12",
            InGameLoader.State.SwitchedOff => $"Stopped in {info.Name} after an error; the window overlay is shown",
            _ => $"Starting in {info.Name}",
        }, ExeName(info));
    }

    /// Why the overlay is not in a game, for the status line.
    private static string NotInText(GameInfo info) => info.Verdict switch
    {
        Verdict.Excluded => $"Turned off for {info.Name}",
        Verdict.AntiCheat => $"Not in {info.Name}: {info.Detail}, so the window overlay is shown",
        _ => $"Not in {info.Name}: {info.Detail}",
    };

    private string? Load(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (InGameLoader.IsLoaded(process)) return null;  // already there: from earlier, or another Pulse run
        }
        catch (ArgumentException) { return "the game closed"; }

        var problem = InGameLoader.Load(pid);
        if (problem == null) LogService.Info(nameof(InGameOverlayService), $"In-game overlay added to process {pid}.");
        return problem;
    }

    /// The verdict for a game, remembered: a game's folder and anti-cheat do not change while it runs.
    /// "No DirectX yet" is asked again for a while, as a game loads its graphics after starting.
    private GameInfo CheckGame(int pid, AppSettings settings)
    {
        var now = DateTime.UtcNow;
        if (_checked.TryGetValue(pid, out var known))
        {
            bool recheck = known.Info.Verdict == Verdict.NoDirectX && now - known.CheckedAt > TimeSpan.FromSeconds(2)
                        || known.Info.Verdict == Verdict.Excluded || known.Info.Verdict == Verdict.Allowed && IsExcluded(known.Info, settings);
            if (!recheck) return known.Info;
        }

        var info = GameCheck.Check(pid, settings.InGameExcludedGames);
        if (!_checked.ContainsKey(pid) || _checked[pid].Info.Verdict != info.Verdict)
            LogService.Info(nameof(InGameOverlayService), $"{info.Name} ({pid}): {info.Verdict}{(info.Detail.Length > 0 ? ", " + info.Detail : "")}.");
        _checked[pid] = (info, now);
        _firstSeen.TryAdd(pid, now);

        // Forget games that have closed, so their process ids can be used again.
        foreach (var gone in _checked.Keys.Where(p => !IsRunning(p)).ToList())
        {
            _checked.Remove(gone); _loaded.Remove(gone); _loadFailed.Remove(gone); _firstSeen.Remove(gone);
        }
        return info;
    }

    private static bool IsExcluded(GameInfo info, AppSettings settings) =>
        settings.InGameExcludedGames.Any(e => string.Equals(e, ExeName(info), StringComparison.OrdinalIgnoreCase));

    private static string ExeName(GameInfo info) => System.IO.Path.GetFileName(info.Path);

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// What the overlay draws: the window overlay's tiles or the in-game list, with the window
    /// overlay's look or its own. Runs on the UI thread.
    /// </summary>
    private InGameFrame BuildFrame(AppSettings s)
    {
        IEnumerable<TileViewModel> tiles = s.InGameOwnTiles ? OwnTiles(s) : OverlayViewModel.Instance.ActiveTiles;
        var list = tiles.Take(InGameBlockWriter.MaxTiles).Select(t => new InGameTile(
            t.Definition.Id, t.DisplayLabel, t.DisplayValue, t.DisplayUnit, (byte)t.ColourState,
            t.Definition.HasBar, (float)t.BarFraction)).ToList();

        if (s.InGameOwnLook)
            return new InGameFrame(true, s.InGameLayout == "Bar", (float)s.InGameScale, (float)s.InGameAnchorX, (float)s.InGameAnchorY,
                                   (float)s.InGameBackgroundOpacity, (float)s.InGameOpacity, list);

        var (ax, ay) = WindowOverlayAnchor(s);
        return new InGameFrame(true, false, (float)s.OverlayScale, ax, ay, (float)s.OverlayBackgroundOpacity, (float)s.OverlayOpacity, list);
    }

    /// The window overlay's position as a place on the game's screen: its corner, or where it was
    /// dragged to, as the same fraction of free space.
    internal static (float X, float Y) WindowOverlayAnchor(AppSettings s) => s.OverlayPosition switch
    {
        "TopLeft" => (0f, 0f),
        "BottomLeft" => (0f, 1f),
        "BottomRight" => (1f, 1f),
        "Custom" when s.OverlayAnchorFx >= 0 && s.OverlayAnchorFy >= 0 => ((float)s.OverlayAnchorFx, (float)s.OverlayAnchorFy),
        _ => (1f, 0f),  // TopRight, Pulse's default
    };

    /// The in-game list's tiles, kept up to date from the latest reading. The frame rate and 1% low
    /// are left as they are: the overlay measures those itself, from the game's own frames.
    /// <summary>
    /// The chosen readings for games, in the order the panel shows them: the player's own once
    /// arranged, the window overlay's until then.
    /// </summary>
    internal static List<string> InGameOrder(AppSettings s)
    {
        var order = s.InGameTileOrder.Count > 0 ? s.InGameTileOrder : s.TileOrder;
        return s.InGameTileIds.Select((id, i) => (id, i))
                              .OrderBy(x => { int at = order.IndexOf(x.id); return at >= 0 ? at : order.Count + x.i; })
                              .Select(x => x.id).ToList();
    }

    private IEnumerable<TileViewModel> OwnTiles(AppSettings s)
    {
        var ids = InGameOrder(s);
        var key = string.Join(",", ids);
        if (key != _ownTileKey)
        {
            _ownTileKey = key;
            _ownTiles.Clear();
            foreach (var id in ids)
            {
                var def = SensorTileDefinition.All.FirstOrDefault(d => d.Id == id);
                if (def != null && SensorDriver.Instance.StatusOf(id) == TileStatus.Ready) _ownTiles.Add(new TileViewModel(def));
            }
        }
        var data = HardwareService.Instance.Current;
        if (data != null)
            foreach (var tile in _ownTiles)
            {
                if (tile.Definition.Id is "fps" or "fps_1low") continue;
                tile.Value = data.GetById(tile.Definition.Id);
                tile.IsAsleep = data.GpuAsleep && tile.Definition.Category == SensorCategory.GPU && tile.Definition.Id != "gpu_vram";
            }
        return _ownTiles;
    }

    private void Suppress(App app, bool suppress)
    {
        if (suppress == _suppressing) return;
        _suppressing = suppress;
        app.Dispatcher.BeginInvoke(() => app.SetOverlaySuppressed(suppress));
    }

    private void SetStatus(string text, string? game)
    {
        if (text == StatusText && game == CurrentGame) return;
        StatusText = text;
        CurrentGame = game;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static readonly InGameFrame Empty = new(false, false, 1, 0, 0, 1, 1, Array.Empty<InGameTile>());

    private static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        // An update already under way finishes first, so it cannot write after this.
        for (int i = 0; i < 50 && Volatile.Read(ref _ticking) == 1; i++) Thread.Sleep(20);
        _writer.Dispose();
    }

    /// Whether a game's window can be seen: there, shown, not minimised, and not hidden by Windows
    /// (another virtual desktop, for one). A game that stops drawing while behind something else
    /// also stops reporting, and its status going stale brings the window overlay back.
    private static bool OnScreen(IntPtr window)
    {
        if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)) return false;
        return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
