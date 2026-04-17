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

    public string DisplayValue => _value.HasValue
        ? Definition.Unit switch
        {
            "GHz" => $"{_value:F2}",
            "GB"  => $"{_value:F1}",
            "%"   => $"{_value:F0}",
            _     => $"{_value:F0}"
        }
        : "--";

    /// Compact HUD line: "CPU Temp  68 °C"
    public string CompactLine => $"{DisplayValue} {Definition.Unit}";

    public WpfColor ValueColor
    {
        get
        {
            if (!_value.HasValue || Definition.DangerThreshold == 0)
                return WpfColor.FromRgb(0xF0, 0xF0, 0xF5);
            if (_value >= Definition.DangerThreshold)
                return WpfColor.FromRgb(0xFF, 0x4B, 0x4B);   // Red
            if (_value >= Definition.WarnThreshold)
                return WpfColor.FromRgb(0xFF, 0xB4, 0x00);   // Amber
            return WpfColor.FromRgb(0x00, 0xFF, 0x87);        // Green
        }
    }

    public WpfBrush ValueBrush => new(ValueColor);

    public double BarFraction
    {
        get
        {
            if (!_value.HasValue || !Definition.HasBar || Definition.BarMax <= 0) return 0;
            return Math.Clamp((double)_value.Value / Definition.BarMax, 0, 1);
        }
    }

    public TileViewModel(SensorTileDefinition def) { Definition = def; }
}

public class OverlayViewModel : BaseViewModel
{
    private static OverlayViewModel? _instance;
    public static OverlayViewModel Instance => _instance ??= new OverlayViewModel();

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
                OnPropertyChanged(nameof(StatusBrush));
        }
    }
    public WpfBrush StatusBrush => new(_statusColor);

    private double _overlayOpacity = 0.85;
    public double OverlayOpacity { get => _overlayOpacity; set => Set(ref _overlayOpacity, value); }

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

    private OverlayViewModel()
    {
        var s = SettingsService.Instance.Settings;
        _isCompactMode = s.IsCompactMode;
        _isDragEnabled = s.IsDragEnabled;
        LoadActiveTiles();
        HardwareService.Instance.SensorsUpdated += OnSensorsUpdated;
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

    private void UpdateStatus(SensorData data)
    {
        var temps = new[] { data.CpuTemp, data.GpuTemp }
            .Where(t => t.HasValue).Select(t => t!.Value).ToList();

        if (!temps.Any())
        {
            StatusText  = "Reading sensors...";
            StatusColor = WpfColors.Gray;
            return;
        }

        var maxTemp   = temps.Max();
        var cpuDanger = SensorTileDefinition.All.First(d => d.Id == "cpu_temp").DangerThreshold;
        var cpuWarn   = SensorTileDefinition.All.First(d => d.Id == "cpu_temp").WarnThreshold;

        if (maxTemp >= cpuDanger)
        {
            StatusText  = $"Running hot — {maxTemp:F0}°C peak";
            StatusColor = WpfColor.FromRgb(0xFF, 0x4B, 0x4B);
        }
        else if (maxTemp >= cpuWarn)
        {
            StatusText  = $"Warming up — {maxTemp:F0}°C peak";
            StatusColor = WpfColor.FromRgb(0xFF, 0xB4, 0x00);
        }
        else
        {
            StatusText  = "All systems nominal";
            StatusColor = WpfColor.FromRgb(0x00, 0xFF, 0x87);
        }
    }
}
