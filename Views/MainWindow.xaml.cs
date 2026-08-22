using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Services;
using Pulse.ViewModels;

using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfStyle = System.Windows.Style;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDataObject = System.Windows.DataObject;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfBorder = System.Windows.Controls.Border;
using WpfTextBox = System.Windows.Controls.TextBox;

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
            // Debug.WriteLine only reaches an attached debugger, so on a user's machine this
            // failure left no trace at all — and it is the one that leaves the whole control
            // panel unbound and inert.
            LogService.Error(nameof(MainWindow), "Settings view model failed to initialise", ex);
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
            RefreshPositionBoxes();

            // Dragging the overlay updates these, so the typed values and the overlay never
            // disagree about where it is.
            if (_vm != null) _vm.PropertyChanged += OnSettingsViewModelPropertyChanged;

            // The panel is hidden and re-shown rather than recreated, so Loaded fires only
            // once. Without this the position boxes would keep whatever they read the first
            // time, however far the overlay had been dragged since.
            IsVisibleChanged += (_, args) => { if (args.NewValue is true) RefreshPositionBoxes(); };

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

    private bool _gpuRefreshPending;

    // --- Exact overlay position ------------------------------------------------------
    // Committed on Enter or focus loss rather than bound two-way: a live binding would move
    // the overlay on every keystroke, so typing "120" would drag it to 1, then 12, then 120.

    private bool _updatingPositionBoxes;

    private void OnSettingsViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.OverlayX) or nameof(SettingsViewModel.OverlayY))
            RefreshPositionBoxes();
    }

    private void RefreshPositionBoxes()
    {
        if (_vm == null || PositionXBox == null || PositionYBox == null) return;

        _updatingPositionBoxes = true;
        try
        {
            var (x, y, maxX, maxY, display) = _vm.OverlayPlacement;

            // Not while the user is mid-edit: the overlay repositions itself as tiles resize
            // it, and overwriting a half-typed value would be maddening.
            if (!PositionXBox.IsKeyboardFocusWithin) PositionXBox.Text = x.ToString();
            if (!PositionYBox.IsKeyboardFocusWithin) PositionYBox.Text = y.ToString();

            // Naming the display matters on a multi-monitor setup: "0-2354" means nothing
            // unless you know which screen it is measured across.
            PositionHint.Text = display.Length > 0
                ? $"Pixels from the top-left of {display} (0-{maxX}, 0-{maxY})"
                : $"Pixels from the top-left of the display (0-{maxX}, 0-{maxY})";
        }
        finally
        {
            _updatingPositionBoxes = false;
        }
    }

    /// Digits only, so the box can never hold something that will not parse.
    private void PositionBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsAsciiDigit);

    // --- Drag a position box to slide the overlay ------------------------------------
    // Press and drag on a box to move the overlay live, the way a numeric field scrubs in
    // an editor. The X box follows horizontal movement and the Y box vertical, so the drag
    // matches the direction the overlay travels.
    //
    // A press on an unfocused box starts a scrub rather than placing a caret, which is what
    // makes dragging feel immediate. Releasing without having moved focuses the box for
    // typing instead, so a plain click still does the obvious thing.

    private const double ScrubThreshold = 3;   // px of travel before a press becomes a drag

    private WpfTextBox? _scrubBox;
    private System.Windows.Point _scrubOrigin;
    private int  _scrubStartX, _scrubStartY;
    private bool _scrubbing;

    private void PositionBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not WpfTextBox box || _vm == null) return;
        if (box.IsKeyboardFocusWithin) return;   // already typing in it; leave text editing alone

        _scrubBox    = box;
        _scrubOrigin = e.GetPosition(this);
        _scrubStartX = _vm.OverlayX;
        _scrubStartY = _vm.OverlayY;
        _scrubbing   = false;

        box.CaptureMouse();
        e.Handled = true;   // suppresses the caret placement and text selection
    }

    private void PositionBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_scrubBox == null || _vm == null) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        var offset = e.GetPosition(this) - _scrubOrigin;
        bool isX   = ReferenceEquals(_scrubBox, PositionXBox);
        double travel = isX ? offset.X : offset.Y;

        if (!_scrubbing && Math.Abs(travel) < ScrubThreshold) return;
        _scrubbing = true;

        _vm.MoveOverlayLive(
            isX ? _scrubStartX + (int)travel : _scrubStartX,
            isX ? _scrubStartY : _scrubStartY + (int)travel);
    }

    private void PositionBox_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_scrubBox == null) return;

        var box = _scrubBox;
        _scrubBox = null;
        box.ReleaseMouseCapture();

        if (_scrubbing)
        {
            _scrubbing = false;
            _vm?.CommitOverlayPosition();
        }
        else
        {
            // Never moved, so this was an ordinary click: give it focus and select the value
            // so typing a new one replaces it.
            box.Focus();
            box.SelectAll();
        }

        e.Handled = true;
    }

    private void PositionBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitPositionBoxes();
        e.Handled = true;
    }

    private void PositionBox_Commit(object sender, RoutedEventArgs e) => CommitPositionBoxes();

    private void CommitPositionBoxes()
    {
        if (_vm == null || _updatingPositionBoxes) return;
        if (!int.TryParse(PositionXBox.Text, out int x) || !int.TryParse(PositionYBox.Text, out int y))
        {
            RefreshPositionBoxes();   // put back what it really is
            return;
        }

        _vm.OverlayX = x;
        _vm.OverlayY = y;

        // Reflects any clamping the overlay applied, rather than leaving the box showing a
        // position it could not actually take.
        RefreshPositionBoxes();
    }

    /// BeginInvoke rather than Invoke: this fires from the polling thread, and blocking it
    /// on the UI thread is one half of a deadlock — the UI thread takes the same hardware
    /// lock when the tile selection changes. Nothing here needs to complete synchronously.
    private void OnGpuListChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(PopulateGpuButtons);

    /// Applies any GPU list change that arrived while the dropdown was open.
    private void GpuCombo_DropDownClosed(object? sender, EventArgs e)
    {
        if (!_gpuRefreshPending) return;
        _gpuRefreshPending = false;
        PopulateGpuButtons();
    }

    // ── Tile reordering ────────────────────────────────────────────────────────────
    // Dragging is started from the grip rather than the tile body so that clicking a
    // tile still toggles it. The list order in settings is the overlay order.

    private const string TileDragFormat = "PulseTileId";

    private System.Windows.Point _tileDragStart;
    private string? _pendingDragTileId;

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement grip) return;
        _tileDragStart     = e.GetPosition(null);
        _pendingDragTileId = grip.Tag?.ToString();
        e.Handled = true;   // don't let the click reach the checkbox underneath
    }

    private void Grip_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pendingDragTileId is null) return;

        // Wait for the system drag threshold so a click on the grip isn't treated as a drag.
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _tileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var id = _pendingDragTileId;
        _pendingDragTileId = null;   // guard against re-entering while the drag runs

        DragDrop.DoDragDrop((DependencyObject)sender,
            new WpfDataObject(TileDragFormat, id), WpfDragDropEffects.Move);
    }

    private void Tile_DragOver(object sender, WpfDragEventArgs e)
    {
        bool ours = e.Data.GetDataPresent(TileDragFormat);
        e.Effects = ours ? WpfDragDropEffects.Move : WpfDragDropEffects.None;
        e.Handled = true;

        // Marks the slot the tile will take. The reorder itself happens on drop.
        if (ours && sender is WpfBorder border)
            border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6));
    }

    private void Tile_DragLeave(object sender, WpfDragEventArgs e)
    {
        if (sender is WpfBorder border)
            border.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }

    private void Tile_Drop(object sender, WpfDragEventArgs e)
    {
        if (sender is WpfBorder border)
            border.BorderBrush = System.Windows.Media.Brushes.Transparent;

        if (_vm == null || !e.Data.GetDataPresent(TileDragFormat)) return;

        var draggedId = e.Data.GetData(TileDragFormat) as string;
        var targetId  = (sender as FrameworkElement)?.Tag?.ToString();
        if (string.IsNullOrEmpty(draggedId) || string.IsNullOrEmpty(targetId) || draggedId == targetId) return;

        // Dropping onto a tile takes that tile's position; everything else shuffles along.
        for (int i = 0; i < _vm.AllTiles.Count; i++)
        {
            if (_vm.AllTiles[i].Definition.Id != targetId) continue;
            _vm.MoveTile(draggedId, i);
            break;
        }

        e.Handled = true;
    }

    private void BtnResetTileOrder_Click(object sender, RoutedEventArgs e)
        => _vm?.ResetTileOrder();

    /// <summary>
    /// Stops the mouse wheel changing the GPU selection.
    ///
    /// A WPF ComboBox changes its selected item on scroll even while closed, so simply
    /// scrolling the settings panel with the cursor over this control would silently
    /// repoint every GPU tile at a different adapter. The wheel is forwarded to the
    /// parent instead, so the panel still scrolls normally; the selection can only be
    /// changed by opening the dropdown and picking an entry.
    /// </summary>
    private void GpuCombo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfComboBox combo || combo.IsDropDownOpen) return;

        e.Handled = true;

        if (combo.Parent is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source      = combo,
            });
        }
    }

    private void PopulateGpuButtons()
    {
        if (_vm == null || GpuSourceSection == null) return;

        // Rebuilding the item source while the popup is open forces WPF to tear down and
        // regenerate the open dropdown, which shows up as a freeze right as the user
        // clicks it. GPU enumeration is intermittent, so this fires at awkward moments.
        // Defer until the dropdown closes.
        if (GpuCombo is { IsDropDownOpen: true })
        {
            _gpuRefreshPending = true;
            return;
        }

        _vm.RefreshGpuChoices();

        // Nothing to choose between on a single-GPU machine, so don't add noise. Once a
        // second adapter has been seen the section stays put, because LibreHardwareMonitor
        // stops reporting an iGPU while a game holds the discrete card.
        GpuSourceSection.Visibility = _vm.HasMultipleGpus
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
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
        if (_vm.IsUpdateAvailable) await ConfirmThenInstallAsync();
        else await _vm.CheckForUpdatesAsync(true);
    }

    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
        => await ConfirmThenInstallAsync();

    /// Shows the release notes first so the user knows what they are getting, and only
    /// downloads if they confirm.
    private async Task ConfirmThenInstallAsync()
    {
        if (_vm?.PendingUpdate == null)
        {
            if (_vm != null) await _vm.InstallUpdateAsync();
            return;
        }

        var dialog = new WhatsNewWindow(_vm.PendingUpdate) { Owner = this };
        dialog.ShowDialog();

        if (dialog.Accepted) await _vm.InstallUpdateAsync();
    }

    private void BtnDismissBanner_Click(object sender, RoutedEventArgs e)
        => _vm?.DismissBanner();

    /// <summary>
    /// Writes the log files to the desktop and reveals the result, so someone reporting a
    /// problem has something to attach rather than being asked to reproduce it blind.
    /// </summary>
    private void BtnExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var path = LogService.Export();

        if (path == null)
        {
            DiagnosticsLinkText.Text = "Couldn't save diagnostics";
            return;
        }

        DiagnosticsLinkText.Text = "Saved to your desktop";

        try
        {
            // Selects the file in Explorer rather than opening it, so it is obvious what to
            // attach without a text editor stealing focus.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(MainWindow), "Could not reveal the diagnostics file", ex);
        }
    }

    /// Opens an About-section link in the user's default browser. The URL lives in the
    /// button's Tag so the markup stays the single source of truth for these.
    private void BtnExternalLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn) return;
        var url = btn.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(MainWindow), $"Could not open {url}", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // HardwareService is a singleton and this window is recreated every time the
        // control panel is reopened, so leaving this attached would pin every closed
        // instance in memory for the lifetime of the app.
        Pulse.Services.HardwareService.Instance.GpuListChanged -= OnGpuListChanged;

        // SettingsViewModel is a singleton too, so the position-box subscription pins this
        // window exactly the same way.
        if (_vm != null) _vm.PropertyChanged -= OnSettingsViewModelPropertyChanged;

        base.OnClosed(e);
    }
}
