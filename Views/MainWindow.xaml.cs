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
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using Shortcut = Pulse.Models.Shortcut;
using ShortcutAction = Pulse.Models.ShortcutAction;

namespace Pulse.Views;

public partial class MainWindow : Window
{
    private SettingsViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // Not in the markup: a decode that fails there is a XamlParseException, and this
        // window is built during startup. See App.ApplyIcon.
        App.ApplyIcon(this);

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
            HighlightActiveNetworkUnit();
            HighlightActivePosition();
            UpdateOverlayButton();
            PopulateMonitorButtons();
            PopulateGpuButtons();

            // The panel is hidden and re-shown rather than recreated, so Loaded fires only
            // once. Without this the position sliders would keep whatever they read the first
            // time, however far the overlay had been dragged since, and the display buttons
            // would still list monitors that were unplugged while the panel was away.
            //
            // The overlay button belongs here for the same reason. It was refreshed only on
            // Loaded and by App's show/hide, so any other route to the overlay disappearing
            // left it reading "Hide Overlay" over an empty screen.
            IsVisibleChanged += (_, args) =>
            {
                if (args.NewValue is not true) return;
                _vm?.NotifyPositionChanged();
                PopulateMonitorButtons();
                UpdateOverlayButton();

                // The driver can be installed, removed or blocked while the panel is away, and
                // the tiles should say what is true now rather than what was true at startup.
                SensorDriver.Instance.Refresh();
            };

            // The corner and display buttons are painted in code rather than bound, so they
            // need telling when something other than a click moves the overlay. Dragging it
            // writes a new position and a new display straight into settings, and without this
            // the panel went on highlighting the corner and the screen it used to be on.
            if (_vm != null)
            {
                _vm.PropertyChanged += (_, args) =>
                {
                    switch (args.PropertyName)
                    {
                        case nameof(_vm.OverlayPosition):     HighlightActivePosition(); break;
                        case nameof(_vm.SelectedMonitorIndex): HighlightActiveMonitor(); break;
                    }
                };
            }

            // Displays can also change while the panel is open, which is the case someone is
            // most likely to be looking at: plugging a monitor in and going straight to Pulse
            // to move the overlay onto it.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // The GPU list isn't known until the first sensor poll completes, so rebuild
            // the picker when it arrives (and if an eGPU is plugged in later).
            Pulse.Services.HardwareService.Instance.GpuListChanged += OnGpuListChanged;

            PollRatePanel.SizeChanged += (_, _) => UpdateSegIndicator(false);
            NetUnitPanel.SizeChanged  += (_, _) => UpdateNetUnitIndicator(false);
            Dispatcher.InvokeAsync(() => UpdateNetUnitIndicator(false),
                System.Windows.Threading.DispatcherPriority.Loaded);
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

            // The minimum gives way when the screen is smaller than it.
            //
            // MinWidth and MinHeight are 420, which is a sensible floor for a usable panel and
            // not a fact about anybody's display. Clamping to it meant that on a small screen at
            // a high scaling factor the window stayed larger than the desktop, and since it is
            // borderless with no resize there was no way to drag it back or reach what had
            // fallen off the edge. A cramped panel that can be scrolled beats a panel with its
            // buttons off screen. The absolute floor is there so this can never collapse the
            // window to nothing if a display reports something absurd.
            const double floor = 280;

            double roomWidth  = Math.Max(floor, availableWidth  - margin);
            double roomHeight = Math.Max(floor, availableHeight - margin);

            MinWidth  = Math.Min(MinWidth,  roomWidth);
            MinHeight = Math.Min(MinHeight, roomHeight);

            double width  = Math.Max(MinWidth,  Math.Min(Width,  roomWidth));
            double height = Math.Max(MinHeight, Math.Min(Height, roomHeight));

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
        if (!App.IsExiting)
        {
            if (_vm?.MinimizeToTray == true)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // Tray minimising is off, so closing this window means closing Pulse, which is
            // what the ✕ has always done. Alt+F4, the taskbar's close item and the system menu
            // used to close the window and leave Pulse running: the overlay is a window too,
            // so WPF never reached its last one and never exited. What the user got was an
            // application with no way back to its settings except a keyboard shortcut.
            //
            // Posted rather than called, so this close finishes being cancelled before the
            // exit starts closing the same window again.
            e.Cancel = true;
            if (WpfApplication.Current is App app)
                Dispatcher.BeginInvoke(new Action(app.RequestExit));
            return;
        }

        base.OnClosing(e);
    }

    private void CloseOrHide()
    {
        if (_vm?.MinimizeToTray == true) Hide();
        else if (WpfApplication.Current is App app) app.RequestExit();
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

    private void NetworkUnit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || _vm == null) return;

        _vm.NetworkUnitBits = (btn.Tag as string) == "bits";
        HighlightActiveNetworkUnit();
    }

    private void HighlightActiveNetworkUnit()
    {
        if (_vm == null || NetUnitPanel == null) return;

        var activeStyle = (WpfStyle)FindResource("SegBtnActive");
        var normalStyle = (WpfStyle)FindResource("SegBtn");

        foreach (var child in NetUnitPanel.Children)
        {
            if (child is not WpfButton btn) continue;
            bool isActive = ((btn.Tag as string) == "bits") == _vm.NetworkUnitBits;
            btn.Style = isActive ? activeStyle : normalStyle;
        }

        UpdateNetUnitIndicator(true);
    }

    private void UpdateNetUnitIndicator(bool animate)
    {
        if (_vm == null || NetUnitIndicator == null || NetUnitPanel == null) return;
        if (NetUnitPanel.ActualWidth <= 0) return;

        double segW = NetUnitPanel.ActualWidth / 2.0;
        NetUnitIndicator.Width = segW;

        double targetX = _vm.NetworkUnitBits ? segW : 0;

        if (animate)
        {
            var anim = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            NetUnitIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            NetUnitIndicatorTranslate.X = targetX;
        }
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

    /// <summary>
    /// Rebuilds the display buttons when Windows reports a change.
    /// </summary>
    /// Marshalled onto the UI thread: SystemEvents raises this from its own hidden window,
    /// which is not necessarily ours, and this touches bound children.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(PopulateMonitorButtons);

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

    /// <summary>
    /// Reports that a position slider's thumb is being held, and released.
    /// </summary>
    /// <remarks>
    /// Whether the user is still placing the overlay used to be inferred from the save
    /// debounce: a change had arrived within the last 400ms, so a drag must be in progress.
    /// That is true while the thumb keeps moving and wrong the moment it stops. Pausing
    /// mid-drag to look at the result let the timer lapse, and the overlay was then free to
    /// re-anchor itself out from under a thumb that was still held down.
    ///
    /// The debounce is still what covers the arrow keys and clicks on the track, where there
    /// is no drag to report. This only adds the part it could never know.
    /// </remarks>
    private void PositionSlider_DragStarted(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.IsDraggingPositionSlider = true;
    }

    private void PositionSlider_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.IsDraggingPositionSlider = false;
    }

    // ── Keyboard shortcuts ──────────────────────────────────────────────────────────
    //
    // "Press a key combination" sounds trivial and is where this kind of control usually goes
    // wrong. The awkward parts, all of which are handled below: Alt makes WPF report the key as
    // System with the real one somewhere else; our own shortcuts are registered globally and
    // fire before the panel sees the keystroke; and a combination has to be refused at the
    // moment it is pressed rather than accepted and then quietly failing to register.

    private ShortcutRowViewModel? _capturing;

    private void ShortcutChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ShortcutAction action }) return;

        var row = _vm?.ShortcutRows.FirstOrDefault(r => r.Action == action);
        if (row == null) return;

        BeginCapture(row);
        Keyboard.Focus((WpfButton)sender);
    }

    private void BeginCapture(ShortcutRowViewModel row)
    {
        if (_capturing == row) return;
        EndCapture();

        _capturing = row;
        row.IsCapturing = true;
        row.Problem = "";

        // Ours are global, so Windows delivers them to Pulse before the focused control sees
        // the keys at all. Left registered, pressing the combination a row already holds would
        // carry out the action instead of being recorded: asking to change the overlay
        // shortcut would hide the overlay.
        HotkeyService.Instance.Suspend();
    }

    private void EndCapture()
    {
        if (_capturing == null) return;

        _capturing.IsCapturing = false;
        _capturing = null;

        HotkeyService.Instance.Resume();
        _vm?.RefreshShortcutProblems();
    }

    private void ShortcutChip_LostFocus(object sender, RoutedEventArgs e) => EndCapture();

    private void ShortcutChip_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_capturing == null) return;
        e.Handled = true;

        // Alt is held for every one of our suggested combinations, and WPF reports Alt+letter
        // as Key.System with the real key in SystemKey. Reading Key alone here would record
        // every Alt combination as the same thing.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        // Modifiers arrive as key presses of their own on the way to the combination. Waiting
        // rather than rejecting: the user is part-way through pressing something valid.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var candidate = new Shortcut(Keyboard.Modifiers, key);
        var row = _capturing;

        var problem = _vm?.AssignShortcut(row.Action, candidate);

        // Refused, so the previous binding is untouched and the reason is shown against the
        // row. Capture ends either way: leaving it open after a refusal makes it unclear
        // whether anything was recorded.
        EndCapture();
        if (problem != null) row.Problem = problem;
    }

    private void ShortcutClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ShortcutAction action }) return;

        // Each row is cleared on its own; the others keep working.
        _vm?.ClearShortcut(action);
    }

    private void ShortcutReset_Click(object sender, RoutedEventArgs e) => _vm?.ResetShortcuts();

    /// Puts the opacity and background sliders back to the values Pulse ships with, so a
    /// transparent panel can be tried out without having to remember what it was before.
    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => _vm?.ResetAppearance();


    /// BeginInvoke rather than Invoke: this fires from the polling thread, and blocking it
    /// on the UI thread is one half of a deadlock — the UI thread takes the same hardware
    /// lock when the tile selection changes. Nothing here needs to complete synchronously.
    private void OnGpuListChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(PopulateGpuButtons);

    /// Guards against a second export starting while one is still gathering.
    private bool _exportingDiagnostics;

    /// True while the entries are being replaced, so the resulting selection changes are
    /// recognised as ours rather than the user's.
    private bool _rebuildingGpuList;

    /// <summary>
    /// Records a GPU the user picked from the dropdown.
    ///
    /// Selection is handled here rather than by a two way binding because the entry shown as
    /// selected is not always the user's choice: when their GPU has been switched off, the
    /// dropdown shows whichever adapter is actually being read. A binding would write that
    /// back and quietly make it permanent.
    /// </summary>
    private void GpuCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_rebuildingGpuList || _vm == null) return;
        if (GpuCombo?.SelectedItem is not Pulse.Models.GpuChoice choice) return;

        _vm.ChooseGpu(choice);
    }

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

    /// The tile whose explanation is open, so Escape can put focus back where it came from.
    private WpfButton? _explainedTileButton;

    private void UnavailableTile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not WpfButton { DataContext: TileSelectionItem tile } button) return;

        _vm.ExplainTile(tile);

        if (!_vm.IsExplaining) { _explainedTileButton = null; return; }

        _explainedTileButton = button;

        // Into the panel, so a keyboard user lands on the choice rather than having to find it.
        // After layout, because the panel has only just become visible.
        Dispatcher.InvokeAsync(() =>
        {
            if (BtnExplainPrimary.IsVisible) BtnExplainPrimary.Focus();
            else                             BtnExplainSecondary.Focus();
            TileExplanation.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void BtnExplainPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        try
        {
            await _vm.InstallDriverAsync();
        }
        catch (Exception ex)
        {
            // async void: anything escaping here would take Pulse down.
            LogService.Error(nameof(MainWindow), "Installing the sensor driver from the tile chooser failed", ex);
        }

        // Wherever the result left the buttons, focus goes to one that is still there.
        if (BtnExplainPrimary.IsVisible) BtnExplainPrimary.Focus();
        else                             BtnExplainSecondary.Focus();
    }

    private void BtnExplainSecondary_Click(object sender, RoutedEventArgs e) => CloseTileExplanation();

    private void BtnExplainStopShowing_Click(object sender, RoutedEventArgs e)
    {
        _vm?.StopShowingExplainedTile();
        if (_explainedTileButton is { IsVisible: true } b) b.Focus();
        _explainedTileButton = null;
    }

    private void TileExplanation_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseTileExplanation();
    }

    private void CloseTileExplanation()
    {
        if (_vm == null || _vm.DriverBusy) return;

        _vm.CloseExplanation();

        // Back to the tile, if it is still a button. After a successful install it has turned
        // into a switch and the button is hidden, so focus goes nowhere rather than somewhere odd.
        if (_explainedTileButton is { IsVisible: true } b) b.Focus();
        _explainedTileButton = null;
    }

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

        // Rebuilding the list makes the ComboBox raise SelectionChanged as items come and go.
        // None of that is the user choosing anything, so it must not reach their settings.
        _rebuildingGpuList = true;
        try   { _vm.RefreshGpuChoices(); }
        finally { _rebuildingGpuList = false; }

        // The section is always shown, because it answers "which GPU are these readings from",
        // and that question does not stop mattering on a machine with one graphics adapter.
        // It used to be hidden there, which left a Ryzen laptop with no way to see what Pulse
        // was reading at all.
        //
        // The dropdown is what goes away, since there is genuinely nothing to choose between.
        // The line underneath still names the adapter and says it is the only one available.
        //
        // On anything hybrid the dropdown stays put once seen: LibreHardwareMonitor stops
        // reporting an iGPU while a game holds the discrete card, and a card switched off in
        // Device Manager disappears until it comes back. Hiding it at those moments removed
        // the one control on screen that says which GPU is being read, right when that answer
        // had just changed.
        GpuSourceSection.Visibility = System.Windows.Visibility.Visible;

        // Three states, not two. Nothing is known about the graphics until the sensor host has
        // started and enumerated them, which is a few seconds after the panel opens, and during
        // those seconds this card used to be empty and then suddenly contained a dropdown.
        bool known     = _vm.AvailableGpus.Count > 0;
        bool choosable = known && _vm.HasMultipleGpus;

        Show(GpuPending, !known);
        Show(GpuCombo,   choosable);
        Show(GpuSingle,  known && !choosable);

        static void Show(System.Windows.UIElement? element, bool visible)
        {
            if (element != null)
                element.Visibility = visible ? System.Windows.Visibility.Visible
                                             : System.Windows.Visibility.Collapsed;
        }
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || _vm == null) return;
        _vm.SelectedMonitorIndex = (int)btn.Tag;

        HighlightActiveMonitor();
        HighlightActivePosition();
    }

    /// Which display button is shown as chosen. Its own method because the overlay being
    /// dragged onto another screen has to repaint these too, not only a click here.
    private void HighlightActiveMonitor()
    {
        if (_vm == null || MonitorPanel == null) return;

        var activeStyle = (WpfStyle)FindResource("MonitorBtnActive");
        var normalStyle = (WpfStyle)FindResource("MonitorBtn");

        foreach (var child in MonitorPanel.Children)
        {
            if (child is WpfButton b)
                b.Style = (int)b.Tag == _vm.SelectedMonitorIndex ? activeStyle : normalStyle;
        }
    }

    // Both of these are async void, so an exception escaping one goes straight to the
    // dispatcher rather than to any caller. Pulse now survives that, but a button that fails
    // should still fail as a button rather than as an application-wide fault, and the log
    // entry says which button it was.
    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.IsCheckingUpdate || _vm.IsDownloading) return;

        try
        {
            // Once a check has already found an update, this button switches to actually
            // starting that download instead of redundantly re-checking GitHub again.
            if (_vm.IsUpdateAvailable) await ConfirmThenInstallAsync();
            else await _vm.CheckForUpdatesAsync(true);
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(MainWindow), "Checking for updates failed", ex);
        }
    }

    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try { await ConfirmThenInstallAsync(); }
        catch (Exception ex)
        {
            LogService.Error(nameof(MainWindow), "Starting the update failed", ex);
        }
    }

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

    /// Stops a download in progress. The status line goes back to offering the update, so
    /// cancelling is not the same as dismissing it.
    private void BtnCancelDownload_Click(object sender, RoutedEventArgs e) => _vm?.CancelDownload();

    private void BtnDismissBanner_Click(object sender, RoutedEventArgs e)
        => _vm?.DismissBanner();

    /// <summary>
    /// Writes the log files to the desktop and reveals the result, so someone reporting a
    /// problem has something to attach rather than being asked to reproduce it blind.
    /// </summary>
    /// <remarks>
    /// Off the UI thread, because gathering this is not as cheap as it looks: it shells out to
    /// schtasks, asks DXGI about the graphics adapters, and reads every log file we still hold.
    /// Run inline, a slow or stuck Task Scheduler froze the whole control panel, and it froze
    /// the one button someone presses when something is already wrong.
    /// </remarks>
    private async void BtnExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_exportingDiagnostics) return;
        _exportingDiagnostics = true;

        var button = sender as WpfButton;
        if (button != null) button.IsEnabled = false;

        DiagnosticsLinkText.Text = "Saving…";
        DiagnosticsHint.Text     = "Collecting logs and hardware details.";

        string? path;
        try
        {
            path = await Task.Run(LogService.Export);
        }
        catch (Exception ex)
        {
            // Export catches its own failures and returns null, so reaching here means
            // something unexpected. Swallowed rather than left to the dispatcher, which would
            // take Pulse down over a diagnostics file.
            LogService.Error(nameof(MainWindow), "Exporting diagnostics failed", ex);
            path = null;
        }
        finally
        {
            _exportingDiagnostics = false;
            if (button != null) button.IsEnabled = true;
        }

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
                FileName        = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
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
        // HardwareService is a singleton that outlives this window. The panel is hidden
        // rather than closed in normal use, so this runs when Pulse exits, but detaching is
        // still right: a window left attached would be kept alive by the singleton.
        Pulse.Services.HardwareService.Instance.GpuListChanged -= OnGpuListChanged;

        // SystemEvents is static and lives as long as the process, so this one leaks harder
        // than the singleton above: every panel ever opened would stay alive, and each would
        // still try to rebuild buttons on a window that has been closed.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        base.OnClosed(e);
    }
}
