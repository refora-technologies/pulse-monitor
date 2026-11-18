using System.Collections.ObjectModel;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>
/// The Sensors page: Pulse's own readings, each with the switches that put it on the overlays, and
/// below them everything this PC reports, like HWMonitor, with the lowest and highest since the
/// page was opened.
/// </summary>
/// <remarks>
/// <para>
/// The owner's design, 18 Nov 2026: a reading can be looked at once here, and added to the overlay
/// from here if wanted. The five advanced readings are added only from this page.
/// </para>
/// <para>
/// The switches are the tile lists' own items (<see cref="TileSelectionItem"/>), not copies, so a
/// switch here and the tile in the settings list are one setting and can never disagree.
/// </para>
/// <para>
/// The full sensor list costs something to read and send, so it is asked for only while the page
/// is open (<see cref="HardwareService.WatchSensors"/>), and the host stops by itself if Pulse
/// stops asking.
/// </para>
/// </remarks>
public sealed class SensorsViewModel : BaseViewModel
{
    private readonly SettingsViewModel _settings;

    public SensorsViewModel(SettingsViewModel settings) { _settings = settings; }

    /// Pulse's readings: the ordinary tiles, then the advanced ones.
    public ObservableCollection<PulseReading> Readings { get; } = new();
    public ObservableCollection<PulseReading> AdvancedReadings { get; } = new();

    /// Everything the PC reports, one group per piece of hardware, in the order the host sends.
    public ObservableCollection<SensorGroup> Groups { get; } = new();

    private readonly Dictionary<string, SensorGroup> _groups = new(StringComparer.Ordinal);

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

    /// True until the first list arrives, so the page says it is gathering rather than looking empty.
    private bool _waiting;
    public bool Waiting { get => _waiting; private set => Set(ref _waiting, value); }

    private string _search = "";

    /// Filters the lists by name as it is typed; empty shows everything.
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) ApplySearch(); }
    }

    /// The In game switches only mean something while the in-game overlay has a list of its own.
    public bool ShowInGameSwitches => SettingsService.Instance.Settings.InGameOverlayEnabled
                                   && SettingsService.Instance.Settings.InGameOwnTiles;

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;

        // Lowest and highest are since the page was opened, so each opening starts afresh.
        Groups.Clear();
        _groups.Clear();
        Waiting = true;
        BuildReadings();
        OnPropertyChanged(nameof(ShowInGameSwitches));

        HardwareService.Instance.TreeUpdated += OnTree;
        HardwareService.Instance.SensorsUpdated += OnReadings;
        FpsService.Instance.Updated += OnFrameRate;
        HardwareService.Instance.WatchSensors(true);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;

        HardwareService.Instance.WatchSensors(false);
        HardwareService.Instance.TreeUpdated -= OnTree;
        HardwareService.Instance.SensorsUpdated -= OnReadings;
        FpsService.Instance.Updated -= OnFrameRate;
    }

    // ── Pulse's readings ──────────────────────────────────────────────────────────────

    private void BuildReadings()
    {
        Readings.Clear();
        AdvancedReadings.Clear();

        foreach (var overlay in _settings.AllTiles.OrderBy(t => SensorTileDefinition.All.IndexOf(t.Definition)))
        {
            var inGame = _settings.InGameTiles.FirstOrDefault(t => t.Definition.Id == overlay.Definition.Id);
            var reading = new PulseReading(overlay, inGame);
            (overlay.Definition.IsAdvanced ? AdvancedReadings : Readings).Add(reading);
        }

        Refresh(HardwareService.Instance.Current);
        ApplySearch();
    }

    private void OnReadings(object? sender, SensorData data) => Refresh(data);

    private void OnFrameRate(object? sender, EventArgs e)
    {
        foreach (var r in Readings.Concat(AdvancedReadings))
            if (r.Id is "fps" or "fps_1low") r.Show(r.Id == "fps" ? FpsService.Instance.CurrentFps : FpsService.Instance.OnePercentLowFps, false);
    }

    private void Refresh(SensorData? data)
    {
        if (data is null) return;
        foreach (var r in Readings.Concat(AdvancedReadings))
        {
            if (r.Id is "fps" or "fps_1low") continue;   // on their own timer, see OnFrameRate
            bool asleep = data.GpuAsleep && r.Overlay.Definition.Category == SensorCategory.GPU && r.Id != "gpu_vram";
            r.Show(data.GetById(r.Id), asleep);
        }
    }

    // ── Everything the PC reports ─────────────────────────────────────────────────────

    private void OnTree(object? sender, SensorTree tree)
    {
        if (!IsOpen) return;
        Waiting = false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        foreach (var node in tree.Hardware)
        {
            seen.Add(node.Id);
            if (!_groups.TryGetValue(node.Id, out var group))
            {
                group = new SensorGroup(node.Id);
                _groups[node.Id] = group;
                Groups.Insert(Math.Min(position, Groups.Count), group);
            }
            group.Update(node);
            position++;
        }

        // Hardware that has gone since (a card switched off, a drive removed) leaves the page.
        foreach (var gone in Groups.Where(g => !seen.Contains(g.Id)).ToList())
        {
            Groups.Remove(gone);
            _groups.Remove(gone.Id);
        }

        ApplySearch();
    }

    private void ApplySearch()
    {
        var words = _search.Trim();
        foreach (var r in Readings.Concat(AdvancedReadings))
            r.IsVisible = words.Length == 0 || r.Label.Contains(words, StringComparison.OrdinalIgnoreCase);
        foreach (var g in Groups) g.Filter(words);
    }
}

/// <summary>
/// One of Pulse's readings on the Sensors page: its value now and its two switches.
/// </summary>
public sealed class PulseReading : BaseViewModel
{
    /// The window overlay's tile list item, so its switch is the list's own.
    public TileSelectionItem Overlay { get; }

    /// The in-game list's item, or null when the in-game list does not have this reading.
    public TileSelectionItem? InGame { get; }

    public PulseReading(TileSelectionItem overlay, TileSelectionItem? inGame)
    {
        Overlay = overlay;
        InGame  = inGame;
    }

    public string Id => Overlay.Definition.Id;
    public string Label => Overlay.Definition.Label;
    public string Description => Overlay.Definition.Description ?? "";
    public string Unit => Overlay.DisplayUnit;

    /// Whether the in-game list has this reading, so it can have an In game switch.
    public bool HasInGame => InGame != null;

    /// Whether this PC can give this reading; when not, the row says why instead of a switch.
    public bool IsAvailable => Overlay.IsAvailable;
    public string Unavailable => Overlay.StatusHint;

    private string _value = "--";
    public string Value { get => _value; private set => Set(ref _value, value); }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    /// Written the way the overlay writes it, by the shared core.
    public void Show(float? value, bool asleep)
    {
        if (!value.HasValue) { Value = asleep ? "asleep" : "--"; return; }

        var settings = SettingsService.Instance.Settings;
        double capacity = Id switch
        {
            "ram_used" => HardwareService.Instance.TotalRamGb,
            "gpu_vram" => HardwareService.Instance.TotalVramGb,
            _          => 0,
        };
        Value = TileReading.FormatValue(Overlay.Definition, value.Value, settings.NetworkUnitBits, false, capacity);
    }
}

/// <summary>
/// One piece of hardware on the Sensors page and its sensors.
/// </summary>
public sealed class SensorGroup : BaseViewModel
{
    public string Id { get; }
    public SensorGroup(string id) { Id = id; }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private bool _asleep;
    /// A graphics card asleep and left alone: no values, and the group says so.
    public bool Asleep { get => _asleep; private set => Set(ref _asleep, value); }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }

    public ObservableCollection<SensorLine> Rows { get; } = new();
    private readonly Dictionary<string, SensorLine> _rows = new(StringComparer.Ordinal);

    public void Update(SensorNode node)
    {
        Name   = node.Name;
        Asleep = node.Asleep;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sensor in node.Sensors)
        {
            seen.Add(sensor.Id);
            if (!_rows.TryGetValue(sensor.Id, out var row))
            {
                row = new SensorLine(sensor.Id);
                _rows[sensor.Id] = row;
                Rows.Add(row);
            }
            row.Update(sensor);
        }

        // Asleep, a card sends no sensors: its rows go rather than stand there with old values.
        foreach (var gone in Rows.Where(r => !seen.Contains(r.Id)).ToList())
        {
            Rows.Remove(gone);
            _rows.Remove(gone.Id);
        }
    }

    public void Filter(string words)
    {
        bool groupMatches = words.Length == 0 || Name.Contains(words, StringComparison.OrdinalIgnoreCase);
        bool any = false;
        foreach (var r in Rows)
        {
            r.IsVisible = groupMatches || r.Name.Contains(words, StringComparison.OrdinalIgnoreCase);
            any |= r.IsVisible;
        }
        IsVisible = groupMatches || any;
    }
}

/// <summary>
/// One sensor: now, lowest and highest since the page opened, and the tile it feeds, if any.
/// </summary>
public sealed class SensorLine : BaseViewModel
{
    public string Id { get; }
    public SensorLine(string id) { Id = id; }

    public string Name { get; private set; } = "";
    public string Unit { get; private set; } = "";

    private string _now = "--", _low = "--", _high = "--";
    public string Now  { get => _now;  private set => Set(ref _now, value); }
    public string Low  { get => _low;  private set => Set(ref _low, value); }
    public string High { get => _high; private set => Set(ref _high, value); }

    /// "on the overlay as CPU Temp" style tag: which of Pulse's readings this sensor is, if any.
    private string _tile = "";
    public string Tile { get => _tile; private set => Set(ref _tile, value); }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    private float? _min, _max;
    private string _type = "";

    public void Update(SensorRow sensor)
    {
        if (Name.Length == 0)
        {
            Name = sensor.Name;
            _type = sensor.Type;
            Unit = SensorFormat.Unit(sensor.Type);
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Unit));
        }

        if (sensor.Value is { } v && float.IsFinite(v))
        {
            _min = _min is { } lo ? MathF.Min(lo, v) : v;
            _max = _max is { } hi ? MathF.Max(hi, v) : v;
        }

        Now  = SensorFormat.Format(_type, sensor.Value).Value;
        Low  = SensorFormat.Format(_type, _min).Value;
        High = SensorFormat.Format(_type, _max).Value;

        var label = sensor.Tile is { } id ? SensorTileDefinition.All.FirstOrDefault(d => d.Id == id)?.Label : null;
        Tile = label is null ? "" : "Pulse: " + label;
    }
}
