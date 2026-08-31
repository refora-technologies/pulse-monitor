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
            FitToWorkArea();   // before the clip is measured, so it matches the final size
            RefreshCornerClip();

            // The panel is resizable now, and the clip that rounds its corners is a fixed
            // rectangle — left alone it would keep the old dimensions and either crop the
            // content or leave square corners showing.
            WindowBorder.SizeChanged += (_, _) => RefreshCornerClip();

            HighlightActivePollingRate();
            HighlightActivePosition();
            UpdateOverlayButton();
            PopulateMonitorButtons();
            PopulateGpuButtons();

            // The panel is hidden and re-shown rather than recreated, so Loaded fires only
            // once. Without this the position sliders would keep whatever they read the first
            // time, however far the overlay had been dragged since.
            IsVisibleChanged += (_, args) => { if (args.NewValue is true) _vm?.NotifyPositionChanged(); };

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

    private void RefreshCornerClip()
    {
        if (WindowBorder.ActualWidth <= 0 || WindowBorder.ActualHeight <= 0) return;

        WindowBorder.Clip = new RectangleGeometry(
            new System.Windows.Rect(0, 0, WindowBorder.ActualWidth, WindowBorder.ActualHeight),
            18, 18);
    }

    /// <summary>
    /// Shrinks the panel to fit the screen it opens on.
    ///
    /// The design size is 520x740 device-independent units, which is 1110 physical pixels
    /// tall at 150% scaling and 1480 at 200%. On a 1080p laptop at those settings the window
    /// was taller than the desktop, and because it is borderless with no resize there was no
    /// way to drag it smaller or reach what had fallen off the bottom. The content already
    /// scrolls, so shrinking costs nothing.
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi  = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0) return;

            // Work area is in physical pixels; the window's size and position are not.
            double availableWidth  = work.Width  / dpi.DpiScaleX;
            double availableHeight = work.Height / dpi.DpiScaleY;

            const double margin = 24;
            double width  = Math.Max(MinWidth,  Math.Min(Width,  availableWidth  - margin));
            double height = Math.Max(MinHeight, Math.Min(Height, availableHeight - margin));

            if (Math.Abs(width - Width) < 1 && Math.Abs(height - Height) < 1) return;

            Width  = width;
            Height = height;

            // Re-centre, since the window was placed for its original size.
            Left = work.Left / dpi.DpiScaleX + (availableWidth  - width)  / 2;
            Top  = work.Top  / dpi.DpiScaleY + (availableHeight - height) / 2;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(MainWindow), "Could not fit the panel to the screen", ex);
        }
    }

    /// Moving the panel to a differently scaled monitor changes its physical size, so the
    /// fit has to be reconsidered.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(FitToWorkArea, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => CloseOrHide();

    /// <summary>
    /// Honours "minimize to tray" for every way of closing this window, not just the ✕.
    ///
    /// Only the custom close button consulted the setting, so Alt+F4 — and the taskbar's
    /// close item, and the system menu — quit Pulse outright even with the preference on.
    /// A preference that only some paths respect is worse than not having one.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A slider adjustment made moments ago may still be waiting on its debounce.
        _vm?.FlushPendingSave();

        // Never block the real exit: Shutdown closes windows through this same path, so
        // cancelling here would make "Exit Pulse" do nothing at all.
        if (!App.IsExiting && _vm?.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void CloseOrHide()
    {
        if (_vm?.MinimizeToTray == true) Hide();
        else WpfApplication.Current.Shutdown();
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
    // Two sliders bound straight to the view model. This used to be a pair of text boxes,
    // which needed a surprising amount of scaffolding to be safe: tracking whether a box had
    // really been typed in, committing on focus loss, filtering pasted text, clearing a stuck
    // mouse grab. A slider cannot hold a value that disagrees with the overlay, so all of it
    // went away along with the bugs it kept producing.

    /// Puts the opacity and background sliders back to the values Pulse ships with, so a
    /// transparent panel can be tried out without having to remember what it was before.
    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => _vm?.ResetAppearance();


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

    /// <summary>
    /// Moves a tile with Alt and an arrow key.
    ///
    /// Reordering was drag-only, which left it unreachable for anyone using the keyboard —
    /// and awkward for anyone who simply finds dragging fiddly. Alt is the modifier because
    /// the arrows alone move focus between tiles, and Space still toggles them.
    ///
    /// The list is laid out two per row, so Left/Right step by one and Up/Down step by two,
    /// which matches what the user sees rather than the underlying index.
    /// </summary>
    private void Tile_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.KeyboardDevice.Modifiers != System.Windows.Input.ModifierKeys.Alt) return;
        if (sender is not WpfBorder border || border.Tag is not string tileId) return;

        const int columns = 2;

        // With Alt held, WPF reports Key.System and puts the real key in SystemKey. Reading
        // e.Key alone matched nothing, which is why the shortcut did nothing at all.
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        int delta = key switch
        {
            System.Windows.Input.Key.Left  => -1,
            System.Windows.Input.Key.Right => +1,
            System.Windows.Input.Key.Up    => -columns,
            System.Windows.Input.Key.Down  => +columns,
            _ => 0,
        };

        if (delta == 0) return;

        int index = -1;
        for (int i = 0; i < _vm.AllTiles.Count; i++)
            if (_vm.AllTiles[i].Definition.Id == tileId) { index = i; break; }

        if (index < 0) return;

        int target = index + delta;

        // Refused rather than clamped. MoveTile clamps into range, so Alt+Up on the second
        // tile asked for index -1, got 0, and slid the tile sideways instead of doing
        // nothing — a move in a direction the user did not press.
        if (target < 0 || target >= _vm.AllTiles.Count)
        {
            e.Handled = true;   // still swallow it, so focus does not jump away instead
            return;
        }

        _vm.MoveTile(tileId, target);
        e.Handled = true;

        // The panel rebuilds its items, so focus has to be put back on the tile that moved
        // or the user loses their place after every keystroke.
        Dispatcher.InvokeAsync(() => FocusTile(tileId), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FocusTile(string tileId)
    {
        foreach (var border in FindVisualChildren<WpfBorder>(this))
        {
            if (border.Tag as string != tileId || !border.AllowDrop) continue;

            foreach (var box in FindVisualChildren<System.Windows.Controls.CheckBox>(border))
            {
                box.Focus();
                return;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;

            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

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

        // Nothing to choose between on a machine that only ever had one GPU, so don't add
        // noise. On anything hybrid the section stays put once seen — LibreHardwareMonitor
        // stops reporting an iGPU while a game holds the discrete card, and a card switched
        // off in Device Manager disappears until it comes back. Hiding the picker at those
        // moments removed the one thing on screen that says which GPU is being read, right
        // when that answer had just changed.
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
            DiagnosticsLinkText.Text = "Couldn't Save Diagnostics";
            DiagnosticsHint.Text     = "The file could not be written. Check that your desktop folder is writable.";
            return;
        }

        DiagnosticsLinkText.Text = "Saved to Desktop";
        DiagnosticsHint.Text     = $"Saved as {System.IO.Path.GetFileName(path)}. Attach this file to a bug report.";

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

        base.OnClosed(e);
    }
}
