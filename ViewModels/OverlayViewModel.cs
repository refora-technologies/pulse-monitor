using System.Collections.ObjectModel;
using Pulse.Models;
using Pulse.Services;

using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace Pulse.ViewModels;

public class TileViewModel : BaseViewModel
{
    public SensorTileDefinition Definition { get; }

    private float? _value;
    public float? Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(ValueColor));
                OnPropertyChanged(nameof(ValueBrush));
                OnPropertyChanged(nameof(BarFraction));
                OnPropertyChanged(nameof(HasValue));
                OnPropertyChanged(nameof(CompactLine));
            }
        }
    }

    public bool HasValue => _value.HasValue;

    /// Known hardware capacity for this tile (total RAM/VRAM), or 0 if none exists.
    private double KnownMax => Definition.Id switch
    {
        "ram_used" => HardwareService.Instance.TotalRamGb,
        "gpu_vram" => HardwareService.Instance.TotalVramGb,
        _          => 0
    };

    public string DisplayValue
    {
        get
        {
            if (!_value.HasValue) return "--";

            var formatted = Definition.Unit switch
            {
                "GHz"  => $"{_value:F2}",
                "GB"   => $"{_value:F1}",
                "%"    => $"{_value:F0}",
                "MB/s" => $"{_value:F2}",
                _      => $"{_value:F0}"
            };

            if (Definition.HasKnownMax && SettingsService.Instance.Settings.ShowMaxValues)
            {
                var max = KnownMax;
                if (max > 0) return $"{formatted} / {max:F0}";
            }

            return formatted;
        }
    }

    /// Compact HUD line: "CPU Temp  68 °C"
    public string CompactLine => $"{DisplayValue} {Definition.Unit}";

    // Tile values only ever take one of four states, so the colours and brushes are
    // shared and frozen rather than allocated per read. Previously every binding
    // evaluation built a new SolidColorBrush, which at fifteen tiles and several
    // notifications per poll produced a steady stream of garbage for no benefit.
    internal static readonly WpfColor NeutralColor = WpfColor.FromRgb(0xF4, 0xF2, 0xFC);
    internal static readonly WpfColor DangerColor  = WpfColor.FromRgb(0xFF, 0x5C, 0x6C);
    internal static readonly WpfColor WarnColor    = WpfColor.FromRgb(0xFF, 0xB4, 0x54);
    internal static readonly WpfColor NormalColor  = WpfColor.FromRgb(0x3D, 0xDC, 0x97);

    internal static readonly WpfBrush NeutralBrush = Frozen(NeutralColor);
    internal static readonly WpfBrush DangerBrush  = Frozen(DangerColor);
    internal static readonly WpfBrush WarnBrush    = Frozen(WarnColor);
    internal static readonly WpfBrush NormalBrush  = Frozen(NormalColor);

    internal static WpfBrush Frozen(WpfColor color)
    {
        var brush = new WpfBrush(color);
        brush.Freeze();   // freezing lets WPF share it across threads without cloning
        return brush;
    }

    /// <summary>
    /// Warning and danger points for this tile.
    ///
    /// For capacity tiles these scale with the hardware actually fitted. The catalogue
    /// values assume 16 GB of RAM and 6 GB of VRAM, so on a 64 GB machine 14.5 GB used —
    /// under a quarter of it — was being coloured as dangerous, and on a 24 GB card the
    /// same nonsense at 5.5 GB. The ratios are taken from those defaults, roughly three
    /// quarters full for a warning and ninety percent for danger.
    /// </summary>
    private (float Warn, float Danger) Thresholds
    {
        get
        {
            if (!Definition.HasKnownMax) return (Definition.WarnThreshold, Definition.DangerThreshold);

            double capacity = KnownMax;
            if (capacity <= 0 || Definition.BarMax <= 0)
                return (Definition.WarnThreshold, Definition.DangerThreshold);

            double scale = capacity / Definition.BarMax;
            return ((float)(Definition.WarnThreshold * scale), (float)(Definition.DangerThreshold * scale));
        }
    }

    /// 0 = neutral, 1 = normal, 2 = warning, 3 = danger.
    private int State
    {
        get
        {
            if (!_value.HasValue) return 0;

            var (warn, danger) = Thresholds;
            if (danger == 0) return 0;
            if (_value >= danger) return 3;
            if (_value >= warn)   return 2;
            return 1;
        }
    }

    public WpfColor ValueColor => State switch
    {
        3 => DangerColor,
        2 => WarnColor,
        1 => NormalColor,
        _ => NeutralColor,
    };

    public WpfBrush ValueBrush => State switch
    {
        3 => DangerBrush,
        2 => WarnBrush,
        1 => NormalBrush,
        _ => NeutralBrush,
    };

    public double BarFraction
    {
        get
        {
            if (!_value.HasValue || !Definition.HasBar) return 0;
            double max = Definition.HasKnownMax ? KnownMax : Definition.BarMax;
            if (max <= 0) max = Definition.BarMax;
            if (max <= 0) return 0;
            return Math.Clamp((double)_value.Value / max, 0, 1);
        }
    }

    public TileViewModel(SensorTileDefinition def) { Definition = def; }

    /// Re-raises formatting-dependent properties without needing a new sensor value —
    /// used when the "show max values" setting is toggled.
    public void RefreshDisplayFormatting()
    {
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(CompactLine));
    }
}

public class OverlayViewModel : BaseViewModel
{
    /// Lazy rather than `??=`: that is not atomic, and these are reached from the polling
    /// thread and the UI thread at the same time during startup. Losing the race builds two
    /// instances, each with its own event subscribers, so notifications reach an object
    /// nobody is listening to.
    private static readonly Lazy<OverlayViewModel> LazyInstance =
        new(() => new OverlayViewModel(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static OverlayViewModel Instance => LazyInstance.Value;

    public ObservableCollection<TileViewModel> ActiveTiles { get; } = new();

    private string _statusText = "Initializing...";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private WpfColor _statusColor = WpfColors.Gray;
    public WpfColor StatusColor
    {
        get => _statusColor;
        private set
        {
            if (Set(ref _statusColor, value))
            {
                _statusBrush = value == TileViewModel.DangerColor ? TileViewModel.DangerBrush
                             : value == TileViewModel.WarnColor   ? TileViewModel.WarnBrush
                             : value == TileViewModel.NormalColor ? TileViewModel.NormalBrush
                             : TileViewModel.Frozen(value);
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }
    // Frozen, like the tile brushes, so the status dot and text don't allocate on
    // every sensor update.
    private WpfBrush _statusBrush = TileViewModel.Frozen(WpfColors.Gray);
    public WpfBrush StatusBrush => _statusBrush;

    private double _overlayOpacity = 0.85;
    public double OverlayOpacity { get => _overlayOpacity; set => Set(ref _overlayOpacity, value); }

    private double _overlayScale = 1.0;
    public double OverlayScale
    {
        get => _overlayScale;
        set => Set(ref _overlayScale, Math.Clamp(value, 0.75, 1.5));
    }

    private bool _isCompactMode;
    public bool IsCompactMode
    {
        get => _isCompactMode;
        set => Set(ref _isCompactMode, value);
    }

    private bool _isDragEnabled;
    public bool IsDragEnabled
    {
        get => _isDragEnabled;
        set => Set(ref _isDragEnabled, value);
    }

    private bool _showStatusBar = true;
    public bool ShowStatusBar
    {
        get => _showStatusBar;
        set => Set(ref _showStatusBar, value);
    }

    private OverlayViewModel()
    {
        var s = SettingsService.Instance.Settings;
        _isCompactMode  = s.IsCompactMode;
        _isDragEnabled  = s.IsDragEnabled;
        _showStatusBar  = s.ShowStatusBar;
        _overlayScale   = s.OverlayScale;
        LoadActiveTiles();
        HardwareService.Instance.SensorsUpdated += OnSensorsUpdated;
        SettingsService.Instance.SettingsChanged += (_, _) =>
        {
            foreach (var tile in ActiveTiles) tile.RefreshDisplayFormatting();
        };
    }

    public void LoadActiveTiles()
    {
        ActiveTiles.Clear();
        var settings = SettingsService.Instance.Settings;
        foreach (var id in settings.ActiveTileIds.Distinct())
        {
            var def = SensorTileDefinition.All.FirstOrDefault(d => d.Id == id);
            if (def != null) ActiveTiles.Add(new TileViewModel(def));
        }
        OverlayOpacity = settings.OverlayOpacity;
    }

    private void OnSensorsUpdated(object? sender, SensorData data)
    {
        foreach (var tile in ActiveTiles)
            tile.Value = data.GetById(tile.Definition.Id);
        UpdateStatus(data);
    }

    /// <summary>
    /// Summarises the tiles that are actually on screen.
    ///
    /// This used to look only at CPU and GPU temperature, so an overlay showing just RAM,
    /// disk and network sat on "Reading sensors…" forever — the sensors were being read
    /// perfectly well, there simply were no temperatures among them. Now the summary
    /// reflects whichever tiles are enabled, and only reports waiting when nothing has
    /// arrived at all.
    /// </summary>
    private void UpdateStatus(SensorData data)
    {
        // A failed sensor driver is the one thing worth saying out loud. Until now it was
        // recorded internally and never shown, so the user just saw every tile reading "--"
        // with no indication that anything was wrong or what to do about it.
        var fault = HardwareService.Instance.HardwareFault;
        if (fault != null)
        {
            StatusText  = "Sensors unavailable — see About for details";
            StatusColor = TileViewModel.DangerColor;
            return;
        }

        // Only temperatures from tiles the user actually has on screen. Reading a hidden
        // sensor and reporting on it made the summary describe something not shown.
        var temps = new List<float>();
        if (data.CpuTemp.HasValue && ActiveTiles.Any(t => t.Definition.Id == "cpu_temp")) temps.Add(data.CpuTemp.Value);
        if (data.GpuTemp.HasValue && ActiveTiles.Any(t => t.Definition.Id == "gpu_temp")) temps.Add(data.GpuTemp.Value);

        if (!temps.Any())
        {
            bool anyReading = ActiveTiles.Any(t => t.HasValue);

            StatusText  = anyReading ? "All systems nominal" : "Reading sensors...";
            StatusColor = anyReading ? TileViewModel.NormalColor : WpfColors.Gray;
            return;
        }

        var maxTemp   = temps.Max();
        var cpuDanger = SensorTileDefinition.All.First(d => d.Id == "cpu_temp").DangerThreshold;
        var cpuWarn   = SensorTileDefinition.All.First(d => d.Id == "cpu_temp").WarnThreshold;

        if (maxTemp >= cpuDanger)
        {
            StatusText  = $"Running hot — {maxTemp:F0}°C peak";
            StatusColor = TileViewModel.DangerColor;
        }
        else if (maxTemp >= cpuWarn)
        {
            StatusText  = $"Warming up — {maxTemp:F0}°C peak";
            StatusColor = TileViewModel.WarnColor;
        }
        else
        {
            StatusText  = "All systems nominal";
            StatusColor = TileViewModel.NormalColor;
        }
    }
}
