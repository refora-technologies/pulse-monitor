using System.ComponentModel;
using System.Diagnostics;
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

    // --- Per-monitor DPI lookup (for correctly placing the overlay on mixed-scaling setups) ---
    private enum MonitorDpiType { Effective = 0 }
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("Shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    // --- Always-on-top enforcement -------------------------------------------------
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
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

        // The resolution may have changed while we were hidden, which would leave the
        // saved position outside the new desktop.
        if (IsVisible) Dispatcher.InvokeAsync(ClampIntoView, DispatcherPriority.Loaded);
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
            _displayChangeTimer ??= BuildDisplayChangeTimer();
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        });
    }

    private DispatcherTimer BuildDisplayChangeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyPosition();
            ClampIntoView();
        };
        return timer;
    }

    /// Pulls the overlay back onto the visible desktop. Corner presets are already
    /// recomputed by ApplyPosition; this exists for custom drag positions, which are
    /// absolute and can fall off-screen when the resolution shrinks.
    private void ClampIntoView()
    {
        var settings = SettingsService.Instance.Settings;
        var screen   = GetMonitorWorkAreaDip(settings.SelectedMonitorIndex);

        double w = ActualWidth  > 0 ? ActualWidth  : 200;
        double h = ActualHeight > 0 ? ActualHeight : 200;

        const double margin = 8;
        double minLeft = screen.Left + margin;
        double minTop  = screen.Top  + margin;
        double maxLeft = Math.Max(minLeft, screen.Right  - w - margin);
        double maxTop  = Math.Max(minTop,  screen.Bottom - h - margin);

        double left = Math.Clamp(Left, minLeft, maxLeft);
        double top  = Math.Clamp(Top,  minTop,  maxTop);

        if (Math.Abs(left - Left) < 0.5 && Math.Abs(top - Top) < 0.5) return;

        Left = left;
        Top  = top;

        // Deliberately not saved. The stored position is still valid for the resolution
        // the user chose it at, so overwriting it here would mean a temporary drop to
        // 1080p permanently moved an overlay the user had placed at 1440p. Leaving it
        // alone means the original spot is restored when the resolution comes back.
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactMode();
        ApplyDragState();
        // Defer position until after first render so ActualWidth/ActualHeight are correct
        Dispatcher.InvokeAsync(() =>
        {
            ApplyPosition();
            // Covers launching while the resolution is lower than when the position was saved.
            ClampIntoView();
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

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _topmostTimer.Tick += (_, _) => ForceTopmost();
        _topmostTimer.Start();
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

        // A transient shell flyout (Quick Settings, tray overflow, Start menu, search) is
        // currently open and focused — don't fight it for the top slot at all.
        if (IsShellForegroundWindow()) return;

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
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero) insertAfter = taskbar;
        }

        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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

    private static bool IsShellForegroundWindow()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return false;
            using var process = Process.GetProcessById((int)pid);
            return ShellProcessNames.Contains(process.ProcessName);
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

    private void ApplyPosition()
    {
        var settings = SettingsService.Instance.Settings;

        if (settings.OverlayPosition == "Custom")
        {
            Left = settings.OverlayCustomX;
            Top  = settings.OverlayCustomY;
            return;
        }

        var screen = GetMonitorWorkAreaDip(settings.SelectedMonitorIndex);

        const double margin = 20;
        switch (settings.OverlayPosition)
        {
            case "TopLeft":
                Left = screen.Left + margin;
                Top  = screen.Top  + margin;
                break;
            case "BottomLeft":
                Left = screen.Left + margin;
                Top  = screen.Bottom - ActualHeight - margin;
                break;
            case "BottomRight":
                Left = screen.Right - ActualWidth - margin;
                Top  = screen.Bottom - ActualHeight - margin;
                break;
            default: // TopRight
                Left = screen.Right - ActualWidth  - margin;
                Top  = screen.Top   + margin;
                break;
        }
    }

    private static System.Windows.Rect GetMonitorWorkAreaDip(int monitorIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (monitorIndex < 0 || monitorIndex >= screens.Length) monitorIndex = 0;
        var wa = screens[monitorIndex].WorkingArea;

        var (sx, sy) = GetMonitorScale(wa);
        return new System.Windows.Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
    }

    /// Looks up the DPI of the monitor containing <paramref name="workArea"/> specifically —
    /// using the desktop/primary DPI here would misplace the overlay on any secondary
    /// monitor whose scaling differs from the primary's.
    private static (double sx, double sy) GetMonitorScale(System.Drawing.Rectangle workArea)
    {
        try
        {
            var rect = new RECT { Left = workArea.Left, Top = workArea.Top, Right = workArea.Right, Bottom = workArea.Bottom };
            var hMonitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero &&
                GetDpiForMonitor(hMonitor, MonitorDpiType.Effective, out uint dpiX, out uint dpiY) == 0)
            {
                return (dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch { }

        // Fallback if the per-monitor DPI API is unavailable for some reason.
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return (g.DpiX / 96.0, g.DpiY / 96.0);
    }

    private void SavePosition()
    {
        var settings = SettingsService.Instance.Settings;
        settings.OverlayCustomX  = Left;
        settings.OverlayCustomY  = Top;
        settings.OverlayPosition = "Custom";
        SettingsService.Instance.Save();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            Opacity = SettingsService.Instance.Settings.OverlayOpacity;
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
