using System.Collections.ObjectModel;
using System.Windows;
using Pulse.Models;
using Pulse.Services;

using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

using Shortcut = Pulse.Models.Shortcut;

namespace Pulse.ViewModels;

public class TileSelectionItem : BaseViewModel
{
    public SensorTileDefinition Definition { get; }
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) { OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(PausedTag)); } }
    }

    private TileStatus _status;

    /// <summary>
    /// Whether this tile can show a real reading here, decided from facts about the machine.
    /// </summary>
    /// <remarks>
    /// A tile that cannot is not offered as a switch at all. It used to be, and switching it on
    /// put a tile on the overlay that read "--" forever, or in the case of processor power a
    /// confident 0 W. Now it explains itself when clicked and offers whatever would fix it.
    /// </remarks>
    public TileStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PausedTag));
            OnPropertyChanged(nameof(CanBeFixed));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(AccessibleName));
            OnPropertyChanged(nameof(StatusHint));
        }
    }

    public bool IsAvailable => Status == TileStatus.Ready;

    /// Chosen by the user, but not readable right now. Kept chosen, so it comes back by itself
    /// when whatever it needs is back, and left off the overlay until then.
    public bool IsPaused => IsSelected && !IsAvailable;

    /// Said on the chip, because a chosen tile missing from the overlay otherwise looks lost.
    public string PausedTag => IsPaused ? "   PAUSED" : "";

    public bool CanBeFixed => Status is TileStatus.NeedsDriver or TileStatus.DriverNotLoading;

    /// A wrench for something that can be set up, an information mark for hardware that is not
    /// there and only has an explanation to offer. Deliberately not a padlock: Pulse is free, and
    /// a padlock reads as a price. Both are in the Windows 10 icon font as well as 11; a no-entry
    /// sign looked better and could not be confirmed there.
    public string StatusGlyph => CanBeFixed ? "\uE90F" : "\uE946";

    public string StatusHint => Status switch
    {
        TileStatus.NeedsDriver      => "Needs the sensor driver. Click to see how to turn it on.",
        TileStatus.DriverNotLoading => "The sensor driver is installed but not running. Click for details.",
        TileStatus.NotOnThisPc      => "Not available on this PC. Click for details.",
        _                           => Definition.Description ?? "",
    };

    public string AccessibleName => Status switch
    {
        TileStatus.NeedsDriver      => $"{Definition.Label}, needs the sensor driver",
        TileStatus.DriverNotLoading => $"{Definition.Label}, sensor driver not running",
        TileStatus.NotOnThisPc      => $"{Definition.Label}, not available on this PC",
        _                           => Definition.Label,
    };

    /// <summary>
    /// The unit shown on the chip, which for network speed is the user's choice.
    /// </summary>
    /// The chooser shows the same unit the overlay will, so picking a tile here tells you what
    /// you are going to get rather than what the catalogue happens to say.
    public string DisplayUnit =>
        Definition.IsNetworkSpeed
            ? NetworkUnits.Label(SettingsService.Instance.Settings.NetworkUnitBits)
            : Definition.Unit;

    public void RefreshUnit() => OnPropertyChanged(nameof(DisplayUnit));

    public TileSelectionItem(SensorTileDefinition def, bool selected)
    {
        Definition = def;
        _isSelected = selected;
        _status     = SensorDriver.Instance.StatusOf(def.Id);
    }
}

public class SettingsViewModel : BaseViewModel
{
    /// Lazy rather than `??=`: that is not atomic, and these are reached from the polling
    /// thread and the UI thread at the same time during startup. Losing the race builds two
    /// instances, each with its own event subscribers, so notifications reach an object
    /// nobody is listening to.
    private static readonly Lazy<SettingsViewModel> LazyInstance =
        new(() => new SettingsViewModel(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static SettingsViewModel Instance => LazyInstance.Value;

    public ObservableCollection<TileSelectionItem> AllTiles { get; } = new();

    private double _opacity;
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (!Set(ref _opacity, value)) return;

            OnPropertyChanged(nameof(OpacityPercent));
            SettingsService.Instance.Settings.OverlayOpacity = value;

            // Applied straight away so the overlay tracks the slider, but written to disk
            // only once the user stops moving it.
            OverlayViewModel.Instance.OverlayOpacity = value;
            ScheduleSettingsSave();
        }
    }

    /// <summary>
    /// How solid the panel behind the readings is. Separate from Opacity, which fades the
    /// readings too; taking this to zero leaves the values on screen with no panel at all.
    /// </summary>
    private double _backgroundOpacity;
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            if (!Set(ref _backgroundOpacity, value)) return;

            OnPropertyChanged(nameof(BackgroundPercent));
            SettingsService.Instance.Settings.OverlayBackgroundOpacity = value;

            OverlayViewModel.Instance.BackgroundOpacity = value;
            ScheduleSettingsSave();
        }
    }

    /// <summary>
    /// Puts the two appearance sliders back to how the overlay looks out of the box.
    ///
    /// Assigned through the properties rather than the backing fields so the overlay, the
    /// stored settings and the sliders all follow, which a direct field write would skip.
    /// </summary>
    public void ResetAppearance()
    {
        var defaults = new Models.AppSettings();
        Opacity           = defaults.OverlayOpacity;
        BackgroundOpacity = defaults.OverlayBackgroundOpacity;

        // The size as well, and this is the part that matters most.
        //
        // The overlay is resized by dragging its own bottom corner, and nothing else changes
        // it. Its position is clamped to the screen but its size is not, so an overlay scaled
        // up with many tiles enabled can extend past the bottom of the display and take that
        // corner with it. The only control for the size is then off screen, and the way back
        // was editing settings.json by hand. This is the way back.
        OverlayViewModel.Instance.OverlayScale = defaults.OverlayScale;
        SettingsService.Instance.Settings.OverlayScale = defaults.OverlayScale;
        SettingsService.Instance.Save();
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>
    /// Delays saving until a continuous adjustment settles.
    ///
    /// Dragging the opacity slider raised this on every tick, and each one rewrote the whole
    /// settings file *and* notified every settings subscriber — which repositions the
    /// overlay and re-evaluates frame capture. A single drag across the slider was hundreds
    /// of file writes and hundreds of overlay repositions.
    /// </summary>
    private void ScheduleSettingsSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = NewDebounceTimer();
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer!.Stop();
                SettingsService.Instance.Save();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// A debounce timer bound to the interface thread, whoever happens to build it.
    /// </summary>
    /// <remarks>
    /// A DispatcherTimer belongs to the thread that constructs it, and both of the timers
    /// here are built on first use rather than up front. Every path that reaches them today
    /// runs on the interface thread, so this is currently safe by luck rather than by design:
    /// one call from a background thread would attach the timer to a dispatcher with no
    /// message loop, and the tick would simply never arrive. The setting would apply for the
    /// session and be gone at the next launch, with nothing logged, because the save was
    /// waiting on a timer that could not fire.
    ///
    /// FpsService had the same hazard and answered it by making the caller responsible for
    /// where the object is first built. Naming the dispatcher is the better answer where the
    /// constructor allows it: it cannot be got wrong from a distance.
    /// </remarks>
    private static System.Windows.Threading.DispatcherTimer NewDebounceTimer()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher
                      ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        return new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
    }

    /// <summary>
    /// Writes a delayed save immediately. Called when the panel closes and when Pulse exits,
    /// so an adjustment made in the last fraction of a second before quitting is not lost —
    /// which is the obvious way a debounce goes wrong.
    /// </summary>
    public void FlushPendingSave()
    {
        // A position slider moved a moment ago has its anchor recorded in memory but not yet
        // written, so this has to cover that timer too or the last nudge is lost on exit.
        if (_positionSaveTimer is { IsEnabled: true })
        {
            _positionSaveTimer.Stop();
            CommitOverlayPosition();
        }

        if (_saveTimer is not { IsEnabled: true }) return;

        _saveTimer.Stop();
        SettingsService.Instance.Save();
    }

    private double _pollingInterval;
    public double PollingInterval
    {
        get => _pollingInterval;
        set
        {
            if (Set(ref _pollingInterval, value))
            {
                OnPropertyChanged(nameof(Is05s));
                OnPropertyChanged(nameof(Is1s));
                OnPropertyChanged(nameof(Is2s));
                OnPropertyChanged(nameof(Is5s));
                SettingsService.Instance.Settings.PollingIntervalSeconds = value;
                HardwareService.Instance.SetInterval(value);
                SettingsService.Instance.Save();
            }
        }
    }

    // Polling rate "radio" bindings
    public bool Is05s => Math.Abs(_pollingInterval - 0.5) < 0.01;
    public bool Is1s  => Math.Abs(_pollingInterval - 1.0) < 0.01;
    public bool Is2s  => Math.Abs(_pollingInterval - 2.0) < 0.01;
    public bool Is5s  => Math.Abs(_pollingInterval - 5.0) < 0.01;

    private string _overlayPosition;
    public string OverlayPosition
    {
        get => _overlayPosition;
        set
        {
            if (Set(ref _overlayPosition, value))
            {
                SettingsService.Instance.Settings.OverlayPosition = value;
                SettingsService.Instance.Save();
            }
        }
    }

    private bool _isDragEnabled;
    public bool IsDragEnabled
    {
        get => _isDragEnabled;
        set
        {
            if (Set(ref _isDragEnabled, value))
            {
                SettingsService.Instance.Settings.IsDragEnabled = value;
                OverlayViewModel.Instance.IsDragEnabled = value;
                SettingsService.Instance.Save();
            }
        }
    }

    private bool _isCompactMode;
    public bool IsCompactMode
    {
        get => _isCompactMode;
        set
        {
            if (Set(ref _isCompactMode, value))
            {
                SettingsService.Instance.Settings.IsCompactMode = value;
                OverlayViewModel.Instance.IsCompactMode = value;
                SettingsService.Instance.Save();
            }
        }
    }

    // ── Keyboard shortcuts ──────────────────────────────────────────────────────────

    public ObservableCollection<ShortcutRowViewModel> ShortcutRows { get; } = new();

    private bool _shortcutsEnabled;

    /// <summary>
    /// Whether the combinations are registered with Windows.
    /// </summary>
    /// Off until asked for. The rows are filled in either way, so the suggestions are visible
    /// without Pulse having taken anything from anyone.
    public bool ShortcutsEnabled
    {
        get => _shortcutsEnabled;
        set
        {
            if (!Set(ref _shortcutsEnabled, value)) return;

            SettingsService.Instance.Settings.ShortcutsEnabled = value;
            SettingsService.Instance.Save();
            ApplyShortcuts();
        }
    }

    /// <summary>
    /// Assigns a combination to a row, or explains why it cannot be.
    /// </summary>
    /// <returns>Null when it was accepted, otherwise the reason to show.</returns>
    /// <remarks>
    /// Refusing here rather than at registration time is the whole difference between a
    /// shortcut feature that can be trusted and one that cannot. A combination that Windows
    /// will not hand over, or that another row already holds, is knowable the moment it is
    /// pressed; accepting it and failing later teaches people the feature is unreliable.
    /// </remarks>
    public string? AssignShortcut(ShortcutAction action, Shortcut shortcut)
    {
        var rejection = shortcut.Rejection();
        if (rejection != null) return rejection;

        // Two rows cannot hold the same combination: Windows refuses the second registration,
        // so one of them would simply stop working with no obvious cause.
        if (shortcut.IsSet)
        {
            foreach (var other in ShortcutRows)
            {
                if (other.Action == action || other.Binding != shortcut) continue;
                return $"{shortcut} is already set to “{other.Label.ToLowerInvariant()}”.";
            }
        }

        var previous = SettingsService.Instance.Settings.ShortcutFor(action);

        SettingsService.Instance.Settings.SetShortcut(action, shortcut);
        SettingsService.Instance.Save();

        // Every binding change leaves a trace, so a combination that is not what the user last
        // chose can be told apart from one they changed and forgot. A reporter described custom
        // shortcuts going back to the defaults on their own, and without this there is no way
        // to know whether something rewrote them or the save never reached the disk.
        LogService.Info(nameof(SettingsViewModel),
            $"Shortcut for {action} changed from {Describe(previous)} to {Describe(shortcut)} (chosen in the panel).");

        foreach (var row in ShortcutRows)
            if (row.Action == action) row.Binding = shortcut;

        ApplyShortcuts();
        return null;
    }

    private static string Describe(Shortcut shortcut) => shortcut.IsSet ? shortcut.ToString() : "nothing";

    public void ClearShortcut(ShortcutAction action) => AssignShortcut(action, Shortcut.None);

    /// Puts the three suggested combinations back, including on rows that were cleared.
    public void ResetShortcuts()
    {
        var changed = new List<string>();

        foreach (var action in HotkeyService.AllActions)
        {
            var fallback = Shortcut.Default(action);
            var previous = SettingsService.Instance.Settings.ShortcutFor(action);
            if (previous != fallback) changed.Add($"{action} was {Describe(previous)}");

            SettingsService.Instance.Settings.SetShortcut(action, fallback);

            foreach (var row in ShortcutRows)
                if (row.Action == action) row.Binding = fallback;
        }

        SettingsService.Instance.Save();

        // The only route in the product that puts the defaults back, so if a diagnostic shows
        // defaults and no line from here, they were never reset and something else is at fault.
        LogService.Info(nameof(SettingsViewModel),
            changed.Count == 0
                ? "Shortcuts reset to the defaults from the panel; they were already the defaults."
                : $"Shortcuts reset to the defaults from the panel. Replaced: {string.Join(", ", changed)}.");

        ApplyShortcuts();
    }

    /// Re-registers everything and copies any failures onto the rows that caused them.
    public void ApplyShortcuts()
    {
        HotkeyService.Instance.Apply();
        RefreshShortcutProblems();
    }

    public void RefreshShortcutProblems()
    {
        var failures = HotkeyService.Instance.Failures;

        foreach (var row in ShortcutRows)
            row.Problem = failures.TryGetValue(row.Action, out var reason) ? reason : "";
    }

    private void BuildShortcutRows()
    {
        ShortcutRows.Clear();
        foreach (var action in HotkeyService.AllActions)
            ShortcutRows.Add(new ShortcutRowViewModel(action));
    }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value)) return;

            // Snap back if Windows refused. An enabled-looking toggle that does nothing at
            // logon is worse than one that visibly refuses to stay on.
            if (!SettingsService.Instance.UpdateStartWithWindows(value))
                Set(ref _startWithWindows, !value);
        }
    }

    /// <summary>
    /// Re-reads the startup setting from disk, for after something else has corrected it.
    /// </summary>
    /// <remarks>
    /// Reconciliation runs in the background at launch and writes the truth into settings, but
    /// this view model had already cached the old value on the way up. The panel therefore went
    /// on showing what settings.json said before the check, which on the one machine where the
    /// two disagree is precisely the machine whose owner is trying to work out why startup does
    /// not work. Must be called on the interface thread.
    /// </remarks>
    public void RefreshStartWithWindows()
    {
        var actual = SettingsService.Instance.Settings.StartWithWindows;
        if (actual == _startWithWindows) return;

        _startWithWindows = actual;
        OnPropertyChanged(nameof(StartWithWindows));
    }

    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (Set(ref _minimizeToTray, value))
            {
                SettingsService.Instance.Settings.MinimizeToTray = value;
                SettingsService.Instance.Save();
            }
        }
    }

    private int _selectedMonitorIndex;
    public int SelectedMonitorIndex
    {
        get => _selectedMonitorIndex;
        set
        {
            if (Set(ref _selectedMonitorIndex, value))
            {
                // Same reasoning as the corner presets: this replaces the position, so a
                // pending write of the old one must not arrive after it.
                CancelPendingPositionSave();

                var s = SettingsService.Instance.Settings;
                s.SelectedMonitorIndex = value;

                // A saved position belongs to the monitor it was set on, so moving to a
                // different monitor retires it rather than carrying the offsets across.
                s.OverlayCustomX   = -1;
                s.OverlayCustomY   = -1;
                s.OverlayAnchorFx  = -1;
                s.OverlayAnchorFy  = -1;
                s.OverlayMonitorId = "";

                if (s.OverlayPosition == "Custom")
                {
                    s.OverlayPosition = "TopRight";

                    // Keep our own property in step. Updating only the model left the corner
                    // buttons still showing "Custom" selected while the overlay had actually
                    // snapped to the top right.
                    _overlayPosition = "TopRight";
                    OnPropertyChanged(nameof(OverlayPosition));
                }

                SettingsService.Instance.Save();
            }
        }
    }

    // --- Precise overlay placement -------------------------------------------------
    // Pixels here rather than the fraction actually stored, because a pixel is what the
    // sliders move in. Both directions go through the overlay window, which owns the maths
    // and the monitor it belongs to.

    private Views.OverlayWindow? LiveOverlay =>
        (System.Windows.Application.Current as App)?.Overlay;

    public int OverlayX
    {
        get => LiveOverlay?.GetPositionPixels().X ?? 0;
        set => MoveAxis(x: value, y: null);
    }

    public int OverlayY
    {
        get => LiveOverlay?.GetPositionPixels().Y ?? 0;
        set => MoveAxis(x: null, y: value);
    }

    /// <summary>
    /// Moves one axis while its slider is being dragged.
    ///
    /// Nothing is written to disk here. A slider raises this for every pixel of travel, and
    /// persisting each one would mean a settings write per pixel — the same trap the opacity
    /// slider fell into. The final resting place is stored once the drag settles.
    /// </summary>
    private void MoveAxis(int? x, int? y)
    {
        var overlay = LiveOverlay;
        if (overlay == null) return;

        var (curX, curY, _, _) = overlay.GetPositionPixels();
        int newX = x ?? curX;
        int newY = y ?? curY;

        // Guards against the round trip: moving the overlay re-raises OverlayX/OverlayY,
        // which writes back to this setter. Without this the binding would ping-pong.
        if (newX == curX && newY == curY) return;

        // SetPositionPixels records the anchor and reports the move, so no notify here.
        overlay.SetPositionPixels(newX, newY, persist: false);
        SchedulePositionSave();
    }

    private System.Windows.Threading.DispatcherTimer? _positionSaveTimer;

    /// <summary>
    /// True while a position slider's thumb is held, whether or not it is currently moving.
    ///
    /// Set from the panel's drag events. Not a property anything binds to; it exists because
    /// holding a thumb still is indistinguishable from having let go, as far as a debounce
    /// timer can tell.
    /// </summary>
    public bool IsDraggingPositionSlider { get; set; }

    /// <summary>
    /// True while the user is placing the overlay with the position sliders. The overlay uses
    /// this to hold off re-anchoring itself, which would otherwise tug the slider out from
    /// under the user's thumb.
    ///
    /// Two sources, because neither covers the other. The timer means a change arrived
    /// recently, which is what catches the arrow keys and clicks on the track, where there is
    /// no drag at all. The flag means a thumb is being held, which the timer cannot see: it
    /// lapses 400ms after the last movement, so pausing mid-drag to look at where the overlay
    /// had got to was enough to release the guard and let it re-anchor.
    /// </summary>
    public bool IsAdjustingPosition =>
        IsDraggingPositionSlider || _positionSaveTimer is { IsEnabled: true };

    /// Stores the position once a slider stops moving, rather than on every tick.
    private void SchedulePositionSave()
    {
        if (_positionSaveTimer == null)
        {
            _positionSaveTimer = NewDebounceTimer();
            _positionSaveTimer.Tick += (_, _) =>
            {
                _positionSaveTimer!.Stop();
                CommitOverlayPosition();
            };
        }

        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    /// <summary>
    /// Abandons a position that was scheduled to be written and has not been written yet.
    /// </summary>
    /// <remarks>
    /// Moving a slider schedules its write for 400ms later. Choosing a corner, or another
    /// display, is a decision that replaces whatever the sliders were doing, and until now it
    /// did not stop that timer. The timer then fired and committed the slider position, and
    /// committing a position sets it to "Custom", so a preset chosen within 400ms of touching a
    /// slider was silently undone a moment after it was clicked.
    ///
    /// The later, explicit action wins. That is the only ordering a person can predict.
    /// </remarks>
    private void CancelPendingPositionSave()
    {
        _positionSaveTimer?.Stop();
        IsDraggingPositionSlider = false;
    }

    /// Upper bounds for the position sliders: the overlay can never be moved further than its
    /// own size short of the far edge.
    public int OverlayMaxX => LiveOverlay?.GetPositionPixels().MaxX ?? 0;
    public int OverlayMaxY => LiveOverlay?.GetPositionPixels().MaxY ?? 0;

    /// Which display the X and Y figures are measured on, so the numbers are never ambiguous
    /// on a multi-monitor setup.
    public string OverlayDisplayLabel => LiveOverlay?.CurrentDisplayLabel ?? "";

    /// Names the display the sliders are measured on, so the numbers are never ambiguous on a
    /// multi-monitor setup.
    public string PositionHintText =>
        OverlayDisplayLabel.Length > 0
            ? $"Measured from the top-left of {OverlayDisplayLabel}"
            : "Measured from the top-left of the display";

    /// Stores wherever the overlay currently sits, ending a drag.
    public void CommitOverlayPosition()
    {
        var overlay = LiveOverlay;
        if (overlay == null) return;

        var (x, y, _, _) = overlay.GetPositionPixels();
        overlay.SetPositionPixels(x, y);   // persists
    }

    /// <summary>
    /// Re-reads the overlay's position so the sliders follow it, whatever moved it — dragging
    /// the overlay itself, snapping to a corner, or a layout change that re-anchored it.
    /// </summary>
    public void NotifyPositionChanged()
    {
        // One authoritative answer about where the overlay is, which is the settings file the
        // overlay just wrote. This view model used to keep a second opinion and never revisit
        // it, so dragging the overlay left the corner buttons highlighting a preset that had
        // already been replaced by "Custom", and dragging it onto another display left that
        // display's button unselected. Clicking the button it had selected then did nothing at
        // all, because as far as the property was concerned nothing had changed.
        var s = SettingsService.Instance.Settings;

        if (_overlayPosition != s.OverlayPosition)
        {
            _overlayPosition = s.OverlayPosition;
            OnPropertyChanged(nameof(OverlayPosition));
        }

        if (_selectedMonitorIndex != s.SelectedMonitorIndex)
        {
            // The field rather than the property: the setter treats a change as the user
            // asking to move displays and retires the saved position, which is the opposite
            // of following one that has just been saved.
            _selectedMonitorIndex = s.SelectedMonitorIndex;
            OnPropertyChanged(nameof(SelectedMonitorIndex));
        }

        OnPropertyChanged(nameof(OverlayX));
        OnPropertyChanged(nameof(OverlayY));
        OnPropertyChanged(nameof(OverlayMaxX));
        OnPropertyChanged(nameof(OverlayMaxY));
        OnPropertyChanged(nameof(OverlayDisplayLabel));
        OnPropertyChanged(nameof(PositionHintText));
    }

    private bool _showStatusBar;
    public bool ShowStatusBar
    {
        get => _showStatusBar;
        set
        {
            if (Set(ref _showStatusBar, value))
            {
                SettingsService.Instance.Settings.ShowStatusBar = value;
                OverlayViewModel.Instance.ShowStatusBar = value;
                SettingsService.Instance.Save();
            }
        }
    }

    /// Empty string means auto-detect. Setting this repoints every GPU tile at the
    /// chosen adapter on the next poll.
    public string SelectedGpuId
    {
        get => SettingsService.Instance.Settings.SelectedGpuId;
        set
        {
            if (SettingsService.Instance.Settings.SelectedGpuId == value) return;
            SettingsService.Instance.Settings.SelectedGpuId = value;
            SettingsService.Instance.Save();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<GpuInfo> AvailableGpus => HardwareService.Instance.AvailableGpus;

    /// Entries shown in the GPU dropdown, including the leading "Automatic" option.
    public ObservableCollection<GpuChoice> GpuChoices { get; } = new();

    /// <summary>
    /// Which entry the dropdown shows as selected. Read only, on purpose.
    ///
    /// This is display, not choice. When the adapter someone picked is not currently there,
    /// the dropdown has to show what is actually being read instead, and that must not be
    /// written back into their settings — otherwise switching a card off would quietly
    /// convert a deliberate choice of the discrete GPU into a permanent pin to the
    /// integrated one, and the discrete card would never be picked up again when it
    /// returned. A real choice arrives through <see cref="ChooseGpu"/>, which only the
    /// user's own interaction with the dropdown calls.
    /// </summary>
    public GpuChoice? SelectedGpuChoice => ResolveSelected(
        GpuChoices, SelectedGpuId ?? "", HardwareService.Instance.ActiveGpuName);

    /// <summary>
    /// Records a GPU the user actually picked. Called from the dropdown's selection handler,
    /// never from a binding, so nothing here happens as a side effect of merely displaying.
    /// </summary>
    public void ChooseGpu(GpuChoice? choice)
    {
        if (choice == null) return;

        SelectedGpuId = choice.Id;
        OnPropertyChanged(nameof(SelectedGpuChoice));
        OnPropertyChanged(nameof(GpuSourceHint));
    }

    /// <summary>
    /// Decides which entry to show as selected: the adapter the user picked when it is
    /// present, otherwise whichever adapter is genuinely being read right now.
    /// </summary>
    public static GpuChoice? ResolveSelected(
        IReadOnlyList<GpuChoice> choices, string pinned, string activeName)
    {
        pinned ??= "";

        // The user's own choice always wins, when it is actually available.
        foreach (var c in choices)
            if (c.Id == pinned) return c;

        // It is not, so show what the GPU tiles are really reading from.
        foreach (var c in choices)
            if (c.Label == activeName) return c;

        return choices.Count > 0 ? choices[0] : null;
    }

    /// <summary>
    /// Whether the GPU picker is worth showing at all.
    ///
    /// Latches on. Once this machine has been seen to have two adapters it keeps the picker,
    /// even when only one is reported now — because "only one is reported now" is a normal,
    /// temporary state on a laptop: a game holding the discrete card stops the integrated one
    /// being enumerated, and switching a card off in Device Manager removes it until it comes
    /// back. Letting the section vanish at those moments took away the one piece of UI that
    /// says which GPU is being read, at precisely the moment the answer had just changed.
    /// </summary>
    public bool HasMultipleGpus
    {
        get
        {
            if (AvailableGpus.Count > 1) _hasSeenMultipleGpus = true;
            return _hasSeenMultipleGpus;
        }
    }

    private bool _hasSeenMultipleGpus;

    /// <summary>
    /// Rebuilds the dropdown entries from the adapters present right now, and re-resolves
    /// which of them is shown as selected.
    ///
    /// The user's saved choice is deliberately never touched here, whatever the list turns out
    /// to contain. This runs at startup before any hardware has been enumerated, and again
    /// whenever a card comes or goes; clearing a selection that is merely "not found yet" wiped
    /// the choice on every launch. Showing something other than their pick is a display
    /// decision only, made in <see cref="ResolveSelected"/>.
    /// </summary>
    public void RefreshGpuChoices()
    {
        GpuChoices.Clear();
        foreach (var choice in BuildGpuChoices(AvailableGpus))
            GpuChoices.Add(choice);

        // The pinned GPU is deliberately never cleared here.
        //
        // This runs at startup before LibreHardwareMonitor has finished enumerating, when
        // there are no entries at all — so resetting a selection that is merely "not found
        // yet" wiped the user's choice on every single launch. It also fires while a game has
        // the discrete GPU active and the integrated one stops being reported. The sensor host
        // already falls back to automatic when a pinned id does not resolve, so leaving the
        // setting alone costs nothing and the choice survives until the user changes it.

        OnPropertyChanged(nameof(HasMultipleGpus));
        OnPropertyChanged(nameof(GpuSourceHint));
        OnPropertyChanged(nameof(SelectedGpuChoice));
    }

    /// <summary>
    /// Works out what the GPU dropdown should contain. Free of any UI, so the behaviour can be
    /// checked directly.
    ///
    /// Nothing but adapters that are actually present. An earlier version kept an entry for a
    /// pinned card that had been switched off, marked unavailable, on the theory that it
    /// explained itself. In practice the dropdown showed a raw device identifier for hardware
    /// that was not there, while the GPU genuinely being read was not listed at all. What is on
    /// the machine right now is the only honest list.
    /// </summary>
    public static List<GpuChoice> BuildGpuChoices(IReadOnlyList<GpuInfo> available)
    {
        var choices = new List<GpuChoice>();

        // Only when there is genuinely something to choose between. With one adapter left there
        // is nothing for "automatic" to decide, and offering it beside a single GPU reads as two
        // options that do the same thing.
        if (available.Count > 1)
            choices.Add(new GpuChoice { Id = "", Label = "Automatic", Detail = "Picks the discrete GPU" });

        foreach (var gpu in available)
        {
            choices.Add(new GpuChoice
            {
                Id     = gpu.Id,
                Label  = gpu.Name,
                Detail = gpu.IsDiscrete ? "Discrete" : "Integrated",
            });
        }

        return choices;
    }


    /// <summary>
    /// The line under the picker. Says which adapter the GPU tiles are actually reading from
    /// rather than describing the rule in the abstract, because after a card is switched off
    /// the only thing anyone wants to know is which one took over.
    /// </summary>
    public string GpuSourceHint
    {
        get
        {
            var active = HardwareService.Instance.ActiveGpuName;

            if (active.Length == 0)
                return "Auto picks the GPU with dedicated video memory, which is the discrete one on a laptop.";

            // Pinned to something that is not here. Worth saying out loud, because the
            // dropdown is showing a different adapter than the one they chose and the reason
            // is not otherwise visible.
            var pinned = SelectedGpuId ?? "";
            if (pinned.Length > 0 && !AvailableGpus.Any(g => g.Id == pinned))
                return $"Currently reading {active}. The GPU you selected is not available right now, "
                     + "so Pulse is using what is left. Your choice is kept and it goes back "
                     + "as soon as that GPU returns.";

            if (AvailableGpus.Count <= 1)
                return $"Currently reading {active}. It is the only graphics adapter available right now; "
                     + "if another comes back, Pulse picks it up on its own.";

            return $"Currently reading {active}. Auto picks the GPU with dedicated video memory, "
                 + "which is the discrete one on a laptop.";
        }
    }

    private bool _showMaxValues;
    public bool ShowMaxValues
    {
        get => _showMaxValues;
        set
        {
            if (Set(ref _showMaxValues, value))
            {
                SettingsService.Instance.Settings.ShowMaxValues = value;
                SettingsService.Instance.Save();
            }
        }
    }

    private bool _networkUnitBits;
    public bool NetworkUnitBits
    {
        get => _networkUnitBits;
        set
        {
            if (!Set(ref _networkUnitBits, value)) return;

            SettingsService.Instance.Settings.NetworkUnitBits = value;
            SettingsService.Instance.Save();

            // The chooser chips print the unit too, and they are not rebuilt when a setting
            // changes, so they are told. The overlay picks it up on its next reading, which
            // is at most one poll away.
            foreach (var tile in AllTiles) tile.RefreshUnit();
        }
    }

    public int SelectedCount  => AllTiles.Count(t => t.IsSelected);
    public int PausedCount    => AllTiles.Count(t => t.IsPaused);
    public int OpacityPercent => (int)Math.Round(_opacity * 100);
    public int BackgroundPercent => (int)Math.Round(_backgroundOpacity * 100);

    public string AppVersionLabel => UpdateService.CurrentVersionLabel;

    private UpdateInfo? _pendingUpdate;

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate { get => _isCheckingUpdate; private set => Set(ref _isCheckingUpdate, value); }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set { if (Set(ref _isUpdateAvailable, value)) OnPropertyChanged(nameof(ShowUpdateBanner)); }
    }

    private bool _bannerDismissed;
    public bool ShowUpdateBanner => _isUpdateAvailable && !_bannerDismissed && !_isDownloading;

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set { if (Set(ref _isDownloading, value)) OnPropertyChanged(nameof(ShowUpdateBanner)); }
    }

    private int _downloadProgress;
    public int DownloadProgress
    {
        get => _downloadProgress;
        private set { if (Set(ref _downloadProgress, value)) OnPropertyChanged(nameof(DownloadFraction)); }
    }
    public double DownloadFraction => _downloadProgress / 100.0;

    private string _bannerVersion = "";
    public string BannerVersion { get => _bannerVersion; private set => Set(ref _bannerVersion, value); }

    /// <summary>
    /// Asks GitHub for the latest release. Returns whether GitHub answered, which is not the
    /// same as whether there is an update, or null when a check was already running.
    /// </summary>
    public async Task<bool?> CheckForUpdatesAsync(bool manual)
    {
        if (_isCheckingUpdate) return null;

        IsCheckingUpdate = true;
        if (manual) UpdateStatus = "Checking for updates…";

        var (success, info) = await UpdateService.CheckForUpdateAsync();

        IsCheckingUpdate = false;

        if (info != null)
        {
            // Only a newer release brings the banner back. Pulse checks again every day now,
            // and without this a banner somebody dismissed returned every morning for the same
            // version they had already decided about.
            if (_pendingUpdate?.Version != info.Version) _bannerDismissed = false;

            _pendingUpdate     = info;
            BannerVersion      = info.DisplayVersion;
            IsUpdateAvailable  = true;
            UpdateStatus       = $"{info.DisplayVersion} is available";
            OnPropertyChanged(nameof(ShowUpdateBanner));
        }
        else if (success)
        {
            IsUpdateAvailable = false;
            OnPropertyChanged(nameof(ShowUpdateBanner));
            if (manual) UpdateStatus = "You're on the latest version";
        }
        else
        {
            // A check that failed knows nothing, so it changes nothing. It used to clear the
            // update flag as if GitHub had said there was none, which with one check per launch
            // was harmless and with retries would hide an update a later failed attempt had no
            // right to take back.
            if (manual) UpdateStatus = "Couldn't check for updates — try again later";
        }

        return success;
    }

    /// The update currently offered, so the caller can show its release notes.
    /// <summary>
    /// Whether to warn that preferences are not being written to disk.
    ///
    /// Shown rather than only logged, because the symptom is otherwise invisible until the next
    /// launch and looks like Pulse forgetting things at random.
    /// </summary>
    public bool HasSaveWarning => SettingsService.Instance.LastSaveFailed;

    public string SaveWarning =>
        "Your preferences can't be saved to disk, so changes will be lost when Pulse closes. "
      + "Check that there is free space and that Pulse is allowed to write to your AppData folder.";

    public UpdateInfo? PendingUpdate => _pendingUpdate;

    private CancellationTokenSource? _downloadCancel;

    /// <summary>
    /// Stops a download in progress.
    ///
    /// There was previously no way out of one at all. The panel switched into its downloading
    /// state and stayed there, and since the transfer had no overall timeout either, a
    /// connection that quietly died left someone watching a frozen percentage with nothing to
    /// press. Closing Pulse was the only escape.
    /// </summary>
    public void CancelDownload()
    {
        try   { _downloadCancel?.Cancel(); }
        catch
        {
            // Cancelling a source that has already been disposed, which is what a download
            // that finished a moment ago leaves behind. The user asked for it to stop and it
            // has stopped; there is nothing to report.
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (_pendingUpdate == null)
        {
            UpdateService.OpenReleasePage(null);
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        UpdateStatus = "Downloading…";

        var progress = new Progress<int>(p =>
        {
            DownloadProgress = p;
            UpdateStatus = p >= 100 ? "Starting installer…" : $"Downloading… {p}%";
        });

        // Replaced rather than reused, so a cancelled download does not stop the next attempt.
        _downloadCancel?.Dispose();
        _downloadCancel = new CancellationTokenSource();

        UpdateDownloadStatus status;
        try
        {
            status = await UpdateService.DownloadAndRunAsync(_pendingUpdate, progress, _downloadCancel.Token);
        }
        catch (Exception ex)
        {
            // The download reports failure rather than throwing, so this is unexpected. Caught
            // anyway, so that it fails as an update rather than as an application fault: the
            // dispatcher would survive it now, but the user would be left looking at a panel
            // still saying "Downloading" with nothing to explain the stop.
            LogService.Error(nameof(SettingsViewModel), "Installing the update failed", ex);
            status = UpdateDownloadStatus.DownloadFailed;
        }

        switch (status)
        {
            case UpdateDownloadStatus.Success:
                UpdateStatus = "Starting installer…";
                System.Windows.Application.Current.Shutdown();
                break;
            case UpdateDownloadStatus.VerificationFailed:
                IsDownloading = false;
                UpdateStatus = "Update failed verification — download blocked for your safety";
                break;
            case UpdateDownloadStatus.VerificationUnavailable:
                IsDownloading = false;
                UpdateStatus = "Can't verify this update yet — download it manually from the release page";
                break;
            case UpdateDownloadStatus.LocationNotSecurable:
                IsDownloading = false;
                UpdateStatus = "Can't secure the download folder — install manually from the release page";
                break;

            case UpdateDownloadStatus.Stalled:
                IsDownloading = false;
                UpdateStatus = "The download stopped responding — check your connection and try again";
                break;

            case UpdateDownloadStatus.Cancelled:
                IsDownloading = false;
                DownloadProgress = 0;
                UpdateStatus = $"{_pendingUpdate.DisplayVersion} is available";
                break;
            default:
                IsDownloading = false;
                UpdateStatus = "Download failed — click Update Now to retry";
                break;
        }
    }

    public void DismissBanner()
    {
        _bannerDismissed = true;
        OnPropertyChanged(nameof(ShowUpdateBanner));
    }

    // Preview / mirror bindings
    public IEnumerable<TileViewModel> ActiveTileVMs => OverlayViewModel.Instance.ActiveTiles;
    public string   StatusText  => OverlayViewModel.Instance.StatusText;
    public WpfBrush StatusBrush => OverlayViewModel.Instance.StatusBrush;
    public WpfColor StatusColor => OverlayViewModel.Instance.StatusColor;

    private SettingsViewModel()
    {
        var settings  = SettingsService.Instance.Settings;
        _opacity               = settings.OverlayOpacity;
        _backgroundOpacity     = settings.OverlayBackgroundOpacity;
        _pollingInterval       = settings.PollingIntervalSeconds;
        _overlayPosition       = settings.OverlayPosition;
        _startWithWindows      = settings.StartWithWindows;
        _minimizeToTray        = settings.MinimizeToTray;
        _isDragEnabled         = settings.IsDragEnabled;
        _isCompactMode         = settings.IsCompactMode;
        _showStatusBar         = settings.ShowStatusBar;
        _selectedMonitorIndex  = settings.SelectedMonitorIndex;
        _showMaxValues         = settings.ShowMaxValues;
        _networkUnitBits       = settings.NetworkUnitBits;
        _shortcutsEnabled      = settings.ShortcutsEnabled;

        BuildShortcutRows();

        // A combination can be claimed by another program between one launch and the next, so
        // the reason is refreshed whenever registration is attempted, not only when the user
        // changes something.
        HotkeyService.Instance.FailuresChanged += (_, _) => RefreshShortcutProblems();

        HardwareService.Instance.SetInterval(_pollingInterval);

        foreach (var def in OrderedDefinitions(settings.TileOrder))
        {
            var item = new TileSelectionItem(def, settings.ActiveTileIds.Contains(def.Id));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TileSelectionItem.IsSelected)) return;
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(PausedCount));
                ApplyTileSelection();
            };
            AllTiles.Add(item);
        }

        // Only fires when saving starts failing or starts working again, so this costs
        // nothing during normal use.
        SettingsService.Instance.SaveStateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSaveWarning));
            OnPropertyChanged(nameof(SaveWarning));
        };

        HardwareService.Instance.SensorsUpdated += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusColor));
        };

        HardwareService.Instance.HardwareStateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SensorFault));
            OnPropertyChanged(nameof(HasSensorFault));
        };

        SensorDriver.Instance.Changed += (_, _) => RefreshTileStatuses();

        // Write the order back once at startup so the overlay always matches what the
        // settings list shows. Without this, an install upgrading from a version that
        // had no saved order would show the new arrangement in settings while the
        // overlay still rendered the old one until something was toggled.
        ApplyTileSelection();
    }

    /// <summary>
    /// Why sensors are unavailable, or null when they are fine.
    /// </summary>
    /// <remarks>
    /// The overlay's status line says "Sensors unavailable, see About for details" when this is
    /// set. It said that for a long time while the details existed nowhere in the interface:
    /// the reason reached the log and the diagnostics export and no screen at all, so the one
    /// instruction Pulse gives a user whose tiles are empty led nowhere.
    /// </remarks>
    public string? SensorFault => HardwareService.Instance.HardwareFault;

    public bool HasSensorFault => !string.IsNullOrWhiteSpace(SensorFault);

    // ── Tiles this PC cannot read ───────────────────────────────────────────────────

    /// <summary>
    /// The unavailable tile whose explanation is open under the chooser, or null.
    /// </summary>
    /// <remarks>
    /// One panel under the grid rather than a popup on the tile. A popup is a separate window in
    /// WPF: it takes keyboard focus away from the panel it belongs to, screen readers lose their
    /// place, and it can end up drawn over other programs. An inline panel has none of that and
    /// stays where the user was looking.
    /// </remarks>
    private TileSelectionItem? _explainedTile;
    public TileSelectionItem? ExplainedTile
    {
        get => _explainedTile;
        private set
        {
            if (!Set(ref _explainedTile, value)) return;
            _installOutcome = null;
            _installDetail  = "";
            RaiseExplanation();
        }
    }

    public bool IsExplaining => ExplainedTile != null;

    private bool _driverBusy;
    public bool DriverBusy { get => _driverBusy; private set { if (Set(ref _driverBusy, value)) RaiseExplanation(); } }

    private SensorDriver.InstallOutcome? _installOutcome;
    private string _installDetail = "";

    public bool FaceitInstalled => SensorDriver.Instance.FaceitInstalled;

    public string ExplainTitle
    {
        get
        {
            var t = ExplainedTile;
            if (t == null) return "";

            return _installOutcome switch
            {
                SensorDriver.InstallOutcome.Working      => "Driver installed",
                SensorDriver.InstallOutcome.NeedsRestart => "Restart Windows to finish",
                SensorDriver.InstallOutcome.Failed       => "The driver didn't install",
                _ => t.Status switch
                {
                    TileStatus.NeedsDriver      => $"{t.Definition.Label} needs the sensor driver",
                    TileStatus.DriverNotLoading => "The sensor driver isn't running",
                    TileStatus.NotOnThisPc      => $"{t.Definition.Label} isn't available on this PC",
                    _                           => $"{t.Definition.Label} is ready",
                },
            };
        }
    }

    public string ExplainBody
    {
        get
        {
            var t = ExplainedTile;
            if (t == null) return "";

            var label  = t.Definition.Label;
            var others = OtherDriverTiles(t);

            return _installOutcome switch
            {
                SensorDriver.InstallOutcome.Working =>
                    $"{label} is on." + (others.Length > 0 ? $" {others} are ready too, whenever you want them." : ""),

                SensorDriver.InstallOutcome.NeedsRestart =>
                    $"The driver is installed, and Windows will start it after a restart. {label} will turn on by itself then.",

                SensorDriver.InstallOutcome.Failed => _installDetail,

                _ => t.Status switch
                {
                    TileStatus.NeedsDriver =>
                        "Pulse reads this straight from the processor, which needs a small driver called PawnIO. "
                      + "It isn't installed on this PC."
                      + (others.Length > 0 ? $" Installing it also turns on {others}." : "")
                      + (SensorDriver.Instance.CanInstall ? "" : " The driver's installer isn't in Pulse's folder. Reinstalling Pulse puts it back."),

                    TileStatus.DriverNotLoading =>
                        $"PawnIO is installed, but Windows isn't running it, so {label} has nothing to read. Restarting "
                      + "Windows usually fixes this, especially straight after installing. If it keeps happening, "
                      + "something on this PC is stopping it from loading. FACEIT's anti-cheat is known to do this.",

                    TileStatus.NotOnThisPc =>
                        "Windows reports no battery in this PC, so there's nothing to read. On a laptop this tile shows the charge left.",

                    _ => "",
                },
            };
        }
    }

    /// The FACEIT note, shown only while the user is deciding whether to install.
    public bool ShowFaceitNote =>
        ExplainedTile is { Status: TileStatus.NeedsDriver }
        && _installOutcome is null or SensorDriver.InstallOutcome.Failed;

    public string FaceitNote => FaceitInstalled
        ? "FACEIT is installed on this PC. FACEIT's anti-cheat won't start while this driver is installed. "
        + "You can remove the driver any time from Windows Settings, Apps, where it's listed as PawnIO."
        : "FACEIT's anti-cheat won't start while this driver is installed. You can remove it any time "
        + "from Windows Settings, Apps, where it's listed as PawnIO.";

    public bool ShowPrimaryAction =>
        !DriverBusy
        && ExplainedTile is { CanBeFixed: true }
        && SensorDriver.Instance.CanInstall
        && _installOutcome is null or SensorDriver.InstallOutcome.Failed;

    public string PrimaryActionText =>
        _installOutcome == SensorDriver.InstallOutcome.Failed ? "Try again"
      : ExplainedTile?.Status == TileStatus.DriverNotLoading  ? "Reinstall driver"
      : "Install driver";

    public string SecondaryActionText =>
        _installOutcome is SensorDriver.InstallOutcome.Working or SensorDriver.InstallOutcome.NeedsRestart ? "Done"
      : ExplainedTile is { CanBeFixed: true } ? "Not now"
      : "Got it";

    public bool InstallSucceeded => _installOutcome == SensorDriver.InstallOutcome.Working;

    /// <summary>
    /// Whether to offer turning a paused tile off.
    /// </summary>
    /// <remarks>
    /// A paused tile is drawn as a button, not a switch, so without this the one thing the user
    /// could not do was change their mind about it.
    /// </remarks>
    public bool ShowStopShowing => !DriverBusy && ExplainedTile is { IsPaused: true } && _installOutcome is null;

    public void StopShowingExplainedTile()
    {
        if (ExplainedTile is not { } tile || DriverBusy) return;
        tile.IsSelected = false;
        ExplainedTile = null;
    }

    private void RaiseExplanation()
    {
        OnPropertyChanged(nameof(IsExplaining));
        OnPropertyChanged(nameof(ExplainTitle));
        OnPropertyChanged(nameof(ExplainBody));
        OnPropertyChanged(nameof(ShowFaceitNote));
        OnPropertyChanged(nameof(FaceitInstalled));
        OnPropertyChanged(nameof(FaceitNote));
        OnPropertyChanged(nameof(ShowPrimaryAction));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(SecondaryActionText));
        OnPropertyChanged(nameof(InstallSucceeded));
        OnPropertyChanged(nameof(ShowStopShowing));
    }

    /// "CPU Power, CPU Clock and CPU+GPU Power": the rest of what the same install brings,
    /// named so nobody installs it once per tile.
    private string OtherDriverTiles(TileSelectionItem except)
    {
        var names = AllTiles.Where(t => t != except && TileAvailability.DriverTiles.Contains(t.Definition.Id))
                            .Select(t => KeepTogether(t.Definition.Label))
                            .ToList();

        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>
    /// A tile name that wraps as a whole.
    /// </summary>
    /// <remarks>
    /// WPF will break a line at a plus sign, which split "CPU+GPU Power" into "CPU" at the end of
    /// one line and "+GPU Power" at the start of the next. Word joiners either side stop that.
    /// </remarks>
    private static string KeepTogether(string label) => label.Replace("+", "\u2060+\u2060");

    /// Opens the explanation for a tile, or closes it if that tile's is already open.
    public void ExplainTile(TileSelectionItem tile)
    {
        if (DriverBusy) return;   // an install is running; its result belongs to the tile that started it

        // Facts can change while the panel sits in the tray, so they are read again first.
        SensorDriver.Instance.Refresh();

        if (tile.IsAvailable)
        {
            // Fixed since the grid was drawn, for instance by a restart the panel did not see.
            tile.IsSelected = true;
            ExplainedTile = null;
            return;
        }

        ExplainedTile = ReferenceEquals(ExplainedTile, tile) ? null : tile;
    }

    public void CloseExplanation()
    {
        if (DriverBusy) return;
        ExplainedTile = null;
    }

    /// <summary>
    /// Installs the driver for the tile being explained, and turns that tile on.
    /// </summary>
    /// <remarks>
    /// The tile the user clicked is switched on whatever the outcome short of failure: they have
    /// already said they want it. When Windows needs a restart first, it is left chosen and
    /// paused, and comes on by itself once the driver is running.
    /// </remarks>
    public async Task InstallDriverAsync()
    {
        var tile = ExplainedTile;
        if (tile == null || DriverBusy) return;

        DriverBusy = true;
        try
        {
            var (outcome, detail) = await SensorDriver.Instance.InstallAsync();

            _installOutcome = outcome;
            _installDetail  = detail;

            if (outcome != SensorDriver.InstallOutcome.Failed)
                tile.IsSelected = true;

            // A fresh host, so the sensor library looks for the driver again rather than trusting
            // whatever it found when it started.
            if (outcome == SensorDriver.InstallOutcome.Working)
                HardwareService.Instance.RestartHost("the sensor driver was just installed");
        }
        finally
        {
            DriverBusy = false;
            RaiseExplanation();
        }
    }

    /// Applies fresh facts to every tile, and to the overlay, which leaves out tiles that cannot read.
    private void RefreshTileStatuses()
    {
        foreach (var tile in AllTiles)
            tile.Status = SensorDriver.Instance.StatusOf(tile.Definition.Id);

        OnPropertyChanged(nameof(PausedCount));
        RaiseExplanation();
        OverlayViewModel.Instance.LoadActiveTiles();
    }

    /// <summary>
    /// Returns tile definitions in the user's saved order. Anything the saved order
    /// doesn't mention is appended in catalog order, which is what allows a newer
    /// version to introduce tiles (as FPS was) without discarding someone's layout.
    /// </summary>
    private static IEnumerable<SensorTileDefinition> OrderedDefinitions(List<string> savedOrder)
    {
        if (savedOrder.Count == 0) return SensorTileDefinition.All;

        var ordered = new List<SensorTileDefinition>(SensorTileDefinition.All.Count);

        foreach (var id in savedOrder)
        {
            var def = SensorTileDefinition.All.FirstOrDefault(d => d.Id == id);
            if (def != null && !ordered.Contains(def)) ordered.Add(def);
        }

        foreach (var def in SensorTileDefinition.All)
            if (!ordered.Contains(def)) ordered.Add(def);

        return ordered;
    }

    /// <summary>
    /// Moves a tile to a new position. The list order here is the overlay order, so this
    /// is what lets someone group related metrics together.
    /// </summary>
    public void MoveTile(string tileId, int newIndex)
    {
        int oldIndex = -1;
        for (int i = 0; i < AllTiles.Count; i++)
            if (AllTiles[i].Definition.Id == tileId) { oldIndex = i; break; }

        if (oldIndex < 0) return;

        newIndex = Math.Clamp(newIndex, 0, AllTiles.Count - 1);
        if (newIndex == oldIndex) return;

        AllTiles.Move(oldIndex, newIndex);
        ApplyTileSelection();
    }

    /// Puts the tiles back to the shipped default arrangement.
    public void ResetTileOrder()
    {
        var selectedIds = AllTiles.Where(t => t.IsSelected).Select(t => t.Definition.Id).ToHashSet();

        AllTiles.Clear();
        foreach (var def in SensorTileDefinition.All)
        {
            var item = new TileSelectionItem(def, selectedIds.Contains(def.Id));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TileSelectionItem.IsSelected)) return;
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(PausedCount));
                ApplyTileSelection();
            };
            AllTiles.Add(item);
        }

        ApplyTileSelection();
    }

    private void ApplyTileSelection()
    {
        var settings = SettingsService.Instance.Settings;

        // Tiles this build does not have, kept rather than dropped.
        //
        // AppSettings.Sanitise goes out of its way to preserve ids it does not recognise, so
        // that opening an older Pulse does not cost someone the layout they built in a newer
        // one. This method then rebuilt both lists purely from the catalogue and saved, which
        // threw those ids away a moment later and without anybody touching anything: it runs
        // once as the panel is built. The care taken in one file was undone in another.
        //
        // They go at the end, since there is no meaningful place to put a tile that cannot be
        // drawn, and the overlay skips any id it has no definition for.
        var known = new HashSet<string>(AllTiles.Select(t => t.Definition.Id), StringComparer.Ordinal);

        var keptActive = settings.ActiveTileIds.Where(id => !known.Contains(id)).ToList();
        var keptOrder  = settings.TileOrder.Where(id => !known.Contains(id)).ToList();

        // Both lists follow AllTiles, so the order shown in settings is the order the
        // overlay renders.
        settings.ActiveTileIds = AllTiles.Where(t => t.IsSelected)
                                         .Select(t => t.Definition.Id)
                                         .Concat(keptActive)
                                         .ToList();

        settings.TileOrder     = AllTiles.Select(t => t.Definition.Id)
                                         .Concat(keptOrder)
                                         .ToList();

        SettingsService.Instance.Save();
        OverlayViewModel.Instance.LoadActiveTiles();
        OnPropertyChanged(nameof(ActiveTileVMs));
    }

    public void SetPositionPreset(string position)
    {
        // Before anything else: a slider write still waiting on its debounce belongs to the
        // position this replaces, and letting it land would put "Custom" back.
        CancelPendingPositionSave();

        var s = SettingsService.Instance.Settings;
        s.OverlayPosition  = position;
        s.IsDragEnabled    = false;

        // Choosing a corner discards any dragged position, old format and new.
        s.OverlayCustomX   = -1;
        s.OverlayCustomY   = -1;
        s.OverlayAnchorFx  = -1;
        s.OverlayAnchorFy  = -1;
        s.OverlayMonitorId = "";

        _overlayPosition   = position;
        _isDragEnabled     = false;
        OverlayViewModel.Instance.IsDragEnabled = false;
        SettingsService.Instance.Save();
        OnPropertyChanged(nameof(OverlayPosition));
        OnPropertyChanged(nameof(IsDragEnabled));
    }
}
