using System.Collections.ObjectModel;
using System.Windows;
using Pulse.Models;
using Pulse.Services;

using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace Pulse.ViewModels;

public class TileSelectionItem : BaseViewModel
{
    public SensorTileDefinition Definition { get; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public TileSelectionItem(SensorTileDefinition def, bool selected)
    {
        Definition = def;
        _isSelected = selected;
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
            _saveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
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
    /// True while a position slider is still being moved, i.e. changes are arriving and the
    /// resting place has not been written yet. The overlay uses this to hold off re-anchoring
    /// itself, which would otherwise tug the slider out from under the user's thumb.
    /// </summary>
    public bool IsAdjustingPosition => _positionSaveTimer is { IsEnabled: true };

    /// Stores the position once a slider stops moving, rather than on every tick.
    private void SchedulePositionSave()
    {
        if (_positionSaveTimer == null)
        {
            _positionSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _positionSaveTimer.Tick += (_, _) =>
            {
                _positionSaveTimer!.Stop();
                CommitOverlayPosition();
            };
        }

        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
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

    public int SelectedCount  => AllTiles.Count(t => t.IsSelected);
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

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_isCheckingUpdate) return;

        IsCheckingUpdate = true;
        if (manual) UpdateStatus = "Checking for updates…";

        var (success, info) = await UpdateService.CheckForUpdateAsync();

        IsCheckingUpdate = false;

        if (info != null)
        {
            _pendingUpdate     = info;
            _bannerDismissed   = false;
            BannerVersion      = info.DisplayVersion;
            IsUpdateAvailable  = true;
            UpdateStatus       = $"{info.DisplayVersion} is available";
        }
        else
        {
            IsUpdateAvailable = false;
            OnPropertyChanged(nameof(ShowUpdateBanner));
            if (manual) UpdateStatus = success ? "You're on the latest version" : "Couldn't check for updates — try again later";
        }
    }

    /// The update currently offered, so the caller can show its release notes.
    public UpdateInfo? PendingUpdate => _pendingUpdate;

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

        var status = await UpdateService.DownloadAndRunAsync(_pendingUpdate, progress);
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

        HardwareService.Instance.SetInterval(_pollingInterval);

        foreach (var def in OrderedDefinitions(settings.TileOrder))
        {
            var item = new TileSelectionItem(def, settings.ActiveTileIds.Contains(def.Id));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TileSelectionItem.IsSelected)) return;
                OnPropertyChanged(nameof(SelectedCount));
                ApplyTileSelection();
            };
            AllTiles.Add(item);
        }

        HardwareService.Instance.SensorsUpdated += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusColor));
        };

        // Write the order back once at startup so the overlay always matches what the
        // settings list shows. Without this, an install upgrading from a version that
        // had no saved order would show the new arrangement in settings while the
        // overlay still rendered the old one until something was toggled.
        ApplyTileSelection();
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
                ApplyTileSelection();
            };
            AllTiles.Add(item);
        }

        ApplyTileSelection();
    }

    private void ApplyTileSelection()
    {
        var settings = SettingsService.Instance.Settings;

        // Both lists follow AllTiles, so the order shown in settings is the order the
        // overlay renders.
        settings.ActiveTileIds = AllTiles.Where(t => t.IsSelected).Select(t => t.Definition.Id).ToList();
        settings.TileOrder     = AllTiles.Select(t => t.Definition.Id).ToList();

        SettingsService.Instance.Save();
        OverlayViewModel.Instance.LoadActiveTiles();
        OnPropertyChanged(nameof(ActiveTileVMs));
    }

    public void SetPositionPreset(string position)
    {
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
