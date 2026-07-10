using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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

    private readonly OverlayViewModel _vm;

    public OverlayWindow()
    {
        InitializeComponent();
        _vm = OverlayViewModel.Instance;
        DataContext = _vm;

        Loaded += OnLoaded;
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactMode();
        ApplyDragState();
        // Defer position until after first render so ActualWidth/ActualHeight are correct
        Dispatcher.InvokeAsync(ApplyPosition, System.Windows.Threading.DispatcherPriority.Render);
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
    /// widgets). Setting Topmost once is not enough: when another app creates its own
    /// topmost window it can push us down, so we re-assert HWND_TOPMOST whenever the
    /// foreground window changes, plus a low-frequency timer as a safety net.
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
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
        }
        else
        {
            DragBorder.MouseLeftButtonDown -= OnDragStart;
        }
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragEnabled) return;
        DragMove();
        SavePosition();
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

        if (settings.OverlayCustomX >= 0 && settings.OverlayCustomY >= 0)
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
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        double sx = g.DpiX / 96.0;
        double sy = g.DpiY / 96.0;
        return new System.Windows.Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
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
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventProc = null;

        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }
}
