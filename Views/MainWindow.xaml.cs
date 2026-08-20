using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            WindowBorder.Clip = new RectangleGeometry(
                new System.Windows.Rect(0, 0, WindowBorder.ActualWidth, WindowBorder.ActualHeight),
                18, 18);

            HighlightActivePollingRate();
            HighlightActivePosition();
            UpdateOverlayButton();
            PopulateMonitorButtons();
            PopulateGpuButtons();

            // The GPU list isn't known until the first sensor poll completes, so rebuild
            // the picker when it arrives (and if an eGPU is plugged in later).
            Pulse.Services.HardwareService.Instance.GpuListChanged += OnGpuListChanged;

            PollRatePanel.SizeChanged += (_, _) => UpdateSegIndicator(false);
            Dispatcher.InvokeAsync(() => UpdateSegIndicator(false),
                System.Windows.Threading.DispatcherPriority.Render);
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

        var activeStyle = (WpfStyle)FindResource("SegBtnActive");
        var normalStyle = (WpfStyle)FindResource("SegBtn");

        foreach (var child in PollRatePanel.Children)
        {
            if (child is WpfButton btn)
            {
                bool isActive = double.TryParse(btn.Tag?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) && Math.Abs(sec - _vm.PollingInterval) < 0.01;
                btn.Style = isActive ? activeStyle : normalStyle;
            }
        }

        UpdateSegIndicator(true);
    }

    private void UpdateSegIndicator(bool animate)
    {
        if (_vm == null || SegIndicator == null || PollRatePanel == null) return;
        if (PollRatePanel.ActualWidth <= 0) return;

        double segW = PollRatePanel.ActualWidth / 4.0;
        SegIndicator.Width = segW;

        int idx = _vm.PollingInterval switch
        {
            <= 0.6 => 0,
            <= 1.5 => 1,
            <= 3.0 => 2,
            _      => 3,
        };

        double targetX = idx * segW;

        if (animate)
        {
            var anim = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            SegIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            SegIndicatorTranslate.X = targetX;
        }
    }

    private void Position_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && _vm != null)
        {
            _vm.SetPositionPreset(btn.Tag?.ToString() ?? "TopRight");
            HighlightActivePosition();
        }
    }

    private void HighlightActivePosition()
    {
        if (_vm == null || PositionPanel == null) return;
        var activeStyle = (WpfStyle)FindResource("CornerBtnActive");
        var normalStyle = (WpfStyle)FindResource("CornerBtn");
        foreach (var child in PositionPanel.Children)
        {
            if (child is WpfButton btn)
                btn.Style = btn.Tag?.ToString() == _vm.OverlayPosition ? activeStyle : normalStyle;
        }
    }

    private void PopulateMonitorButtons()
    {
        if (_vm == null || MonitorPanel == null || MonitorSelectionRow == null) return;

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length <= 1)
        {
            MonitorSelectionRow.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        MonitorSelectionRow.Visibility = System.Windows.Visibility.Visible;
        MonitorPanel.Children.Clear();

        var activeStyle = (WpfStyle)FindResource("MonitorBtnActive");
        var normalStyle = (WpfStyle)FindResource("MonitorBtn");

        for (int i = 0; i < screens.Length; i++)
        {
            var idx = i;
            var btn = new WpfButton
            {
                Content = $"Display {i + 1}",
                Tag = i,
                Style = i == _vm.SelectedMonitorIndex ? activeStyle : normalStyle,
                Margin = new System.Windows.Thickness(0, 0, i < screens.Length - 1 ? 8 : 0, 0),
            };
            btn.Click += Monitor_Click;
            MonitorPanel.Children.Add(btn);
        }
    }

    private void OnGpuListChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(PopulateGpuButtons);

    private void PopulateGpuButtons()
    {
        if (_vm == null || GpuPanel == null || GpuSourceSection == null) return;

        var gpus = Pulse.Services.HardwareService.Instance.AvailableGpus;

        // Nothing to choose between on a single-GPU machine, so don't add noise.
        if (gpus.Count < 2)
        {
            GpuSourceSection.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        GpuSourceSection.Visibility = System.Windows.Visibility.Visible;
        GpuPanel.Children.Clear();

        AddGpuButton("Auto", "", gpus.Count > 0);
        foreach (var gpu in gpus)
            AddGpuButton(gpu.Name + (gpu.IsDiscrete ? "" : "  (integrated)"), gpu.Id, true);
    }

    private void AddGpuButton(string label, string id, bool _)
    {
        var activeStyle = (WpfStyle)FindResource("GpuBtnActive");
        var normalStyle = (WpfStyle)FindResource("GpuBtn");

        var btn = new WpfButton
        {
            Content = label,
            Tag     = id,
            Style   = id == _vm!.SelectedGpuId ? activeStyle : normalStyle,
            Margin  = new System.Windows.Thickness(0, 0, 0, 6),
        };
        btn.Click += Gpu_Click;
        GpuPanel.Children.Add(btn);
    }

    private void Gpu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || _vm == null) return;
        _vm.SelectedGpuId = btn.Tag?.ToString() ?? "";

        var activeStyle = (WpfStyle)FindResource("GpuBtnActive");
        var normalStyle = (WpfStyle)FindResource("GpuBtn");
        foreach (var child in GpuPanel.Children)
        {
            if (child is WpfButton b)
                b.Style = (b.Tag?.ToString() ?? "") == _vm.SelectedGpuId ? activeStyle : normalStyle;
        }
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || _vm == null) return;
        _vm.SelectedMonitorIndex = (int)btn.Tag;

        var activeStyle = (WpfStyle)FindResource("MonitorBtnActive");
        var normalStyle = (WpfStyle)FindResource("MonitorBtn");
        foreach (var child in MonitorPanel.Children)
        {
            if (child is WpfButton b)
                b.Style = (int)b.Tag == _vm.SelectedMonitorIndex ? activeStyle : normalStyle;
        }

        HighlightActivePosition();
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.IsCheckingUpdate || _vm.IsDownloading) return;

        // Once a check has already found an update, this button switches to actually
        // starting that download instead of redundantly re-checking GitHub again.
        if (_vm.IsUpdateAvailable) await _vm.InstallUpdateAsync();
        else await _vm.CheckForUpdatesAsync(true);
    }

    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) await _vm.InstallUpdateAsync();
    }

    private void BtnDismissBanner_Click(object sender, RoutedEventArgs e)
        => _vm?.DismissBanner();

    protected override void OnClosed(EventArgs e)
    {
        // HardwareService is a singleton and this window is recreated every time the
        // control panel is reopened, so leaving this attached would pin every closed
        // instance in memory for the lifetime of the app.
        Pulse.Services.HardwareService.Instance.GpuListChanged -= OnGpuListChanged;
        base.OnClosed(e);
    }
}
