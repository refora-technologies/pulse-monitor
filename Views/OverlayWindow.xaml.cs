using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Pulse.Services;
using Pulse.ViewModels;

using WpfCursors = System.Windows.Input.Cursors;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace Pulse.Views;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // --- Always-on-top enforcement -------------------------------------------------
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // Windows shell surfaces we should never cover: taskbar, Quick Settings / Action
    // Center, tray icon overflow flyout, Start menu, search. All modern shell UI is
    // hosted in one of these processes across Win10/11 builds.
    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "ShellHost",
        "TextInputHost",
    };

    // The process alone is too coarse to act on. "explorer" also owns every File Explorer
    // window and the desktop itself, and "Windows.UI.Core.CoreWindow" is the class of any
    // UWP app, so matching on either one by itself would have us treating a folder window
    // or Settings as shell furniture. A surface has to match on both counts to qualify.
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",                       // taskbar
        "Shell_SecondaryTrayWnd",              // taskbar on an additional display
        "TopLevelWindowForOverflowXamlIsland", // hidden icons / tray overflow flyout
        "ControlCenterWindow",                 // Quick Settings
        "Windows.UI.Core.CoreWindow",          // Start, search, IME candidate window
        "XamlExplorerHostIslandWindow_WASDK",  // newer shell islands
    };

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // Held as a field so the delegate isn't garbage-collected while the hook is live.
    private WinEventDelegate? _winEventProc;
    private IntPtr            _winEventHook = IntPtr.Zero;
    private DispatcherTimer?  _topmostTimer;
    private DispatcherTimer?  _displayChangeTimer;

    // --- Menu outside-click dismissal (cross-process) -------------------------------
    // WPF's own Popup light-dismiss only sees mouse-down events that pass through this
    // app's input pipeline, so it correctly closes the menu for clicks elsewhere in
    // Pulse (the overlay itself, the Control Panel), but never finds out about a click
    // on the taskbar or another app's window — that message never reaches this process.
    // A low-level mouse hook is the only way to see those.
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const int WH_MOUSE_LL    = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private LowLevelMouseProc? _menuMouseHookProc;
    private IntPtr             _menuMouseHook = IntPtr.Zero;
    private WpfContextMenu?    _activeMenu;

    private readonly OverlayViewModel _vm;

    public OverlayWindow()
    {
        InitializeComponent();

        // Not in the markup: a decode that fails there is a XamlParseException, and this is the
        // first window built at startup. See App.ApplyIcon.
        App.ApplyIcon(this);

        _vm = OverlayViewModel.Instance;
        DataContext = _vm;

        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnOverlaySizeChanged;
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>
    /// Stops the overlay auto-sizing while it is hidden.
    ///
    /// This window is layered (AllowsTransparency="True"), so WPF composes it into an
    /// off-screen bitmap pushed through a device context. A hidden window still runs
    /// layout, so every sensor update still changed the text width and drove a
    /// SizeToContent resize — and while hidden that DC + bitmap pair is never reclaimed,
    /// leaking roughly one GDI object per poll. Left running for a few hours that
    /// exhausts the process GDI quota, after which Windows cannot create any more GDI
    /// objects for Pulse: the tray menu stops opening and the app appears frozen while
    /// still sitting at 0% CPU.
    ///
    /// Freezing the size while hidden removes the resize entirely. Sizing is restored on
    /// show, so tiles stay live and correct the moment the overlay comes back.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SizeToContent = IsVisible ? SizeToContent.WidthAndHeight : SizeToContent.Manual;

        // No point re-asserting a z-order the user cannot see.
        if (IsVisible) _topmostTimer?.Start();
        else           _topmostTimer?.Stop();

        // The resolution may have changed while we were hidden, which would leave the
        // saved position outside the new desktop.
        if (IsVisible) Dispatcher.InvokeAsync(ApplyPosition, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Repositions the overlay after a display change.
    ///
    /// A fullscreen game switching to a lower resolution changes the actual Windows
    /// display mode, so a position computed for the previous resolution can end up
    /// outside the desktop entirely — the overlay does not move or rescale, it simply
    /// stops being visible. Pulse previously only positioned itself at startup and when
    /// settings changed, so it never recovered.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised on a system thread, and Windows fires it several times while a mode
        // change settles — with the work area often still reporting stale values on the
        // first notification. Debounce and re-apply once things are stable.
        Dispatcher.InvokeAsync(() =>
        {
            _lastWorkAreaSignature = null;
            _settleTicks           = 0;
            _displayChangeTimer  ??= BuildDisplayChangeTimer();
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        });
    }

    private string? _lastWorkAreaSignature;
    private int     _settleTicks;

    /// <summary>
    /// Waits for the display layout to stop moving before repositioning.
    ///
    /// A single delayed pass was not enough: Windows reports work areas while a mode change
    /// is still settling and then sends no further notification once it finishes, so a pass
    /// that fired too early positioned the overlay against a screen size that no longer
    /// existed and nothing ever corrected it. That is the "occasionally somewhere completely
    /// different" case. Now we poll until two consecutive readings agree.
    /// </summary>
    private DispatcherTimer BuildDisplayChangeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            var signature = WorkAreaSignature();
            bool stable   = signature == _lastWorkAreaSignature;
            _lastWorkAreaSignature = signature;

            // Ceiling of roughly seven seconds, so a display that never settles (a monitor
            // being repeatedly re-detected, say) still gets the overlay put back.
            if (!stable && ++_settleTicks < 20) return;

            timer.Stop();
            ApplyPosition();
        };
        return timer;
    }

    /// Cheap fingerprint of every monitor's work area, used to detect that the layout has
    /// stopped changing.
    private static string WorkAreaSignature()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            builder.Append(screen.DeviceName).Append('=').Append(screen.WorkingArea).Append(';');
        return builder.ToString();
    }

    /// <summary>
    /// A DPI change resizes the window in physical pixels, so the anchor has to be applied
    /// against the new size. Nothing handled this before, which is part of why the overlay
    /// came back slightly out of place after a resolution change altered the scaling.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(ApplyPosition, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-anchors whenever the overlay's own size changes.
    ///
    /// Toggling tiles, compact mode, the status bar and the scale slider all resize the
    /// window. Positioning previously ran from the settings-changed event, which fires
    /// *before* the tile list is rebuilt, so it placed the overlay using the size it had a
    /// moment earlier — a bottom- or right-anchored overlay then grew straight off the edge
    /// of the screen or under the taskbar. Reacting to the resize itself covers every cause
    /// at once, whatever triggered it.
    /// </summary>
    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible) return;   // frozen while hidden; OnIsVisibleChanged repositions on show

        // ApplyPosition makes the same check, but skipping the queue entirely while the user
        // is positioning saves piling up callbacks that will only be discarded.
        if (IsUserPositioning) return;

        Dispatcher.InvokeAsync(ApplyPosition, DispatcherPriority.Loaded);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactMode();
        ApplyDragState();
        // Defer position until after first render so the window has a real size to place.
        Dispatcher.InvokeAsync(() =>
        {
            MigrateLegacyPosition();   // no-op unless upgrading from a pre-1.1 position
            ApplyPosition();           // clamps to the current work area on the way
            LogStartupPlacement();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd  = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);

        StartTopmostEnforcement();
        ForceTopmost();
    }

    /// <summary>
    /// Keep the overlay above every other window (games, launchers, other topmost
    /// widgets) — but not above Windows' own shell UI. Setting Topmost once is not
    /// enough: when another app creates its own topmost window it can push us down, so
    /// we re-assert HWND_TOPMOST whenever the foreground window changes, plus a
    /// low-frequency timer as a safety net. We skip that re-assertion while a shell
    /// surface (taskbar, Quick Settings, tray overflow, Start menu, search) is
    /// foreground, so those still render above us instead of getting buried.
    ///
    /// This covers windowed, borderless-windowed and Windows' fullscreen-optimized
    /// games (the vast majority). True DirectX *exclusive* fullscreen bypasses the
    /// desktop compositor entirely and cannot be covered by any normal window — that
    /// would require DirectX hooking/injection, which is out of scope here.
    /// </summary>
    private void StartTopmostEnforcement()
    {
        _winEventProc = OnForegroundChanged;
        _winEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // One second, measured at 0.047ms per tick — around 170ms of CPU per hour, which is
        // far below the sensor polling this app does anyway. The interval was reviewed for
        // being wasteful and the numbers did not support changing it: a slower safety net
        // would mean the overlay staying buried for longer whenever the event hook misses a
        // z-order change, which is the only reason this timer exists.
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _topmostTimer.Tick += (_, _) => { ForceTopmost(); WatchCovered(); };

        // Only runs while the overlay is actually on screen. ForceTopmost returns
        // immediately when hidden, so ticking then was pure wakeups for nothing.
        if (IsVisible) _topmostTimer.Start();
    }

    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // OUTOFCONTEXT callbacks are delivered on this (the installing) thread, so it's
        // safe to touch the window directly.
        ForceTopmost();
    }

    private void ForceTopmost()
    {
        if (!IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // A shell surface (taskbar, Quick Settings, tray overflow, Start menu, search) is
        // open and focused. Don't fight it for the top slot.
        if (IsShellSurfaceForeground(out var surface, out bool surfaceIsTopmost))
        {
            // Standing still is enough for a surface that is itself topmost: it shares our
            // band and was raised more recently, so it already sits above us.
            //
            // The tray overflow flyout — the "hidden icons" popup — is the one shell surface
            // Windows does not mark topmost, and every topmost window sits above every
            // non-topmost one regardless of the order inside each band. So we have to leave
            // the topmost band for it. Dropping to HWND_NOTOPMOST is not enough on its own:
            // that is documented as placing the window *above all non-topmost windows*, which
            // put us right back on top of the very flyout we were trying to reveal. Anchoring
            // directly beneath the surface itself both clears topmost and lands us below it.
            if (!surfaceIsTopmost) SlipBehind(hwnd, surface);
            return;
        }

        _anchoredBehind = IntPtr.Zero;

        // Otherwise reassert topmost. During ordinary desktop use we insert ourselves
        // just behind the taskbar's own window instead of the very top of the z-order,
        // so its icons/clock are never covered even when it isn't the focused window.
        //
        // A foreground window that covers the entire monitor (a fullscreen-ish game) is
        // a different situation: Windows can demote the taskbar's own z-order in that
        // case even without true exclusive fullscreen, and since we'd be anchored to it,
        // we'd get dragged down too. The taskbar isn't meaningfully visible under a
        // full-monitor window anyway, so there's nothing to protect there — claim the
        // absolute top slot instead, which is what actually wins against a game.
        //
        // And only while that taskbar is itself topmost. Windows takes a taskbar out of the
        // topmost band while a full-screen window is on its display, foreground or not, and a
        // window placed beneath one that is not topmost stops being topmost itself (SetWindowPos,
        // documented). With two displays that happens without the foreground ever being full
        // screen: a video full screen on the overlay's display while you work on the other one
        // anchored the overlay under a demoted taskbar, and it fell behind everything until the
        // next foreground change happened to be full screen (18 Nov 2026). A taskbar that is not on top has nothing to protect anyway.
        //
        // And only while that taskbar is actually over the overlay. A taskbar can keep its topmost
        // flag and still sit below ordinary windows in the window order: measured on the test
        // laptop on 27 Nov 2026, Shell_TrayWnd topmost yet below Chrome and WhatsApp, and the
        // overlay placed under it below them too; that is how Edge stayed over the overlay on
        // 22 Nov, clicks and all. The overlay keeps out of the taskbar's place, so the two only
        // meet when the taskbar slides up over it (auto-hide). Otherwise the top it is.
        var insertAfter = HWND_TOPMOST;
        _anchoredUnderTaskbar = false;
        if (!IsForegroundWindowFullScreen())
        {
            var taskbar = FindTaskbarOnOurMonitor(hwnd);
            if (taskbar != IntPtr.Zero && (GetWindowLong(taskbar, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0
                && Overlaps(hwnd, taskbar))
            {
                insertAfter = taskbar;
                _anchoredUnderTaskbar = true;
            }
        }

        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Whatever the taskbar did, an ordinary window must never be above the overlay. If one
        // is, the place just taken was wrong: take the very top instead.
        if (insertAfter != HWND_TOPMOST && OrdinaryWindowAbove(hwnd))
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _anchoredUnderTaskbar = false;
            if (!_sunkenTaskbarSaid)
            {
                _sunkenTaskbarSaid = true;
                LogService.Info(nameof(OverlayWindow),
                    "The taskbar was below ordinary windows, so the overlay took the top place rather than its usual one just under the taskbar.");
            }
        }
    }

    private bool _sunkenTaskbarSaid;

    /// Whether two windows' visible rectangles meet.
    private static bool Overlaps(IntPtr a, IntPtr b)
    {
        if (!VisibleBounds(a, out var x) || !VisibleBounds(b, out var y)) return false;
        return x.Left < y.Right && y.Left < x.Right && x.Top < y.Bottom && y.Top < x.Bottom;
    }

    /// A window's rectangle as drawn: without the invisible resize borders Windows adds around
    /// most windows, which reach 8 px onto a neighbouring screen when a window is maximised.
    private static bool VisibleBounds(IntPtr w, out RECT r)
    {
        if (DwmGetWindowAttribute(w, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) == 0
            && r.Right > r.Left && r.Bottom > r.Top)
            return true;
        return GetWindowRect(w, out r);
    }

    /// Whether any visible, ordinary (not topmost) window of another program is above this one
    /// in the window order, where no ordinary window can be while this one is truly topmost.
    private static bool OrdinaryWindowAbove(IntPtr hwnd)
    {
        uint self = (uint)Environment.ProcessId;
        int guard = 0;
        for (var w = GetWindow(hwnd, GW_HWNDPREV); w != IntPtr.Zero && guard++ < 1024; w = GetWindow(w, GW_HWNDPREV))
        {
            if (!IsWindowVisible(w)) continue;
            if ((GetWindowLong(w, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) continue;
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            GetWindowThreadProcessId(w, out uint pid);
            if (pid == self) continue;
            return true;
        }
        return false;
    }

    /// The window we are currently parked beneath, so the call isn't repeated on every
    /// foreground change and timer tick for as long as one surface stays open.
    private IntPtr _anchoredBehind;

    /// Whether the last re-claim put the overlay just under the taskbar rather than at the top.
    private bool _anchoredUnderTaskbar;

    // ── When the overlay is covered anyway ────────────────────────────────────────────

    /// When the overlay was first seen covered in this episode, on the tick clock; 0 when not.
    private long _coveredSince;
    private bool _coveredSaid;
    private int  _coveredReports;

    /// How long the overlay must stay covered, after re-claiming its place every second, before
    /// that is worth a line: a window opening over it for a moment is not a fault.
    private const long CoveredAfterMs = 3000;

    /// At most this many lines in one run, so a machine where something always wins cannot
    /// fill the log.
    private const int MaxCoveredReports = 20;

    /// <summary>
    /// Says, once per episode, when the overlay stays covered although it has just re-claimed
    /// its place: what is over it and in what state both are. By process number only, never a
    /// name or a title (the privacy page's promise).
    /// </summary>
    /// <remarks>
    /// Written for a report of 18 Nov 2026 that the overlay sometimes stays behind,
    /// which nothing in the log could explain. Checked right after <see cref="ForceTopmost"/>, so
    /// it sees only what that could not fix. Windows that let clicks through (other overlays,
    /// NVIDIA's included) and Windows' own taskbar, which the overlay sits under on purpose, are
    /// not counted.
    /// </remarks>
    private void WatchCovered()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        bool topmost = false, fills = false;
        var cover = IsVisible && hwnd != IntPtr.Zero ? CoveringWindow(hwnd, out topmost, out fills) : IntPtr.Zero;
        if (cover == IntPtr.Zero)
        {
            _coveredSince = 0;
            _coveredSaid  = false;
            return;
        }

        long now = Environment.TickCount64;
        if (_coveredSince == 0) { _coveredSince = now; return; }
        if (_coveredSaid || now - _coveredSince < CoveredAfterMs || _coveredReports >= MaxCoveredReports) return;

        _coveredSaid = true;
        _coveredReports++;
        GetWindowThreadProcessId(cover, out uint pid);
        bool ourTopmost = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        static string YesNo(bool b) => b ? "yes" : "no";
        // Its size, and whether it is part of Windows itself (a notification, a flyout, Start),
        // which the overlay lets in front on purpose: enough to tell those from another program's
        // always-on-top window without a name (26 Nov 2026, after a week of lines that said only
        // a process number, gone by the time anyone read them).
        GetWindowRect(cover, out var size);
        LogService.Warn(nameof(OverlayWindow),
            $"The overlay has been covered for {(now - _coveredSince) / 1000} s by a window of process {pid} "
            + $"(topmost: {YesNo(topmost)}, fills its screen: {YesNo(fills)}, {size.Right - size.Left} x {size.Bottom - size.Top} px, "
            + $"part of Windows: {YesNo(IsWindowsShell(pid))}). The overlay: topmost {YesNo(ourTopmost)}, "
            + $"under the taskbar {YesNo(_anchoredUnderTaskbar)}, foreground full screen {YesNo(IsForegroundWindowFullScreen())}.");
    }

    /// <summary>
    /// Whether a process is one of Windows' own shell programs (the taskbar and Explorer, Start,
    /// search, notifications and flyouts). Only the answer is logged, never the name.
    /// </summary>
    private static bool IsWindowsShell(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant() is "explorer" or "startmenuexperiencehost" or "searchhost"
                or "shellexperiencehost" or "shellhost" or "textinputhost" or "lockapp";
        }
        catch { return false; }
    }

    /// The nearest visible window above the overlay that overlaps it, or zero.
    private static IntPtr CoveringWindow(IntPtr hwnd, out bool topmost, out bool fills)
    {
        topmost = fills = false;
        if (!VisibleBounds(hwnd, out var ours)) return IntPtr.Zero;
        uint self = (uint)Environment.ProcessId;

        int guard = 0;
        for (var w = GetWindow(hwnd, GW_HWNDPREV); w != IntPtr.Zero && guard++ < 512; w = GetWindow(w, GW_HWNDPREV))
        {
            if (!IsWindowVisible(w)) continue;
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;

            int ex = GetWindowLong(w, GWL_EXSTYLE);
            if ((ex & WS_EX_TRANSPARENT) != 0) continue;   // lets clicks through: another overlay

            GetWindowThreadProcessId(w, out uint pid);
            if (pid == self) continue;

            if (!VisibleBounds(w, out var r)) continue;
            if (r.Right <= ours.Left || r.Left >= ours.Right || r.Bottom <= ours.Top || r.Top >= ours.Bottom) continue;

            var name = new System.Text.StringBuilder(64);
            if (GetClassName(w, name, name.Capacity) > 0 && name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                continue;   // the taskbar, which the overlay sits under on purpose

            topmost = (ex & WS_EX_TOPMOST) != 0;
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(w, MONITOR_DEFAULTTONEAREST), ref info))
            {
                var m = info.rcMonitor;
                fills = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            }
            return w;
        }
        return IntPtr.Zero;
    }

    private const uint GW_HWNDPREV  = 3;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    private void SlipBehind(IntPtr hwnd, IntPtr surface)
    {
        if (_anchoredBehind == surface) return;
        _anchoredBehind = surface;

        SetWindowPos(hwnd, surface, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// The taskbar belonging to the display the overlay is actually on.
    ///
    /// Only the primary monitor's taskbar is Shell_TrayWnd; every additional display gets a
    /// Shell_SecondaryTrayWnd of its own. Anchoring to the primary one regardless is what
    /// let an overlay on a second screen sit over that screen's taskbar, because being just
    /// beneath one topmost window says nothing about where you land relative to another.
    /// </summary>
    private static IntPtr FindTaskbarOnOurMonitor(IntPtr hwnd)
    {
        var ourMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var match = IntPtr.Zero;

        try
        {
            // Held in a local so the delegate cannot be collected while EnumWindows runs.
            EnumWindowsProc scan = (candidate, _) =>
            {
                var name = new System.Text.StringBuilder(64);
                if (GetClassName(candidate, name, name.Capacity) == 0) return true;

                var cls = name.ToString();
                if (cls is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return true;
                if (MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST) != ourMonitor) return true;

                match = candidate;
                return false;   // stop, we have the one on our display
            };

            EnumWindows(scan, IntPtr.Zero);
        }
        catch
        {
            // fall through to the primary taskbar below
        }

        return match != IntPtr.Zero ? match : FindWindow("Shell_TrayWnd", null);
    }

    private static bool IsForegroundWindowFullScreen()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (!GetWindowRect(fg, out var windowRect)) return false;

            var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return false;

            return windowRect.Left   <= mi.rcMonitor.Left
                && windowRect.Top    <= mi.rcMonitor.Top
                && windowRect.Right  >= mi.rcMonitor.Right
                && windowRect.Bottom >= mi.rcMonitor.Bottom;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the foreground window is one of Windows' own shell surfaces.
    /// </summary>
    /// <param name="surface">That window, so we can anchor ourselves directly beneath it.</param>
    /// <param name="surfaceIsTopmost">
    /// Whether the surface is itself topmost, which decides whether we need to stand aside
    /// or move below it outright.
    /// </param>
    private static bool IsShellSurfaceForeground(out IntPtr surface, out bool surfaceIsTopmost)
    {
        surface = IntPtr.Zero;
        surfaceIsTopmost = false;
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return false;
            using var process = Process.GetProcessById((int)pid);
            if (!ShellProcessNames.Contains(process.ProcessName)) return false;

            var name = new System.Text.StringBuilder(256);
            if (GetClassName(fg, name, name.Capacity) == 0) return false;
            if (!ShellWindowClasses.Contains(name.ToString())) return false;

            surface = fg;
            surfaceIsTopmost = (GetWindowLong(fg, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
    }

    private void ApplyDragState()
    {
        bool drag = _vm.IsDragEnabled;
        SetClickThrough(!drag);
        DragBorder.Cursor = drag ? WpfCursors.SizeAll : WpfCursors.Arrow;

        if (drag)
        {
            DragBorder.MouseLeftButtonDown -= OnDragStart;
            DragBorder.MouseLeftButtonDown += OnDragStart;
            DragBorder.MouseRightButtonUp  -= OnRightClick;
            DragBorder.MouseRightButtonUp  += OnRightClick;
        }
        else
        {
            DragBorder.MouseLeftButtonDown -= OnDragStart;
            DragBorder.MouseRightButtonUp  -= OnRightClick;
        }
    }

    /// <summary>
    /// True while the user is physically dragging the overlay.
    ///
    /// DragMove blocks here running its own nested message loop, which keeps pumping this
    /// dispatcher. Anything already queued — an ApplyPosition from a size change a moment
    /// earlier — therefore runs *during* the drag, so checking a flag in SizeChanged alone
    /// would not stop it. ApplyPosition checks this itself.
    /// </summary>
    private bool _userDragging;

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;

        _userDragging = true;
        LocationChanged += OnDragLocationChanged;
        try
        {
            DragMove();   // blocks until the button is released
        }
        finally
        {
            LocationChanged -= OnDragLocationChanged;
            _userDragging = false;
        }

        SavePosition();    // the one disk write for the whole drag
        ApplyPosition();   // settle on the new anchor now that re-anchoring is allowed again
    }

    /// <summary>
    /// Keeps the stored anchor level with the window while it is being dragged.
    ///
    /// Nothing is written to disk — that would be a settings write per pixel of travel. The
    /// anchor in memory is not optional though: ApplyPosition places the overlay from it, and
    /// the overlay auto-sizes on almost every sensor poll. Leaving it stale for the length of
    /// a drag is what made the overlay snap back to where it started, mid-drag, on the first
    /// reading that changed width.
    /// </summary>
    private void OnDragLocationChanged(object? sender, EventArgs e)
    {
        if (_reshaping) return;
        SavePosition(writeToDisk: false);
        ReshapeWhileDragging();
    }

    // ── Joining the edge while dragging (27 Nov 2026) ───────────────────────
    // The in-game overlay shows its flat side and curves the moment it touches an edge, and
    // goes round again the moment it leaves. The window overlay only did so once dropped, and
    // only if dropped exactly on the edge's last pixel. Now it is pulled onto an edge that comes
    // within reach while it is dragged, as Windows pulls windows into place, and takes its
    // shape there and then.

    private const int WM_MOVING = 0x0216;

    /// How close an edge pulls the overlay onto it, in device-independent pixels.
    internal const double SnapReach = 14;

    private bool _reshaping;

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOVING || !_userDragging || lParam == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var r = Marshal.PtrToStructure<RECT>(lParam);
            // The size the window is now: it can change mid-drag, when it takes or loses its
            // flares, and the rectangle Windows offers is the one it started the drag with.
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (GetWindowRect(hwnd, out var now)) { w = now.Right - now.Left; h = now.Bottom - now.Top; }
            var work = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(r.Left, r.Top, w, h)).WorkingArea;
            int reach = (int)Math.Round(SnapReach * VisualTreeHelperDpi());
            var (x, y) = SnapToEdges(r.Left, r.Top, w, h, work, reach);
            Marshal.StructureToPtr(new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        catch (Exception ex)
        {
            Services.LogService.Error(nameof(OverlayWindow), "Could not pull the overlay onto the edge while dragging", ex);
            return IntPtr.Zero;
        }
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// Where a window dragged to x, y lands: on any edge of the work area within reach of it,
    /// inside or out, and otherwise where it was put. An edge further away than that, such as
    /// another monitor's, does not pull, so the overlay can still be dragged across screens.
    internal static (int X, int Y) SnapToEdges(int x, int y, int w, int h, System.Drawing.Rectangle work, int reach)
    {
        if (Math.Abs(x - work.Left) <= reach) x = work.Left;
        else if (Math.Abs(work.Right - (x + w)) <= reach) x = work.Right - w;
        if (Math.Abs(y - work.Top) <= reach) y = work.Top;
        else if (Math.Abs(work.Bottom - (y + h)) <= reach) y = work.Bottom - h;
        return (x, y);
    }

    /// Mid-drag: the shape for where the overlay is now. When the flares come or go the window
    /// changes size, so it is moved to keep the panel where it was and its flat sides on the edge.
    private void ReshapeWhileDragging()
    {
        var settings = SettingsService.Instance.Settings;
        var (ax, ay) = Services.InGame.InGameOverlayService.WindowOverlayAnchor(settings);
        var flush = Flush(ax, ay);
        if (NormalPanel.Flush == flush) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var shown = BarPanel.Visibility == Visibility.Visible ? BarPanel
                  : CompactPanel.Visibility == Visibility.Visible ? CompactPanel : NormalPanel;
        var before = shown.FlareRoom;
        NormalPanel.Flush = CompactPanel.Flush = BarPanel.Flush = flush;
        UpdateLayout();
        var after = shown.FlareRoom;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var b)) return;

        double dip = VisualTreeHelperDpi();
        int w = b.Right - b.Left, h = b.Bottom - b.Top;
        int x = b.Left - (int)Math.Round((after.Left - before.Left) * dip);
        int y = b.Top  - (int)Math.Round((after.Top  - before.Top)  * dip);
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        if ((flush & FlushEdges.Left)   != 0) x = work.Left;
        if ((flush & FlushEdges.Right)  != 0) x = work.Right - w;
        if ((flush & FlushEdges.Top)    != 0) y = work.Top;
        if ((flush & FlushEdges.Bottom) != 0) y = work.Bottom - h;

        _reshaping = true;
        try { SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }
        finally { _reshaping = false; }
    }

    /// Uniform scale (Option B): the whole panel grows/shrinks together via a
    /// LayoutTransform, so the window's SizeToContent measures the scaled size
    /// correctly and grows toward bottom-right — the grip's own corner.
    // Where the cursor was when the resize began, in screen pixels, and the scale at that
    // moment. Both are needed because the scale is worked out from total travel rather than
    // accumulated deltas — see OnResizeDragDelta.
    private System.Drawing.Point _scaleDragOrigin;
    private double _scaleAtDragStart;
    private bool   _scaling;

    private void OnResizeDragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;

        _scaleDragOrigin  = System.Windows.Forms.Cursor.Position;
        _scaleAtDragStart = _vm.OverlayScale;
        _scaling          = true;
    }

    /// <summary>
    /// Resizes the overlay from the grip.
    ///
    /// Driven by how far the cursor has travelled since the drag began, rather than by the
    /// per-event deltas the Thumb reports. Those deltas are measured against the Thumb's own
    /// position, and this Thumb sits in the corner of the very window being resized — so
    /// every size change moved the grip out from under the cursor, which fed back into the
    /// next delta. The result grew and shrank in lurches instead of following the hand.
    /// Total travel from a fixed screen point cannot feed back on itself.
    /// </summary>
    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_vm.IsDragEnabled || !_scaling) return;

        var cursor = System.Windows.Forms.Cursor.Position;
        double travel = ((cursor.X - _scaleDragOrigin.X) + (cursor.Y - _scaleDragOrigin.Y)) / 2.0;

        _vm.OverlayScale = _scaleAtDragStart + travel / 300.0;
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _scaling = false;

        var settings = SettingsService.Instance.Settings;
        settings.OverlayScale = _vm.OverlayScale;
        SettingsService.Instance.Save();
        ViewModels.SettingsViewModel.Instance.FollowOverlayScale();
    }

    /// Quick access to Settings while free-dragging — the overlay is click-through
    /// otherwise, so this only applies in drag mode where clicks already reach it.
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;

        var app       = (App)System.Windows.Application.Current;
        var itemStyle = (Style)FindResource("OverlayMenuItem");

        var menu = new WpfContextMenu { Style = (Style)FindResource("OverlayContextMenu") };

        var controlPanelItem = new WpfMenuItem { Header = "Open Control Panel", Style = itemStyle };
        controlPanelItem.Click += (_, _) => app.ShowControlPanel();
        menu.Items.Add(controlPanelItem);

        var hideOverlayItem = new WpfMenuItem { Header = "Hide Overlay", Style = itemStyle };
        hideOverlayItem.Click += (_, _) => app.HideOverlay();
        menu.Items.Add(hideOverlayItem);

        DragBorder.ContextMenu = menu;

        _activeMenu = menu;
        menu.Closed += (_, _) => { StopMenuMouseHook(); _activeMenu = null; };

        // Opening via IsOpen (rather than the normal right-click-triggered flow) can
        // leave stale mouse capture behind, which stops the popup's own outside-click
        // dismissal from engaging for clicks inside this app. Releasing capture first
        // lets it grab it cleanly.
        Mouse.Capture(null);
        menu.IsOpen = true;

        // The Popup's own dismissal can't see clicks on other processes' windows (the
        // taskbar, other apps) — this hook is what catches those.
        StartMenuMouseHook();
    }

    private void StartMenuMouseHook()
    {
        if (_menuMouseHook != IntPtr.Zero) return;
        _menuMouseHookProc = MenuMouseHookProc;
        _menuMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _menuMouseHookProc, GetModuleHandle(null), 0);
    }

    private void StopMenuMouseHook()
    {
        if (_menuMouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_menuMouseHook);
        _menuMouseHook     = IntPtr.Zero;
        _menuMouseHookProc = null;
    }

    private IntPtr MenuMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN)
            && _activeMenu is { IsOpen: true } menu
            && PresentationSource.FromVisual(menu) is HwndSource source)
        {
            var hook = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (GetWindowRect(source.Handle, out var bounds))
            {
                bool insideMenu = hook.pt.X >= bounds.Left && hook.pt.X <= bounds.Right
                                && hook.pt.Y >= bounds.Top  && hook.pt.Y <= bounds.Bottom;

                // Deferred rather than closed inline — we're inside a global low-level
                // hook callback, and mutating a Popup's visibility synchronously from
                // there risks re-entering Windows' input pipeline mid-dispatch.
                if (!insideMenu)
                    Dispatcher.BeginInvoke(() => menu.IsOpen = false);
            }
        }

        return CallNextHookEx(_menuMouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Shows the panel for the chosen look: the tiles, the compact list, or the compact straight
    /// line. A straight line wider than its screen shows as the list instead, as in games, rather
    /// than running off both sides (see PlaceWindow).
    /// </summary>
    private void ApplyCompactMode()
    {
        bool compact = _vm.IsCompactMode;
        bool bar = compact && _vm.IsCompactBar && !_barTooWide;
        NormalPanel.Visibility  = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = compact && !bar ? Visibility.Visible : Visibility.Collapsed;
        BarPanel.Visibility     = bar ? Visibility.Visible : Visibility.Collapsed;
    }

    /// The straight line did not fit on its screen last time it was placed.
    private bool _barTooWide;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.PropertyName is nameof(OverlayViewModel.IsCompactMode) or nameof(OverlayViewModel.IsCompactBar))
            {
                _barTooWide = false;
                ApplyCompactMode();
            }
            if (e.PropertyName == nameof(OverlayViewModel.IsDragEnabled))
                ApplyDragState();
        });
    }

    /// <summary>
    /// Places the overlay, working entirely in physical pixels.
    ///
    /// Positioning used to go through WPF's Left/Top, which are device-independent units on
    /// the virtual desktop. Converting between those and screen coordinates needs a DPI, and
    /// picking the right one is ambiguous the moment two monitors scale differently or a
    /// resolution change alters the scale underneath us. SetWindowPos takes real pixels, so
    /// there is nothing to convert and nothing to get wrong.
    /// </summary>
    private bool _positioning;

    /// The sides an overlay anchored here lies against: a fraction of 0 or 1 is all the way to
    /// that side, as in the in-game overlay's Touching (InGame/src/ui.cpp).
    internal static FlushEdges Flush(float ax, float ay)
    {
        var e = FlushEdges.None;
        if (ax <= 0f) e |= FlushEdges.Left;
        if (ax >= 1f) e |= FlushEdges.Right;
        if (ay <= 0f) e |= FlushEdges.Top;
        if (ay >= 1f) e |= FlushEdges.Bottom;
        return e;
    }

    /// The user is placing the overlay right now, by dragging the window itself or by moving
    /// one of the position sliders. Either way, nothing else may move it until they finish.
    private bool IsUserPositioning =>
        _userDragging || ViewModels.SettingsViewModel.Instance.IsAdjustingPosition;

    private void ApplyPosition()
    {
        // Re-entrancy guard: this is reached from SizeChanged, and the UpdateLayout below
        // can itself raise SizeChanged. Without this the two would queue each other on the
        // dispatcher indefinitely.
        if (_positioning) return;

        // Never re-place the overlay out from under someone who is positioning it, whether by
        // dragging the window or by moving a position slider. Checked here rather than only at
        // the event sources, because a queued ApplyPosition can still be delivered mid-drag by
        // DragMove's nested message loop.
        if (IsUserPositioning) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _positioning = true;
        try
        {
            PlaceWindow(hwnd);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void PlaceWindow(IntPtr hwnd)
    {
        var settings = SettingsService.Instance.Settings;

        // Against a screen edge the panel joins it, as the in-game overlay does (26 Nov
        // 2026): a corner preset sits flush on its two sides, and a dragged overlay on any side
        // it was dropped against. The same rule as in games (WindowOverlayAnchor), so the two
        // agree on where the overlay touches the edge. Set before measuring, because the curves
        // where the panel meets the edge take a little room.
        var (ax, ay) = Services.InGame.InGameOverlayService.WindowOverlayAnchor(settings);
        var flush = Flush(ax, ay);
        NormalPanel.Flush = CompactPanel.Flush = BarPanel.Flush = flush;

        // Force layout first: SizeToContent is frozen while hidden, and a stale size would
        // put every corner and clamp calculation below out by the difference.
        UpdateLayout();

        if (!GetWindowRect(hwnd, out var bounds)) return;
        int width  = bounds.Right  - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return;

        var work     = ResolveTargetScreen(hwnd).WorkingArea;

        // A straight line wider than the screen: the list instead, until the look changes.
        if (BarPanel.Visibility == Visibility.Visible && width > work.Width)
        {
            _barTooWide = true;
            ApplyCompactMode();
            UpdateLayout();
            if (!GetWindowRect(hwnd, out bounds)) return;
            width  = bounds.Right  - bounds.Left;
            height = bounds.Bottom - bounds.Top;
        }

        // Flush against the edge in a corner, as the panel is drawn to be. This was 2, and 20
        // before that, which left corner-snapped overlays sitting inside the screen when the
        // point of a corner preset is to be in the corner.
        const int margin = 0;
        int x, y;

        if (settings.OverlayPosition == "Custom" && settings.OverlayAnchorFx >= 0)
        {
            // Scale the stored fraction back across whatever room there is now, so the
            // overlay lands in the same relative spot at any resolution instead of being
            // clamped to an edge when the screen gets smaller.
            int roomX = Math.Max(0, work.Width  - width);
            int roomY = Math.Max(0, work.Height - height);

            x = work.Left + (int)Math.Round(Math.Clamp(settings.OverlayAnchorFx, 0, 1) * roomX);
            y = work.Top  + (int)Math.Round(Math.Clamp(settings.OverlayAnchorFy, 0, 1) * roomY);
        }
        else
        {
            switch (settings.OverlayPosition)
            {
                case "TopLeft":     x = work.Left  + margin;          y = work.Top    + margin;          break;
                case "BottomLeft":  x = work.Left  + margin;          y = work.Bottom - height - margin; break;
                case "BottomRight": x = work.Right - width - margin;  y = work.Bottom - height - margin; break;
                default:            x = work.Right - width - margin;  y = work.Top    + margin;          break;
            }
        }

        // Keep it on the monitor it belongs to without persisting the correction: the stored
        // anchor stays valid for the resolution the user chose it at, so a temporary drop to
        // a smaller mode does not permanently move an overlay placed at a larger one.
        // Clamped to the work area itself, with no inset. There used to be an 8px margin
        // here, which meant the overlay could never sit flush against a screen edge: drag it
        // into the corner, let go, and it drifted 8px back out. Dragging into a corner to sit
        // just above the tray is one of the main things people want from a free-placed
        // overlay, and the margin quietly forbade it. Keeping it on the monitor does not
        // require holding it away from the edge.
        //
        // The corner presets place themselves right against the edge too, since 26 Nov 2026,
        // where the panel is drawn to join it.
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right  - width));
        y = Math.Clamp(y, work.Top,  Math.Max(work.Top,  work.Bottom - height));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // The settings panel reads the position once when it opens, which during startup is
        // before this has run — and the overlay is placed again every time tiles resize it.
        // Without telling the panel each time, its X and Y boxes kept whatever they happened
        // to read first and only became correct after a drag, which does notify.
        ViewModels.SettingsViewModel.Instance.NotifyPositionChanged();
    }

    /// <summary>
    /// A display's geometry, used to recognise it when its device name has been renumbered.
    ///
    /// Not a perfect identity: two identical monitors arranged symmetrically could in principle
    /// swap and still match. It is only ever consulted after the device name has already
    /// failed, and landing on a screen of the right size and position is a great deal better
    /// than the previous behaviour, which was to give up and use whichever display the window
    /// happened to be on.
    /// </summary>
    internal static string BoundsKey(System.Windows.Forms.Screen screen)
    {
        var b = screen.Bounds;
        return $"{b.Left},{b.Top},{b.Width},{b.Height}";
    }

    /// <summary>
    /// Which display a corner position belongs on: the one with the recorded bounds, then the
    /// recorded index, or -1 for neither.
    /// </summary>
    /// <remarks>
    /// Kept apart from the Screen objects so it can be tested without a second monitor.
    /// </remarks>
    internal static int PickPresetScreen(IReadOnlyList<string> screenBounds, string savedBounds, int savedIndex)
    {
        if (!string.IsNullOrEmpty(savedBounds))
        {
            for (int i = 0; i < screenBounds.Count; i++)
                if (string.Equals(screenBounds[i], savedBounds, StringComparison.Ordinal)) return i;
        }

        return savedIndex >= 0 && savedIndex < screenBounds.Count ? savedIndex : -1;
    }

    /// <summary>
    /// The monitor the overlay belongs on. A custom position remembers its exact monitor by
    /// device name, then by bounds; corner presets follow the monitor selected in settings, by
    /// bounds and then by index. Falls back to wherever the window currently is, then the
    /// primary, so unplugging a display cannot strand the overlay off-screen.
    /// </summary>
    private static System.Windows.Forms.Screen ResolveTargetScreen(IntPtr hwnd)
    {
        var settings = SettingsService.Instance.Settings;
        var screens  = System.Windows.Forms.Screen.AllScreens;

        if (settings.OverlayPosition == "Custom" && !string.IsNullOrEmpty(settings.OverlayMonitorId))
        {
            foreach (var screen in screens)
                if (string.Equals(screen.DeviceName, settings.OverlayMonitorId, StringComparison.OrdinalIgnoreCase))
                    return screen;

            // The name did not match, which does not mean the display is gone. Windows
            // renumbers device names when the set of graphics adapters changes, so the same
            // physical panel can come back as DISPLAY11 where it used to be DISPLAY1. Its
            // geometry does not change with it, so that is what identifies it now.
            foreach (var screen in screens)
                if (string.Equals(BoundsKey(screen), settings.OverlayMonitorBounds, StringComparison.Ordinal))
                    return screen;
        }
        else
        {
            int index = PickPresetScreen(screens.Select(BoundsKey).ToList(),
                                         settings.PresetMonitorBounds, settings.SelectedMonitorIndex);
            if (index >= 0)
            {
                // Kept in step, so the panel highlights the display actually in use. Not saved
                // here, since nothing the user chose has changed, only Windows' numbering; the
                // next save of any setting records the corrected number, which is the true one.
                settings.SelectedMonitorIndex = index;
                return screens[index];
            }
        }

        try
        {
            if (hwnd != IntPtr.Zero) return System.Windows.Forms.Screen.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            // Falls through to the primary display, which is a working overlay in the wrong
            // place. That is precisely the complaint that is impossible to explain afterwards
            // without knowing this happened.
            Services.LogService.Error(nameof(OverlayWindow),
                "Could not identify the display the overlay is on; falling back to the primary", ex);
        }

        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    /// <summary>
    /// Records where the overlay now is, as a fraction of its monitor's work area.
    ///
    /// <paramref name="writeToDisk"/> is false while a position slider is being dragged, so a
    /// drag across the screen does not mean a settings file write per pixel. The anchor in
    /// memory is still updated every time, and that part is not optional: ApplyPosition
    /// re-places the overlay from that anchor, and the overlay auto-sizes on almost every
    /// sensor poll. Leaving it stale meant the next poll during a drag snapped the overlay
    /// back to wherever it sat before the drag began.
    /// </summary>
    private void SavePosition(bool writeToDisk = true)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return;

        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var work   = screen.WorkingArea;

        int width  = bounds.Right  - bounds.Left;
        int height = bounds.Bottom - bounds.Top;

        // Fraction of the room the overlay has to move in, so an overlay dropped against an
        // edge stays against that edge and one dropped in the middle stays in the middle,
        // whatever the resolution becomes later.
        int roomX = work.Width  - width;
        int roomY = work.Height - height;

        var settings = SettingsService.Instance.Settings;
        settings.OverlayPosition  = "Custom";
        settings.OverlayMonitorId     = screen.DeviceName;
        settings.OverlayMonitorBounds = BoundsKey(screen);

        // The display the overlay is actually on, recorded in the form the panel's buttons
        // use. Dragging the overlay to another screen used to leave the old screen's button
        // selected, which is both wrong and a dead end: clicking it did nothing, because the
        // selection had not changed as far as the property was concerned.
        var all = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < all.Length; i++)
        {
            if (!string.Equals(all[i].DeviceName, screen.DeviceName, StringComparison.Ordinal)) continue;
            settings.SelectedMonitorIndex = i;
            settings.PresetMonitorBounds  = BoundsKey(all[i]);
            break;
        }
        settings.OverlayAnchorFx  = roomX > 0 ? Math.Clamp((bounds.Left - work.Left) / (double)roomX, 0, 1) : 0;
        settings.OverlayAnchorFy  = roomY > 0 ? Math.Clamp((bounds.Top  - work.Top)  / (double)roomY, 0, 1) : 0;

        // Legacy fields retired once a position has been saved in the new form.
        settings.OverlayCustomX = -1;
        settings.OverlayCustomY = -1;

        if (writeToDisk) SettingsService.Instance.Save();

        // Keep the settings panel's position sliders showing where the overlay actually is,
        // so dragging it and moving the sliders stay two views of one position.
        ViewModels.SettingsViewModel.Instance.NotifyPositionChanged();
    }

    /// <summary>
    /// The overlay's position and the room it has to move in, both in physical pixels on its
    /// own monitor. The settings panel works in pixels because that is what people expect to
    /// type, while storage stays fractional so the position survives a resolution change.
    /// </summary>
    public (int X, int Y, int MaxX, int MaxY) GetPositionPixels()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return (0, 0, 0, 0);

        // Measured against the monitor the window is actually on, not the one settings say
        // it belongs to. Those can disagree — during startup before the position has been
        // applied, or when a display has been reconfigured — and measuring against the wrong
        // one produced numbers that looked plausible but described nothing. With monitors at
        // different origins the X offset came out negative and clamped to 0, while Y absorbed
        // the other monitor's origin and reported a confident, wrong value.
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;

        int maxX = Math.Max(0, work.Width  - (bounds.Right  - bounds.Left));
        int maxY = Math.Max(0, work.Height - (bounds.Bottom - bounds.Top));

        return (Math.Clamp(bounds.Left - work.Left, 0, maxX),
                Math.Clamp(bounds.Top  - work.Top,  0, maxY),
                maxX, maxY);
    }

    /// <summary>
    /// Records where the overlay was placed at launch and what it was placed from.
    ///
    /// One line per run. Overlay placement has been the source of several hard-to-pin
    /// reports — it depends on saved anchors, monitor identity, work areas and window size
    /// all agreeing, and a screenshot of the result cannot show which of those disagreed.
    /// </summary>
    private void LogStartupPlacement()
    {
        try
        {
            var settings = SettingsService.Instance.Settings;
            var hwnd     = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds))
            {
                LogService.Warn(nameof(OverlayWindow), "Placed with no window handle yet.");
                return;
            }

            var actual   = System.Windows.Forms.Screen.FromHandle(hwnd);
            var intended = ResolveTargetScreen(hwnd);
            var (x, y, maxX, maxY) = GetPositionPixels();

            LogService.Info(nameof(OverlayWindow),
                $"Placed at screen ({bounds.Left},{bounds.Top}) size {bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}; " +
                $"reported ({x},{y}) of ({maxX},{maxY}) on {actual.DeviceName}; " +
                $"mode={settings.OverlayPosition} anchor=({settings.OverlayAnchorFx:F4},{settings.OverlayAnchorFy:F4}) " +
                $"savedMonitor={(string.IsNullOrEmpty(settings.OverlayMonitorId) ? "(none)" : settings.OverlayMonitorId)} " +
                $"intended={intended.DeviceName} index={settings.SelectedMonitorIndex} " +
                $"screens=[{string.Join(" ", System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName + s.WorkingArea))}]");
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(OverlayWindow), "Could not record startup placement", ex);
        }
    }

    /// Friendly name of the monitor the overlay is currently on, for the position hint.
    public string CurrentDisplayLabel
    {
        get
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return "";

            var screen  = System.Windows.Forms.Screen.FromHandle(hwnd);
            var screens = System.Windows.Forms.Screen.AllScreens;

            for (int i = 0; i < screens.Length; i++)
                if (screens[i].DeviceName == screen.DeviceName)
                    return $"Display {i + 1}";

            return "";
        }
    }

    /// <summary>
    /// Moves the overlay to an exact pixel position on its monitor.
    ///
    /// <paramref name="persist"/> is false while the user is scrubbing a position field, so
    /// a drag across the screen moves the overlay live without writing settings to disk on
    /// every mouse move. The final position is saved once the drag ends.
    /// </summary>
    public void SetPositionPixels(int x, int y, bool persist = true)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return;

        // Same monitor the boxes are showing, so typing 100 moves it to 100 on the display
        // the user is reading the number from.
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;

        int maxX = Math.Max(0, work.Width  - (bounds.Right  - bounds.Left));
        int maxY = Math.Max(0, work.Height - (bounds.Bottom - bounds.Top));

        SetWindowPos(hwnd, IntPtr.Zero,
            work.Left + Math.Clamp(x, 0, maxX),
            work.Top  + Math.Clamp(y, 0, maxY),
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Always record the new anchor; only the disk write is deferred.
        SavePosition(writeToDisk: persist);
    }

    /// <summary>
    /// Converts a position saved by an older build. Those were absolute device-independent
    /// coordinates, so rather than trying to reverse the DPI maths we place the window where
    /// they say once, then record it in the new form from the result.
    /// </summary>
    private void MigrateLegacyPosition()
    {
        var settings = SettingsService.Instance.Settings;

        if (settings.OverlayPosition != "Custom") return;
        if (settings.OverlayAnchorFx >= 0) return;              // already migrated
        if (settings.OverlayCustomX < 0 || settings.OverlayCustomY < 0) return;

        Left = settings.OverlayCustomX;
        Top  = settings.OverlayCustomY;
        UpdateLayout();

        SavePosition();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Opacity is deliberately not assigned here. The window binds it to the view
            // model, and assigning the property in code replaces that binding with a local
            // value — after which opacity only moved when settings were saved. Once saving
            // became debounced that turned into a visible delay behind the slider.
            if (SettingsService.Instance.Settings.OverlayPosition != "Custom")
                ApplyPosition();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        Release();
        base.OnClosed(e);
    }

    /// <summary>
    /// Lets go of everything this window subscribed to or started. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// Public for the one caller that cannot rely on OnClosed: App, when this window was built
    /// and then failed to show. The constructor has already subscribed to a static Windows event
    /// by then, which pins the window for the life of the process, and each retry built another.
    /// </remarks>
    /// <summary>
    /// Hides what the overlay draws while the in-game overlay draws inside the game in front
    /// (App.SetOverlaySuppressed). The window stays shown; its content goes, and with it every
    /// pixel that could catch a click.
    /// </summary>
    public void SetSuppressed(bool suppressed)
    {
        DragBorder.Visibility = suppressed ? Visibility.Hidden : Visibility.Visible;
    }

    public void Release()
    {
        _topmostTimer?.Stop();
        _topmostTimer = null;
        StopMenuMouseHook();
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventProc = null;

        _displayChangeTimer?.Stop();
        _displayChangeTimer = null;

        // SystemEvents is static, so failing to detach here would pin this window for
        // the lifetime of the process.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
