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

                // The video memory tile is named for the pool it is showing, and switching
                // graphics can change that, so the name follows the reading.
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(DisplayUnit));
            }
        }
    }

    public bool HasValue => _value.HasValue;

    private float[]? _coreLoads;

    /// <summary>
    /// Each core's load, for the CPU Cores tile, which draws them as bars in place of a number.
    /// </summary>
    /// Replaced, never changed in place, so the bars redraw on every reading.
    public float[]? CoreLoads
    {
        get => _coreLoads;
        set
        {
            if (ReferenceEquals(_coreLoads, value)) return;
            _coreLoads = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsCoreBars));
        }
    }

    private BatteryPower _power;

    /// The battery tile's power: on battery, charging, or plugged in (PowerGlyph draws it).
    public BatteryPower Power
    {
        get => _power;
        set { if (Set(ref _power, value)) OnPropertyChanged(nameof(ShowsPower)); }
    }

    /// True when this tile has a power sign to show beside its name.
    public bool ShowsPower => Definition.Id == "battery" && _power != BatteryPower.Unknown;

    /// True when this tile draws bars rather than a number: CPU Cores, once there are cores to draw.
    public bool ShowsCoreBars => Definition.Id == "cpu_cores" && _coreLoads is { Length: > 0 };

    private bool _isAsleep;

    /// <summary>
    /// True when this tile's graphics card is asleep and was left alone rather than read.
    /// </summary>
    /// <remarks>
    /// Said in words. A dash reads as "Pulse could not get this", which would be wrong: Pulse chose
    /// not to, because reading a sleeping laptop card wakes it and keeps it awake on battery.
    /// </remarks>
    public bool IsAsleep
    {
        get => _isAsleep;
        set
        {
            if (!Set(ref _isAsleep, value)) return;
            OnPropertyChanged(nameof(DisplayValue));
            OnPropertyChanged(nameof(DisplayUnit));
            OnPropertyChanged(nameof(CompactLine));
        }
    }

    /// Known hardware capacity for this tile (total RAM/VRAM), or 0 if none exists.
    private double KnownMax => Definition.Id switch
    {
        "ram_used" => HardwareService.Instance.TotalRamGb,
        "gpu_vram" => HardwareService.Instance.TotalVramGb,
        _          => 0
    };

    /// <summary>
    /// What the tile is called on screen, which for video memory depends on where the memory is.
    /// </summary>
    /// <remarks>
    /// Graphics built into a processor have little or no memory of their own and borrow from
    /// system RAM, and the tile shows the two together. Showing that figure under "VRAM Used"
    /// would be claiming it is a card's own memory, which is the shape of mistake this project
    /// has already had to remove once. Task Manager calls the two together "GPU memory", and
    /// the tile uses its name.
    ///
    /// Graphics temperature works the same way. On graphics that are part of the processor the
    /// temperature is the die's, so it reads exactly the same as CPU Temp, because it is the
    /// same silicon measured once. Task Manager has the same property and says nothing about
    /// it; two identical numbers with no explanation read as a bug, so the tile says which
    /// reading it is.
    ///
    /// Every other tile simply uses its catalogue name.
    /// </remarks>
    public string DisplayLabel => Definition.Id switch
    {
        "gpu_vram" when HardwareService.Instance.VramIsShared         => "GPU Memory",
        "gpu_temp" when HardwareService.Instance.GpuTempIsDie => "GPU Die Temp",

        // CPU+GPU Power is deliberately not renamed on a processor with built-in graphics,
        // unlike the two above. The name is still true there: the package reading is the
        // processor and the graphics together, because they are one chip. "Package Power"
        // would be the precise term and would mean nothing to most people reading a tile.
        // The tooltip explains why it matches CPU Power on those machines.
        _ => Definition.Label,
    };

    /// <summary>
    /// The unit printed beside the reading.
    /// </summary>
    /// <remarks>
    /// Only network speed can differ from the catalogue, and only because the user asked. The
    /// value below is converted to match, so the two can never disagree.
    /// </remarks>
    public string DisplayUnit =>
        _isAsleep && !_value.HasValue ? ""
      : Definition.IsNetworkSpeed
            ? NetworkUnits.Label(SettingsService.Instance.Settings.NetworkUnitBits)
            : Definition.Unit;

    public string DisplayValue
    {
        get
        {
            if (!_value.HasValue) return _isAsleep ? "asleep" : "--";

            var settings = SettingsService.Instance.Settings;
            return TileReading.FormatValue(Definition, _value.Value, settings.NetworkUnitBits, settings.ShowMaxValues, KnownMax);
        }
    }

    /// Compact HUD line: "CPU Temp  68 °C"
    public string CompactLine => $"{DisplayValue} {DisplayUnit}";

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

    /// The colour state the window overlay draws the value in: 0 neutral, 1 normal, 2 warm, 3 hot.
    /// The in-game overlay is sent the same, so both show the same colour for the same reading.
    /// Decided by the shared core (Models/TileReading.cs), against the hardware's real capacity.
    internal int ColourState => State;

    private int State => TileReading.State(Definition, _value, KnownMax);

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

    public double BarFraction => TileReading.BarFraction(Definition, _value, KnownMax);

    public TileViewModel(SensorTileDefinition def) { Definition = def; }

    /// <summary>
    /// Re-raises everything that depends on a setting rather than on the reading, for when one
    /// of those settings changes and no new reading is coming.
    /// </summary>
    /// <remarks>
    /// DisplayUnit belongs here, and its absence was a real bug rather than an omission of
    /// tidiness. The normal overlay binds the unit as its own element, so switching between
    /// MB/s and Mbps converted the number and left the old unit beside it until the next
    /// reading happened to differ. A machine sitting at a flat 0.00 with no traffic never
    /// produces a differing reading, because a value that has not changed raises nothing, so
    /// the wrong unit stayed on screen indefinitely.
    /// </remarks>
    public void RefreshDisplayFormatting()
    {
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(DisplayUnit));
        OnPropertyChanged(nameof(CompactLine));
        OnPropertyChanged(nameof(DisplayLabel));
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
    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set
        {
            if (!Set(ref _overlayOpacity, value)) return;
            OnPropertyChanged(nameof(NormalPanelOpacity));
            OnPropertyChanged(nameof(CompactPanelOpacity));
        }
    }

    /// <summary>
    /// How solid the panel behind the readings is.
    ///
    /// Applied to the panel's brushes rather than to the panel element, because setting
    /// Opacity on the Border would take the readings down with it — and the whole point is
    /// to keep the text while the panel goes. Each brush already carries a designed alpha,
    /// so they are exposed pre-scaled rather than making the XAML do arithmetic.
    /// </summary>
    private double _backgroundOpacity = 1.0;
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            if (!Set(ref _backgroundOpacity, Math.Clamp(value, 0, 1))) return;
            RebuildBorderBrushes();
            OnPropertyChanged(nameof(NormalPanelOpacity));
            OnPropertyChanged(nameof(CompactPanelOpacity));
        }
    }

    // 100 is solid in every look: the compact panels used to be 82 % of the setting, so they
    // never were (owner, 27 Nov 2026: "making it 100 make it full solid"). And never quite
    // nothing: a see-through window lets clicks fall through any pixel with no alpha at all,
    // so at 0 the empty space between readings could not be grabbed to drag the overlay. The
    // floor is about one step of alpha on screen once the overlay's own opacity is applied.
    public double NormalPanelOpacity  => Math.Max(_backgroundOpacity, GrabFloor);
    public double CompactPanelOpacity => Math.Max(_backgroundOpacity, GrabFloor);

    internal double GrabFloor => 2.0 / 255.0 / Math.Max(0.05, _overlayOpacity);

    // The outlines are handed over as finished brushes, while the fills bind Opacity inside
    // the brush. That asymmetry is not a matter of taste: a Border gives its Background brush
    // a data context but does not give one to its BorderBrush, so a binding written inside
    // the BorderBrush never resolves. It fails silently and Opacity keeps its default of 1,
    // which turned a barely-there 0.22 outline into a hard violet line around the overlay.
    // Binding the element's own BorderBrush property has no such problem.
    private const byte AccentR = 0x8B, AccentG = 0x5C, AccentB = 0xF6;

    private WpfBrush _normalBorderBrush  = MakeBorderBrush(1.0 * 0.22);
    private WpfBrush _compactBorderBrush = MakeBorderBrush(1.0 * 0.18);

    public WpfBrush NormalBorderBrush  => _normalBorderBrush;
    public WpfBrush CompactBorderBrush => _compactBorderBrush;

    private static WpfBrush MakeBorderBrush(double alpha)
    {
        var brush = new WpfBrush(WpfColor.FromRgb(AccentR, AccentG, AccentB)) { Opacity = alpha };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The card behind each reading, its outline, and the empty part of a bar.
    /// </summary>
    /// <remarks>
    /// These fade with the panel, which they did not used to. The setting says "turn it all the
    /// way down to leave just the readings on screen", and at zero the panel behind the tiles
    /// disappeared while every tile kept an opaque card and a hard outline, so what was left was
    /// a grid of boxes rather than the readings. A promise the interface makes about itself is
    /// worth as much as a number being right.
    ///
    /// Finished brushes rather than an Opacity binding inside the brush, for the reason set out
    /// above: a BorderBrush written in XAML gets no data context, so a binding inside it never
    /// resolves and silently leaves the outline at full strength.
    /// </remarks>
    private WpfBrush _tileFillBrush     = MakeTileBrush(0x1A, 0x16, 0x32, 1.0);
    private WpfBrush _tileBorderBrush   = MakeTileBrush(0x2A, 0x25, 0x48, 1.0);
    private WpfBrush _tileBarTrackBrush = MakeTileBrush(0x2A, 0x2A, 0x3A, 1.0);

    public WpfBrush TileFillBrush     => _tileFillBrush;
    public WpfBrush TileBorderBrush   => _tileBorderBrush;
    public WpfBrush TileBarTrackBrush => _tileBarTrackBrush;

    private static WpfBrush MakeTileBrush(byte r, byte g, byte b, double alpha)
    {
        var brush = new WpfBrush(WpfColor.FromRgb(r, g, b)) { Opacity = Math.Clamp(alpha, 0, 1) };
        brush.Freeze();
        return brush;
    }

    private void RebuildBorderBrushes()
    {
        _normalBorderBrush  = MakeBorderBrush(_backgroundOpacity * 0.22);
        _compactBorderBrush = MakeBorderBrush(_backgroundOpacity * 0.18);
        OnPropertyChanged(nameof(NormalBorderBrush));
        OnPropertyChanged(nameof(CompactBorderBrush));

        _tileFillBrush     = MakeTileBrush(0x1A, 0x16, 0x32, _backgroundOpacity);
        _tileBorderBrush   = MakeTileBrush(0x2A, 0x25, 0x48, _backgroundOpacity);
        _tileBarTrackBrush = MakeTileBrush(0x2A, 0x2A, 0x3A, _backgroundOpacity);
        OnPropertyChanged(nameof(TileFillBrush));
        OnPropertyChanged(nameof(TileBorderBrush));
        OnPropertyChanged(nameof(TileBarTrackBrush));
    }

    /// The size range, shared by the grip in the overlay's corner and the Size slider.
    public const double MinScale = 0.75, MaxScale = 1.5;

    private double _overlayScale = 1.0;
    public double OverlayScale
    {
        get => _overlayScale;
        set => Set(ref _overlayScale, Math.Clamp(value, MinScale, MaxScale));
    }

    private bool _isCompactMode;
    public bool IsCompactMode
    {
        get => _isCompactMode;
        set => Set(ref _isCompactMode, value);
    }

    private bool _isCompactBar;
    /// Compact mode as one straight line rather than a list.
    public bool IsCompactBar
    {
        get => _isCompactBar;
        set => Set(ref _isCompactBar, value);
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
        _isCompactBar   = s.CompactLayout == "Bar";
        _isDragEnabled  = s.IsDragEnabled;
        _showStatusBar  = s.ShowStatusBar;
        _overlayScale   = s.OverlayScale;
        _backgroundOpacity = s.OverlayBackgroundOpacity;
        RebuildBorderBrushes();
        LoadActiveTiles();
        HardwareService.Instance.SensorsUpdated += OnSensorsUpdated;

        // Frame rate arrives on its own schedule rather than with the sensor readings. It is
        // measured by a different thing entirely, and tying it to the sensor interval meant a
        // five second polling rate also froze the frame rate for five seconds at a time.
        FpsService.Instance.Updated += (_, _) => RefreshFrameRateTiles();
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
            if (def == null) continue;

            // Chosen but unreadable on this machine, so it stays chosen and stays off screen.
            // Showing it meant a tile that read "--" forever, or 0 W for processor power, which
            // is a reading that looks real and is not.
            if (SensorDriver.Instance.StatusOf(id) != TileStatus.Ready) continue;

            ActiveTiles.Add(new TileViewModel(def));
        }
        OverlayOpacity = settings.OverlayOpacity;

        // A newly added frame rate tile would otherwise show nothing until the next timer
        // tick, and the sensor path no longer fills these in.
        RefreshFrameRateTiles();
    }

    /// <summary>
    /// Updates only the frame rate tiles, leaving every other reading alone.
    ///
    /// Separate from the sensor path because the two are measured independently: a sensor
    /// snapshot must not overwrite a fresher frame rate, and a frame rate update must not
    /// disturb a temperature.
    /// </summary>
    private void RefreshFrameRateTiles()
    {
        var fps = FpsService.Instance;

        foreach (var tile in ActiveTiles)
        {
            tile.Value = tile.Definition.Id switch
            {
                "fps"           => fps.CurrentFps,
                "fps_1low"      => fps.OnePercentLowFps,
                _               => tile.Value,   // not a frame rate; leave the sensor value alone
            };
        }
    }

    private void OnSensorsUpdated(object? sender, SensorData data)
    {
        foreach (var tile in ActiveTiles)
        {
            // Frame rates are not part of this reading. They arrive on their own timer, which
            // is faster than the sensor interval, so taking them from the snapshot would keep
            // replacing a fresh value with an older one and make the tile stutter.
            if (tile.Definition.Id is "fps" or "fps_1low")
                continue;

            tile.Value = data.GetById(tile.Definition.Id);
            if (tile.Definition.Id == "cpu_cores") tile.CoreLoads = data.CpuCoreLoads;
            if (tile.Definition.Id == "battery") tile.Power = data.BatteryPower;

            // Video memory is still read while the card sleeps, from Windows, so only the
            // readings that come from the card itself say so.
            tile.IsAsleep = data.GpuAsleep
                         && tile.Definition.Category == SensorCategory.GPU
                         && tile.Definition.Id != "gpu_vram";
        }

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
