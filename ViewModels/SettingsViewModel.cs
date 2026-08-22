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
    // Pixels here rather than the fraction actually stored, because a pixel is what people
    // expect to type. Both directions go through the overlay window, which owns the maths
    // and the monitor it belongs to.

    private Views.OverlayWindow? LiveOverlay =>
        (System.Windows.Application.Current as App)?.Overlay;

    public int OverlayX
    {
        get => LiveOverlay?.GetPositionPixels().X ?? 0;
        set
        {
            var overlay = LiveOverlay;
            if (overlay == null) return;
            overlay.SetPositionPixels(value, overlay.GetPositionPixels().Y);
            NotifyPositionChanged();
        }
    }

    public int OverlayY
    {
        get => LiveOverlay?.GetPositionPixels().Y ?? 0;
        set
        {
            var overlay = LiveOverlay;
            if (overlay == null) return;
            overlay.SetPositionPixels(overlay.GetPositionPixels().X, value);
            NotifyPositionChanged();
        }
    }

    /// Upper bounds for the position boxes: the overlay can never be moved further than its
    /// own size short of the far edge.
    public int OverlayMaxX => LiveOverlay?.GetPositionPixels().MaxX ?? 0;
    public int OverlayMaxY => LiveOverlay?.GetPositionPixels().MaxY ?? 0;

    /// Which display the X and Y figures are measured on, so the numbers are never ambiguous
    /// on a multi-monitor setup.
    public string OverlayDisplayLabel => LiveOverlay?.CurrentDisplayLabel ?? "";

    /// <summary>
    /// Everything the position row needs, read in one go.
    ///
    /// The individual properties each query the overlay window separately, which is fine for
    /// a one-off but wasteful now that the overlay reports every reposition — that would be
    /// five round trips per notification, several times a second while tiles settle.
    /// </summary>
    public (int X, int Y, int MaxX, int MaxY, string Display) OverlayPlacement
    {
        get
        {
            var overlay = LiveOverlay;
            if (overlay == null) return (0, 0, 0, 0, "");

            var (x, y, maxX, maxY) = overlay.GetPositionPixels();
            return (x, y, maxX, maxY, overlay.CurrentDisplayLabel);
        }
    }

    /// <summary>
    /// Moves the overlay while a position field is being dragged, without saving.
    ///
    /// Writing settings on every mouse move would mean a file write per pixel of travel;
    /// CommitOverlayPosition stores the result once the drag finishes.
    /// </summary>
    public void MoveOverlayLive(int x, int y)
    {
        LiveOverlay?.SetPositionPixels(x, y, persist: false);
        NotifyPositionChanged();
    }

    /// Stores wherever the overlay currently sits, ending a drag.
    public void CommitOverlayPosition()
    {
        var overlay = LiveOverlay;
        if (overlay == null) return;

        var (x, y, _, _) = overlay.GetPositionPixels();
        overlay.SetPositionPixels(x, y);   // persists
        NotifyPositionChanged();
    }

    /// <summary>
    /// Refreshes the position boxes. Called after the overlay is dragged, so typing a
    /// position and dragging it stay two views of the same thing rather than competing.
    /// </summary>
    public void NotifyPositionChanged()
    {
        OnPropertyChanged(nameof(OverlayX));
        OnPropertyChanged(nameof(OverlayY));
        OnPropertyChanged(nameof(OverlayMaxX));
        OnPropertyChanged(nameof(OverlayMaxY));
        OnPropertyChanged(nameof(OverlayDisplayLabel));
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

    public GpuChoice? SelectedGpuChoice
    {
        get
        {
            foreach (var c in GpuChoices)
                if (c.Id == SelectedGpuId) return c;

            // Null, not "Automatic". Falling back to the first entry made the ComboBox
            // report Automatic as the selection, and WPF then wrote that straight back
            // through the setter — so a pinned GPU that had not been detected yet was
            // quietly converted into a real choice of Automatic. Blank for a moment during
            // startup is harmless; it fills in as soon as the adapter is found.
            return null;
        }
        set
        {
            if (value == null) return;
            SelectedGpuId = value.Id;
            OnPropertyChanged();
        }
    }

    /// True once more than one adapter has been seen, which is the only time the
    /// picker is worth showing at all.
    public bool HasMultipleGpus => AvailableGpus.Count > 1;

    /// Rebuilds the dropdown entries from the adapters detected so far.
    public void RefreshGpuChoices()
    {
        GpuChoices.Clear();
        GpuChoices.Add(new GpuChoice { Id = "", Label = "Automatic", Detail = "Picks the discrete GPU" });

        foreach (var gpu in AvailableGpus)
        {
            GpuChoices.Add(new GpuChoice
            {
                Id     = gpu.Id,
                Label  = gpu.Name,
                Detail = gpu.IsDiscrete ? "Discrete" : "Integrated",
            });
        }

        // The pinned GPU is deliberately never cleared here.
        //
        // This runs at startup before LibreHardwareMonitor has finished enumerating, when
        // the only entry is "Automatic" — so resetting a selection that is merely "not
        // found yet" wiped the user's choice on every single launch. It also fires while a
        // game has the discrete GPU active and the integrated one stops being reported.
        // HardwareService.SelectGpu already falls back to auto-detection when a pinned id
        // does not resolve, so leaving the setting alone costs nothing and the choice
        // survives until the user changes it.

        OnPropertyChanged(nameof(HasMultipleGpus));
        OnPropertyChanged(nameof(SelectedGpuChoice));
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
