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

    /// <summary>
    /// The program in front draws like a game but nothing says it is one, so the panel offers
    /// "show Pulse in this game" instead of "turn off".
    /// </summary>
    public bool CurrentGameUnrecognised { get; private set; }

    public event EventHandler? StatusChanged;

    private readonly InGameBlockWriter _writer = new();
    private readonly Dictionary<int, (GameInfo Info, DateTime CheckedAt)> _checked = new();
    private readonly HashSet<int> _loaded = new();
    private readonly HashSet<int> _loadFailed = new();
    private readonly Dictionary<int, DateTime> _firstSeen = new();
    private readonly Dictionary<int, DateTime> _readySince = new();
    private DateTime _nextWatch;

    /// How long a game draws before the overlay goes in: some anti-cheat starts a few seconds after
    /// the game's graphics do, and has to be seen before Pulse is inside, not after.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// How often the games the overlay is in are looked at again for anti-cheat that started late.
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(5);
    private readonly List<TileViewModel> _ownTiles = new();
    private string _ownTileKey = "";
    private System.Threading.Timer? _timer;
    private bool _suppressing;
    private GameInfo? _lastGame;
    private GameInfo? _shownGame;
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

        WatchLoadedGames();

        // The tiles and the player's show/hide choice live on the UI thread; a quick look.
        (InGameFrame Frame, bool Visible)? snapshot = null;
        // Hidden content (Suppress) leaves the window shown, so this stays the player's own choice.
        app.Dispatcher.Invoke(() => snapshot = (BuildFrame(settings), app.IsOverlayVisible));
        var (frame, visible) = snapshot!.Value;

        int pid = ForegroundProcessId();
        GameInfo? found = pid == 0 || pid == Environment.ProcessId ? null : CheckGame(pid, settings);

        // A game the overlay can go into, in front: added to it once, and it becomes the game.
        if (found is { Verdict: Verdict.Allowed } game)
        {
            if (!_loaded.Contains(pid) && !_loadFailed.Contains(pid))
            {
                // Not the moment it starts drawing: after it has drawn for a while, so anti-cheat
                // that starts a few seconds behind the graphics is there to be seen.
                if (!_readySince.TryGetValue(pid, out var since)) _readySince[pid] = since = DateTime.UtcNow;
                if (DateTime.UtcNow - since >= SettleTime)
                {
                    // Asked again from scratch just before going in: nothing remembered is trusted for this.
                    var latest = GameCheck.Check(pid, settings.InGameExcludedGames, settings.InGameIncludedGames, GetForegroundWindow());
                    if (latest.Verdict != Verdict.Allowed)
                    {
                        LogService.Info(nameof(InGameOverlayService), $"Process {pid}: {latest.Verdict} on the last look before adding the overlay.");
                        _checked[pid] = (latest, DateTime.UtcNow);
                        found = latest;
                    }
                    else
                    {
                        var problem = Load(pid);
                        if (problem == null) _loaded.Add(pid);
                        else
                        {
                            _loadFailed.Add(pid);
                            LogService.Warn(nameof(InGameOverlayService), $"Could not put the in-game overlay into process {pid}: {problem}.");
                        }
                    }
                }
            }
            if (_loaded.Contains(pid))
            {
                _shownGame = game;
                _shownWindow = (pid, GetForegroundWindow());
            }
        }

        // The game keeps the overlay until it is closed, turned off in Pulse, or minimised (owner,
        // 4 Nov). Whatever else is in front, a chat app, Steam, the other screen, does not matter.
        if (_shownGame is { } shown && !IsRunning(shown.Pid)) { _shownGame = null; _shownWindow = null; }
        if (_shownGame is { } current && _shownWindow is { } window && !IsExcluded(current, settings) && OnScreen(window.Handle))
        {
            DrawIn(app, frame, visible, current);
            return;
        }

        // No game draws, and the window overlay shows.
        _writer.Write(frame with { Enabled = false });
        Suppress(app, false);

        if (found is { } info && IsAGame(info))
        {
            _lastGame = info;
            if (info.Verdict == Verdict.Allowed)
            {
                SetStatus(_loadFailed.Contains(pid) ? $"Not in {info.Name}: Pulse could not add it to this game"
                                                    : $"Getting ready in {info.Name}", null);
                return;
            }
            // A game loads DirectX a while after it starts: not yet is not "does not". The per-game
            // button is offered only where it changes something: a game the overlay already stays
            // out of has nothing to turn off.
            bool starting = info.Verdict == Verdict.NoDirectX && _firstSeen.TryGetValue(pid, out var seen)
                            && DateTime.UtcNow - seen < TimeSpan.FromMinutes(1);
            SetStatus(starting ? $"Waiting for {info.Name} to start" : NotInText(info),
                      info.Verdict is Verdict.Excluded or Verdict.Unrecognised ? ExeName(info) : null,
                      info.Verdict == Verdict.Unrecognised);
            return;
        }

        // Nothing that counts as a game in front. The game still running is spoken of, so the panel
        // opened beside it can say what happened there and offer to turn the overlay off for it.
        if (_shownGame is { } waiting)
            SetStatus(IsExcluded(waiting, settings) ? $"Turned off for {waiting.Name}"
                      : $"Ready for {waiting.Name}: it shows again when the game is back on screen", ExeName(waiting));
        else if (_lastGame is { } last && IsRunning(last.Pid))
            SetStatus(NotInText(last), last.Verdict == Verdict.Excluded ? ExeName(last) : null);
        else
        {
            _lastGame = null;
            SetStatus("Waiting for a game", null);
        }
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

    /// <summary>
    /// Whether a program is a game, for what the status line speaks of. Programs that do not draw
    /// with DirectX 11 or 12, and are not Vulkan games either, are just programs: an archiver in
    /// front says "waiting for a game", not "not in WinRAR".
    /// </summary>
    private static bool IsAGame(GameInfo info) => info.Verdict switch
    {
        Verdict.NotAGame or Verdict.NotOpenable => false,
        Verdict.NoDirectX => info.Detail.Contains("Vulkan", StringComparison.Ordinal),
        _ => true,
    };

    /// Why the overlay is not in a game, for the status line.
    private static string NotInText(GameInfo info) => info.Verdict switch
    {
        Verdict.Excluded => $"Turned off for {info.Name}",
        Verdict.AntiCheat => $"Not in {info.Name}: {info.Detail}, so the window overlay is shown",
        Verdict.Unrecognised => $"Not in {info.Name}: it does not look like a game. If it is one, Pulse can show in it",
        // Left during its few seconds' wait: it goes in once the game is back in front.
        Verdict.Allowed => $"Getting ready in {info.Name}: it shows a few seconds after you go back to the game",
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

    /// <summary>
    /// Looks again at the games the overlay is in, every few seconds, for anti-cheat that started
    /// after it went in. One found means that game is treated as having anti-cheat from then on:
    /// Pulse stops naming it, so the overlay inside draws nothing, and the window overlay shows.
    /// Taking the overlay out of a running game is not safe, so it stays until the game closes.
    /// </summary>
    private void WatchLoadedGames()
    {
        var now = DateTime.UtcNow;
        if (now < _nextWatch || _loaded.Count == 0) return;
        _nextWatch = now + WatchEvery;

        foreach (var pid in _loaded.ToList())
        {
            if (_checked.TryGetValue(pid, out var known) && known.Info.Verdict == Verdict.AntiCheat) continue;
            string? found;
            string name;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
                found = GameCheck.FindAntiCheat(name, "", GameCheck.LoadedModuleNames(process));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { continue; }  // closed: forgotten elsewhere
            if (found == null) continue;

            var info = _checked.TryGetValue(pid, out var k) ? k.Info : new GameInfo(pid, name, "", Verdict.Allowed, "");
            _checked[pid] = (info with { Verdict = Verdict.AntiCheat, Detail = $"{found}, which started after Pulse's overlay went in" }, now);
            if (_shownGame?.Pid == pid) { _shownGame = null; _shownWindow = null; }
            LogService.Warn(nameof(InGameOverlayService),
                $"Process {pid}: anti-cheat started after the in-game overlay went in. It draws nothing there from now on; the window overlay is shown.");
        }
    }

    /// The verdict for a game, remembered: a game's folder and anti-cheat do not change while it runs.
    /// "No DirectX yet" is asked again for a while, as a game loads its graphics after starting.
    private GameInfo CheckGame(int pid, AppSettings settings)
    {
        var now = DateTime.UtcNow;
        if (_checked.TryGetValue(pid, out var known))
        {
            // Asked again: a game still loading its graphics, one that may yet fill the screen or be
            // named a game by the player, and one the player has just turned off or back on.
            bool recheck = known.Info.Verdict is Verdict.NoDirectX or Verdict.Unrecognised && now - known.CheckedAt > TimeSpan.FromSeconds(2)
                        || known.Info.Verdict == Verdict.Unrecognised && IsIncluded(known.Info, settings)
                        || known.Info.Verdict == Verdict.Excluded || known.Info.Verdict == Verdict.Allowed && IsExcluded(known.Info, settings);
            if (!recheck) return known.Info;
        }

        var info = GameCheck.Check(pid, settings.InGameExcludedGames, settings.InGameIncludedGames, GetForegroundWindow());
        if (!_checked.ContainsKey(pid) || _checked[pid].Info.Verdict != info.Verdict)
            // By process number and verdict only: Pulse's privacy policy promises no application,
            // game, window or file name is ever in its log, and the reasons name files (a library
            // loaded, a file in the game's folder). Name and reason are for the status line on
            // screen only.
            LogService.Info(nameof(InGameOverlayService), $"Process {pid}: {info.Verdict}.");
        _checked[pid] = (info, now);
        _firstSeen.TryAdd(pid, now);

        // Forget games that have closed, so their process ids can be used again.
        foreach (var gone in _checked.Keys.Where(p => !IsRunning(p)).ToList())
        {
            _checked.Remove(gone); _loaded.Remove(gone); _loadFailed.Remove(gone); _firstSeen.Remove(gone); _readySince.Remove(gone);
        }
        return info;
    }

    private static bool IsIncluded(GameInfo info, AppSettings settings) =>
        settings.InGameIncludedGames.Any(e => string.Equals(e, ExeName(info), StringComparison.OrdinalIgnoreCase));

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

    private void SetStatus(string text, string? game, bool unrecognised = false)
    {
        if (text == StatusText && game == CurrentGame && unrecognised == CurrentGameUnrecognised) return;
        StatusText = text;
        CurrentGame = game;
        CurrentGameUnrecognised = unrecognised;
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
