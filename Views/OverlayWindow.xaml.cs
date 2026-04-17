using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
        ApplyPosition();
        ApplyCompactMode();
        ApplyDragState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd  = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
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
        var screen   = SystemParameters.WorkArea;

        if (settings.OverlayCustomX >= 0 && settings.OverlayCustomY >= 0)
        {
            Left = settings.OverlayCustomX;
            Top  = settings.OverlayCustomY;
            return;
        }

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
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }
}
