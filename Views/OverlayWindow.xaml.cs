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
    private ContextMenu?       _activeMenu;

    private readonly OverlayViewModel _vm;

    public OverlayWindow()
    {
        InitializeComponent();
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

        // Re-anchoring mid-adjustment fights the slider. The overlay resizes whenever a
        // reading changes width, which is most polls, and re-placing from the anchor shifts it
        // by a pixel or two each time — enough to tug the slider's value away from the thumb
        // the user is holding. The resting place is re-anchored once the drag settles.
        if (ViewModels.SettingsViewModel.Instance.IsAdjustingPosition) return;

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
        _topmostTimer.Tick += (_, _) => ForceTopmost();

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
        var insertAfter = HWND_TOPMOST;
        if (!IsForegroundWindowFullScreen())
        {
            var taskbar = FindTaskbarOnOurMonitor(hwnd);
            if (taskbar != IntPtr.Zero) insertAfter = taskbar;
        }

        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// The window we are currently parked beneath, so the call isn't repeated on every
    /// foreground change and timer tick for as long as one surface stays open.
    private IntPtr _anchoredBehind;

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

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;
        DragMove();
        SavePosition();
    }

    /// Uniform scale (Option B): the whole panel grows/shrinks together via a
    /// LayoutTransform, so the window's SizeToContent measures the scaled size
    /// correctly and grows toward bottom-right — the grip's own corner.
    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;
        double delta = (e.HorizontalChange + e.VerticalChange) / 2.0;
        _vm.OverlayScale += delta / 300.0;
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settings = SettingsService.Instance.Settings;
        settings.OverlayScale = _vm.OverlayScale;
        SettingsService.Instance.Save();
    }

    /// Quick access to Settings while free-dragging — the overlay is click-through
    /// otherwise, so this only applies in drag mode where clicks already reach it.
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;

        var app       = (App)System.Windows.Application.Current;
        var itemStyle = (Style)FindResource("OverlayMenuItem");

        var menu = new ContextMenu { Style = (Style)FindResource("OverlayContextMenu") };

        var controlPanelItem = new MenuItem { Header = "Open Control Panel", Style = itemStyle };
        controlPanelItem.Click += (_, _) => app.ShowControlPanel();
        menu.Items.Add(controlPanelItem);

        var hideOverlayItem = new MenuItem { Header = "Hide Overlay", Style = itemStyle };
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

    private void ApplyCompactMode()
    {
        bool compact = _vm.IsCompactMode;
        NormalPanel.Visibility  = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = compact ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.IsCompactMode))
                ApplyCompactMode();
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

    private void ApplyPosition()
    {
        // Re-entrancy guard: this is reached from SizeChanged, and the UpdateLayout below
        // can itself raise SizeChanged. Without this the two would queue each other on the
        // dispatcher indefinitely.
        if (_positioning) return;

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
        // Force layout first: SizeToContent is frozen while hidden, and a stale size would
        // put every corner and clamp calculation below out by the difference.
        UpdateLayout();

        if (!GetWindowRect(hwnd, out var bounds)) return;
        int width  = bounds.Right  - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return;

        var settings = SettingsService.Instance.Settings;
        var work     = ResolveTargetScreen(hwnd).WorkingArea;

        const int margin = 20;
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
        const int edge = 8;
        x = Math.Clamp(x, work.Left + edge, Math.Max(work.Left + edge, work.Right  - width  - edge));
        y = Math.Clamp(y, work.Top  + edge, Math.Max(work.Top  + edge, work.Bottom - height - edge));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // The settings panel reads the position once when it opens, which during startup is
        // before this has run — and the overlay is placed again every time tiles resize it.
        // Without telling the panel each time, its X and Y boxes kept whatever they happened
        // to read first and only became correct after a drag, which does notify.
        ViewModels.SettingsViewModel.Instance.NotifyPositionChanged();
    }

    /// <summary>
    /// The monitor the overlay belongs on. A custom position remembers its exact monitor by
    /// device name; corner presets follow whichever monitor is selected in settings. Falls
    /// back to wherever the window currently is, then the primary, so unplugging a display
    /// cannot strand the overlay off-screen.
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
        }
        else
        {
            int index = settings.SelectedMonitorIndex;
            if (index >= 0 && index < screens.Length) return screens[index];
        }

        try
        {
            if (hwnd != IntPtr.Zero) return System.Windows.Forms.Screen.FromHandle(hwnd);
        }
        catch { }

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
        settings.OverlayMonitorId = screen.DeviceName;
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
        base.OnClosed(e);
    }
}
