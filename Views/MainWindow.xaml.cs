using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Services;
using Pulse.ViewModels;

using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfStyle = System.Windows.Style;

namespace Pulse.Views;

public partial class MainWindow : Window
{
    private SettingsViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _vm = SettingsViewModel.Instance;
            DataContext = _vm;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ViewModel init error: {ex}");
        }

        Loaded += (_, _) =>
        {
            // Clip content to rounded border shape
            WindowBorder.Clip = new RectangleGeometry(
                new System.Windows.Rect(0, 0, WindowBorder.ActualWidth, WindowBorder.ActualHeight),
                16, 16);

            HighlightActivePollingRate();
            UpdateOverlayButton();
        };
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.MinimizeToTray == true)
            Hide();
        else
            WpfApplication.Current.Shutdown();
    }

    private void BtnOverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WpfApplication.Current is App app)
        {
            if (app.IsOverlayVisible)
                app.HideOverlay();
            else
                app.ShowOverlay();

            UpdateOverlayButton();
        }
    }

    public void UpdateOverlayButton()
    {
        if (WpfApplication.Current is App app)
        {
            bool visible = app.IsOverlayVisible;
            OverlayBtnIcon.Text = visible ? "■" : "▶";
            OverlayBtnText.Text = visible ? "Hide Overlay" : "Show Overlay";
        }
    }

    private void PollRate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && double.TryParse(btn.Tag?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) && _vm != null)
        {
            _vm.PollingInterval = sec;
            HighlightActivePollingRate();
        }
    }

    private void HighlightActivePollingRate()
    {
        if (_vm == null || PollRatePanel == null) return;

        var activeStyle = (WpfStyle)FindResource("PillBtnActive");
        var normalStyle = (WpfStyle)FindResource("PillBtn");

        foreach (var child in PollRatePanel.Children)
        {
            if (child is WpfButton btn)
            {
                bool isActive = double.TryParse(btn.Tag?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) && Math.Abs(sec - _vm.PollingInterval) < 0.01;
                btn.Style = isActive ? activeStyle : normalStyle;
            }
        }
    }

    private void Position_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && _vm != null)
        {
            _vm.OverlayPosition = btn.Tag?.ToString() ?? "TopRight";
            _vm.IsDragEnabled = false;
            SettingsService.Instance.Settings.OverlayCustomX = -1;
            SettingsService.Instance.Settings.OverlayCustomY = -1;
            SettingsService.Instance.Save();
        }
    }
}
