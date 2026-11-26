using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Pulse.Views;

/// <summary>
/// Everything this PC reports, like HWMonitor, in a window of its own, opened from the control
/// panel's Sensors button. One at a time: the button brings an open one forward.
/// </summary>
/// <remarks>
/// Its own window rather than a page of the panel (owner's choice of the mockups, 26 Nov 2026),
/// so it can stay open on a second screen while the panel is closed. It asks the sensor host for
/// every sensor only while it is on screen: closing it stops that, and so does minimizing it.
/// </remarks>
public partial class SensorsWindow : Window
{
    private static SensorsWindow? _open;
    private readonly SensorsViewModel _vm;

    public static void ShowFor(SensorsViewModel vm)
    {
        if (_open is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }
        _open = new SensorsWindow(vm);
        _open.Show();
    }

    private SensorsWindow(SensorsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        App.ApplyIcon(this);
        vm.Open();

        // Off the screen, the host stops sending every sensor; back on it, it starts again.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _vm.Pause(); else _vm.Resume(); };
        Closed += (_, _) => { _vm.Close(); _open = null; };

        // The clip that rounds the corners follows the window's size.
        WindowBorder.SizeChanged += (_, _) =>
            ShellGrid.Clip = new RectangleGeometry(new Rect(0, 0, WindowBorder.ActualWidth - 2, WindowBorder.ActualHeight - 2), 9, 9);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        _vm.Select(null);
        OverviewScroll.ScrollToTop();
    }

    /// A part in the sidebar or its card on the overview.
    private void Part_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SensorPart part) return;
        _vm.Select(part);
        PartScroll.ScrollToTop();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+F searches, as everywhere; Escape clears the search, then goes back to the overview.
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_vm.Searching) _vm.Search = "";
            else _vm.Select(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
