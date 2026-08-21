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
    private static SettingsViewModel? _instance;
    public static SettingsViewModel Instance => _instance ??= new SettingsViewModel();

    public ObservableCollection<TileSelectionItem> AllTiles { get; } = new();

    private double _opacity;
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Set(ref _opacity, value))
            {
                OnPropertyChanged(nameof(OpacityPercent));
                SettingsService.Instance.Settings.OverlayOpacity = value;
                OverlayViewModel.Instance.OverlayOpacity = value;
                SettingsService.Instance.Save();
            }
        }
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
            if (Set(ref _startWithWindows, value))
                SettingsService.Instance.UpdateStartWithWindows(value);
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
                s.OverlayCustomX = -1;
                s.OverlayCustomY = -1;
                if (s.OverlayPosition == "Custom")
                    s.OverlayPosition = "TopRight";
                SettingsService.Instance.Save();
            }
        }
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
            return GpuChoices.Count > 0 ? GpuChoices[0] : null;
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
        var previous = SelectedGpuId;

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

        // A pinned GPU that is no longer present would otherwise leave the box blank.
        bool stillThere = false;
        foreach (var c in GpuChoices)
            if (c.Id == previous) { stillThere = true; break; }
        if (!stillThere) SelectedGpuId = "";

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
        s.OverlayCustomX   = -1;
        s.OverlayCustomY   = -1;
        _overlayPosition   = position;
        _isDragEnabled     = false;
        OverlayViewModel.Instance.IsDragEnabled = false;
        SettingsService.Instance.Save();
        OnPropertyChanged(nameof(OverlayPosition));
        OnPropertyChanged(nameof(IsDragEnabled));
    }
}
