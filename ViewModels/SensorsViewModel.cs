using System.Collections.ObjectModel;
using System.Windows.Media;
using Pulse.Models;
using Pulse.Services;
using Color = System.Windows.Media.Color;

namespace Pulse.ViewModels;

/// <summary>
/// The Sensors window: everything this PC reports, one part at a time (the processor, each
/// graphics adapter, memory, drives, network, battery, games), with Pulse's own readings for that
/// part at the top, each with the switches that put it on the overlays.
/// </summary>
/// <remarks>
/// <para>
/// The design, 18 Nov 2026: a reading can be looked at once here, and added to the overlay
/// from here if wanted. The five advanced readings are added only from here. Its own window, with
/// an overview of every part as its front page (26 Nov 2026).
/// </para>
/// <para>
/// The switches are the tile lists' own items (<see cref="TileSelectionItem"/>), not copies, so a
/// switch here and the tile in the settings list are one setting and can never disagree.
/// </para>
/// <para>
/// The full sensor list costs something to read and send, so it is asked for only while the
/// window is on screen (<see cref="HardwareService.WatchSensors"/>): not once it is closed or
/// minimized, and the host stops by itself if Pulse stops asking.
/// </para>
/// </remarks>
public sealed class SensorsViewModel : BaseViewModel
{
    private readonly SettingsViewModel _settings;

    public SensorsViewModel(SettingsViewModel settings) { _settings = settings; }

    /// The parts of this PC, in the sidebar's order.
    public ObservableCollection<SensorPart> Parts { get; } = new();

    private readonly Dictionary<string, SensorPart> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SensorGroup> _groups = new(StringComparer.Ordinal);

    /// Pulse's readings, every tile, ordinary and advanced, by tile id.
    private readonly Dictionary<string, PulseReading> _readings = new(StringComparer.Ordinal);

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

    private bool _watching;

    /// True until the first list arrives, so the window says it is gathering rather than looking empty.
    private bool _waiting;
    public bool Waiting { get => _waiting; private set => Set(ref _waiting, value); }

    private string _search = "";

    /// Filters every part by name as it is typed; empty shows the chosen part or the overview.
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            ApplySearch();
            OnPropertyChanged(nameof(Searching));
            NotifyPage();
        }
    }

    public bool Searching => _search.Trim().Length > 0;

    private SensorPart? _selected;

    /// The part on show, or null for the overview.
    public SensorPart? SelectedPart { get => _selected; private set => Set(ref _selected, value); }

    public bool ShowOverview => !Searching && SelectedPart is null && !Waiting;
    public bool ShowPart     => !Searching && SelectedPart is not null;
    public bool OverviewSelected => SelectedPart is null;

    /// The In game switches only mean something while the in-game overlay has a list of its own.
    public bool ShowInGameSwitches => SettingsService.Instance.Settings.InGameOverlayEnabled
                                   && SettingsService.Instance.Settings.InGameOwnTiles;

    public void Select(SensorPart? part)
    {
        SelectedPart = part;
        foreach (var p in Parts) p.IsSelected = p == part;
        if (Searching) Search = "";
        NotifyPage();
    }

    private void NotifyPage()
    {
        OnPropertyChanged(nameof(ShowOverview));
        OnPropertyChanged(nameof(ShowPart));
        OnPropertyChanged(nameof(OverviewSelected));
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;

        // Lowest and highest are since the window was opened, so each opening starts afresh.
        Parts.Clear();
        _parts.Clear();
        _groups.Clear();
        _search = "";
        OnPropertyChanged(nameof(Search));
        OnPropertyChanged(nameof(Searching));
        SelectedPart = null;
        Waiting = true;
        BuildReadings();
        OnPropertyChanged(nameof(ShowInGameSwitches));
        NotifyPage();

        HardwareService.Instance.TreeUpdated += OnTree;
        HardwareService.Instance.SensorsUpdated += OnReadings;
        FpsService.Instance.Updated += OnFrameRate;
        Resume();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;

        _watching = false;
        HardwareService.Instance.WatchSensors(false);
        HardwareService.Instance.TreeUpdated -= OnTree;
        HardwareService.Instance.SensorsUpdated -= OnReadings;
        FpsService.Instance.Updated -= OnFrameRate;
    }

    /// Minimized: off the screen, so the host stops sending every sensor. The lists stay as they
    /// are, lowest and highest included, for when the window comes back.
    public void Pause()
    {
        if (!IsOpen || !_watching) return;
        _watching = false;
        HardwareService.Instance.WatchSensors(false);
    }

    public void Resume()
    {
        if (!IsOpen || _watching) return;
        _watching = true;
        HardwareService.Instance.WatchSensors(true);
    }

    // ── Pulse's readings ──────────────────────────────────────────────────────────────

    private void BuildReadings()
    {
        _readings.Clear();
        foreach (var overlay in _settings.AllTiles.OrderBy(t => SensorTileDefinition.All.IndexOf(t.Definition)))
        {
            var inGame = _settings.InGameTiles.FirstOrDefault(t => t.Definition.Id == overlay.Definition.Id);
            _readings[overlay.Definition.Id] = new PulseReading(overlay, inGame);
        }
        Refresh(HardwareService.Instance.Current);
    }

    private void OnReadings(object? sender, SensorData data)
    {
        Refresh(data);
        foreach (var part in Parts) part.Refresh();
    }

    /// Four times a second (FpsService's pace): the frame rate's value only, and the Games card
    /// only when that changed. Its graph gets a point with the other readings, so every graph
    /// covers the same minute.
    private void OnFrameRate(object? sender, EventArgs e)
    {
        bool changed = false;
        foreach (var id in new[] { "fps", "fps_1low" })
            if (_readings.TryGetValue(id, out var r))
                changed |= r.Show(id == "fps" ? FpsService.Instance.CurrentFps : FpsService.Instance.OnePercentLowFps, false, record: false);
        if (changed && _parts.TryGetValue(nameof(PartKind.Games), out var games)) games.Refresh();
    }

    private void Refresh(SensorData? data)
    {
        if (data is null) return;
        foreach (var r in _readings.Values)
        {
            if (r.Id is "fps" or "fps_1low")
            {
                // Shown on their own timer (OnFrameRate); a point for the graph here.
                r.Show(r.Id == "fps" ? FpsService.Instance.CurrentFps : FpsService.Instance.OnePercentLowFps, false);
                continue;
            }
            bool asleep = data.GpuAsleep && r.Overlay.Definition.Category == SensorCategory.GPU && r.Id != "gpu_vram";
            // CPU Cores draws a bar per core, here as on the overlay.
            if (r.Id == "cpu_cores") { r.ShowCores(data.CpuCoreLoads); continue; }
            r.Show(data.GetById(r.Id), asleep);
        }
    }

    // ── Everything the PC reports ─────────────────────────────────────────────────────

    private void OnTree(object? sender, SensorTree tree)
    {
        if (!IsOpen) return;
        if (Waiting) { Waiting = false; NotifyPage(); }

        // Each piece of hardware into its part: one part for the processor, one per graphics
        // adapter, one for all the drives, one for all the network adapters.
        var nodesOf = new Dictionary<string, List<SensorGroup>>(StringComparer.Ordinal);
        var kinds   = new Dictionary<string, PartKind>(StringComparer.Ordinal);
        var gpuKeys = new List<string>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tree.Hardware)
        {
            if (SensorPart.KindOf(node.Type) is not { } kind) continue;
            string key = kind == PartKind.Gpu ? "gpu:" + node.Id : kind.ToString();
            if (kind == PartKind.Gpu && !gpuKeys.Contains(key)) gpuKeys.Add(key);

            seen.Add(node.Id);
            if (!_groups.TryGetValue(node.Id, out var group))
            {
                group = new SensorGroup(node.Id);
                _groups[node.Id] = group;
            }
            group.Update(node, tile => tile is not null && _readings.TryGetValue(tile, out var r) ? r : null);

            if (!nodesOf.TryGetValue(key, out var list)) nodesOf[key] = list = new List<SensorGroup>();
            list.Add(group);
            kinds[key] = kind;
        }
        foreach (var gone in _groups.Keys.Where(id => !seen.Contains(id)).ToList()) _groups.Remove(gone);

        // The parts in the sidebar's order. Battery and Games are there whenever Pulse has a
        // reading for them, sensors or not.
        var order = new List<string> { nameof(PartKind.Cpu) };
        order.AddRange(gpuKeys);
        order.AddRange(new[] { nameof(PartKind.Memory), nameof(PartKind.Drives), nameof(PartKind.Network), nameof(PartKind.Battery), nameof(PartKind.Games) });
        kinds.TryAdd(nameof(PartKind.Battery), PartKind.Battery);
        kinds.TryAdd(nameof(PartKind.Games), PartKind.Games);

        // Pulse's GPU readings belong to the adapter Pulse is reading them from.
        string activeGpu = HardwareService.Instance.ActiveGpuName;
        string? gpuOfReadings = gpuKeys.FirstOrDefault(k => nodesOf[k].Any(g => g.Name == activeGpu)) ?? gpuKeys.FirstOrDefault();

        var wanted = new List<SensorPart>();
        foreach (var key in order)
        {
            if (!kinds.TryGetValue(key, out var kind)) continue;
            var nodes = nodesOf.GetValueOrDefault(key) ?? new List<SensorGroup>();
            var readings = _readings.Values.Where(r => PartOf(r) == kind && (kind != PartKind.Gpu || key == gpuOfReadings)).ToList();

            // A part with no sensors and none of Pulse's readings this PC can give is left out:
            // no battery on a desktop.
            if (nodes.Count == 0 && !readings.Any(r => r.IsAvailable)) continue;

            if (!_parts.TryGetValue(key, out var part)) _parts[key] = part = new SensorPart(key, kind);
            part.Update(nodes, readings, key == gpuOfReadings);
            wanted.Add(part);
        }

        // The sidebar changes only when a part comes or goes, so it does not flicker every second.
        if (!wanted.SequenceEqual(Parts))
        {
            Parts.Clear();
            foreach (var p in wanted) Parts.Add(p);
            foreach (var key in _parts.Keys.Where(k => !wanted.Any(p => p.Key == k)).ToList()) _parts.Remove(key);
            if (SelectedPart is { } s && !wanted.Contains(s)) Select(null);
        }

        ApplySearch();
    }

    /// The part a reading of Pulse's belongs to.
    private static PartKind PartOf(PulseReading r) => r.Id switch
    {
        "fps" or "fps_1low" => PartKind.Games,
        "battery"           => PartKind.Battery,
        "sys_power"         => PartKind.Cpu,
        _ => r.Overlay.Definition.Category switch
        {
            SensorCategory.CPU     => PartKind.Cpu,
            SensorCategory.GPU     => PartKind.Gpu,
            SensorCategory.Memory  => PartKind.Memory,
            SensorCategory.Network => PartKind.Network,
            SensorCategory.Storage => PartKind.Drives,
            _                      => PartKind.Games,
        },
    };

    private void ApplySearch()
    {
        var words = _search.Trim();
        foreach (var part in Parts) part.Filter(words);
    }
}

/// <summary>
/// One part of the PC in the Sensors window: its headline number, Pulse's readings for it, and
/// every sensor on the hardware it is made of.
/// </summary>
public sealed class SensorPart : BaseViewModel
{
    public string Key { get; }
    public PartKind Kind { get; }

    public SensorPart(string key, PartKind kind)
    {
        Key = key;
        Kind = kind;
        Title = kind switch
        {
            PartKind.Cpu     => "CPU",
            PartKind.Memory  => "Memory",
            PartKind.Drives  => "Drives",
            PartKind.Network => "Network",
            PartKind.Battery => "Battery",
            PartKind.Games   => "Games",
            _                => "Graphics",
        };
        Accent = Brush(kind switch
        {
            PartKind.Cpu     => "#A78BFA",
            PartKind.Gpu     => "#34D399",
            PartKind.Memory  => "#F472B6",
            PartKind.Drives  => "#FBBF24",
            PartKind.Network => "#22D3EE",
            PartKind.Battery => "#A3E635",
            _                => "#FB923C",
        });
    }

    /// The part the sensor library's kind of hardware belongs to, or null for one the window
    /// does not show.
    public static PartKind? KindOf(string type) => type switch
    {
        "Cpu"                                 => PartKind.Cpu,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => PartKind.Gpu,
        "Memory"                              => PartKind.Memory,
        "Storage"                             => PartKind.Drives,
        "Network"                             => PartKind.Network,
        "Battery"                             => PartKind.Battery,
        _                                     => null,
    };

    private string _title = "";
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _model = "";
    /// What the part is: the processor's name, the drives' names.
    public string Model { get => _model; private set => Set(ref _model, value); }

    private SolidColorBrush _accent = Brush("#A78BFA");
    public SolidColorBrush Accent { get => _accent; private set => Set(ref _accent, value); }

    private string _headline = "--";
    /// The part's one number for the sidebar and its card: temperature, how full, how fast.
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    private IReadOnlyList<float> _headlineHistory = Array.Empty<float>();
    public IReadOnlyList<float> HeadlineHistory { get => _headlineHistory; private set => Set(ref _headlineHistory, value); }

    // The part's page leads with its headline reading in a large graph (the way Task Manager's
    // Performance page does), with its scale written at the graph's edge.
    private string _heroValue = "--", _heroUnit = "", _heroLabel = "", _heroTop = "", _heroBottom = "";
    public string HeroValue  { get => _heroValue;  private set => Set(ref _heroValue, value); }
    public string HeroUnit   { get => _heroUnit;   private set => Set(ref _heroUnit, value); }
    public string HeroLabel  { get => _heroLabel;  private set => Set(ref _heroLabel, value); }
    private bool _hasHero;
    public bool HasHero { get => _hasHero; private set => Set(ref _hasHero, value); }
    public string HeroTop    { get => _heroTop;    private set => Set(ref _heroTop, value); }
    public string HeroBottom { get => _heroBottom; private set => Set(ref _heroBottom, value); }
    private double _heroMin = double.NaN, _heroMax = double.NaN;
    /// The graph's fixed range, or NaN for the reading's own lowest and highest.
    public double HeroMin { get => _heroMin; private set => Set(ref _heroMin, value); }
    public double HeroMax { get => _heroMax; private set => Set(ref _heroMax, value); }

    /// A scale label: whole numbers from 10 up, one decimal below.
    private static string Scale(double v) => Math.Abs(v) >= 10 ? v.ToString("0", System.Globalization.CultureInfo.CurrentCulture)
                                                               : v.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);

    private bool _asleep;
    /// A graphics card asleep and left alone: no values, and the part says so.
    public bool Asleep { get => _asleep; private set => Set(ref _asleep, value); }

    private bool _isActiveGpu;
    /// The graphics adapter Pulse's GPU readings come from.
    public bool IsActiveGpu { get => _isActiveGpu; private set => Set(ref _isActiveGpu, value); }

    public ObservableCollection<PulseReading> Readings { get; } = new();
    public ObservableCollection<SensorGroup> Nodes { get; } = new();

    private bool _hasReadings, _hasSensors, _manyNodes;
    public bool HasReadings { get => _hasReadings; private set => Set(ref _hasReadings, value); }
    public bool HasSensors  { get => _hasSensors;  private set => Set(ref _hasSensors, value); }
    /// More than one piece of hardware (two drives): each gets its name over its sensors.
    public bool ManyNodes   { get => _manyNodes;   private set => Set(ref _manyNodes, value); }

    private IReadOnlyList<KeyRow> _keyRows = Array.Empty<KeyRow>();
    /// The three or so numbers the overview card shows.
    public IReadOnlyList<KeyRow> KeyRows { get => _keyRows; private set => Set(ref _keyRows, value); }

    private string _more = "";
    public string More { get => _more; private set => Set(ref _more, value); }

    private bool _isSelected, _isVisible = true, _anyReadingShown = true;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsVisible  { get => _isVisible;  private set => Set(ref _isVisible, value); }
    public bool AnyReadingShown { get => _anyReadingShown; private set => Set(ref _anyReadingShown, value); }

    public void Update(List<SensorGroup> nodes, List<PulseReading> readings, bool isActiveGpu)
    {
        Sync(Nodes, nodes);
        Sync(Readings, readings);
        IsActiveGpu = isActiveGpu;
        HasReadings = Readings.Count > 0;
        HasSensors  = Nodes.Any(n => n.Rows.Count > 0);
        ManyNodes   = Nodes.Count > 1;
        Asleep      = Kind == PartKind.Gpu && Nodes.Count > 0 && Nodes.All(n => n.Asleep);
        if (Kind == PartKind.Gpu && Nodes.Count > 0) Title = ShortGpuName(Nodes[0].Name);
        if (Kind == PartKind.Gpu && Nodes.Count > 0)
            Accent = Brush(Nodes[0].Type switch { "GpuIntel" => "#60A5FA", "GpuAmd" => "#F87171", _ => "#34D399" });

        Model = Kind switch
        {
            PartKind.Memory => HardwareService.Instance.TotalRamGb > 0 ? $"{HardwareService.Instance.TotalRamGb:0.#} GB installed" : "",
            PartKind.Games  => "Frame rate of the game you are playing",
            PartKind.Gpu    => (Nodes.Count > 0 ? Nodes[0].Name + " · " : "")
                             + (Asleep ? "asleep, so Pulse leaves it alone"
                                : IsActiveGpu ? "your overlay reads this one" : "not the one your overlay shows"),
            _               => string.Join(" · ", Nodes.Select(n => n.Name).Distinct()),
        };

        int count = Nodes.Sum(n => n.Rows.Count);
        More = count > 0 ? $"{count} sensors" : "";
        Refresh();
    }

    /// The headline and the overview's numbers, from what the readings and sensors say now.
    public void Refresh()
    {
        PulseReading? R(string id) => Readings.FirstOrDefault(r => r.Id == id);
        SensorLine? Row(Func<SensorLine, bool> pick) => Nodes.SelectMany(n => n.Rows).FirstOrDefault(pick);

        PulseReading? headReading = null;
        SensorLine? headRow = null;
        switch (Kind)
        {
            case PartKind.Cpu:
                headReading = R("cpu_temp") is { HasValue: true } t ? t : R("cpu_usage");
                break;
            case PartKind.Gpu:
                // The adapter the overlay reads says what the overlay's GPU Temp says.
                if (IsActiveGpu && R("gpu_temp") is { HasValue: true } g) { headReading = g; break; }
                headRow = Row(r => r.Kind == "Temperature" && r.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                       ?? Row(r => r.Kind == "Temperature") ?? Row(r => r.Kind == "Load");
                break;
            case PartKind.Memory:
                headRow = Row(r => r.Id == "/ram/load");
                break;
            case PartKind.Drives:  headReading = R("disk_activity"); break;
            case PartKind.Network: headReading = R("net_download"); break;
            case PartKind.Battery:
                headReading = R("battery");
                if (headReading is not { HasValue: true }) { headRow = Row(r => r.Kind == "Level"); if (headRow != null) headReading = null; }
                break;
            case PartKind.Games:   headReading = R("fps"); break;
        }

        string value = "--", unit = "", label = "";
        IReadOnlyList<float> history = Array.Empty<float>();
        if (Asleep) value = "asleep";
        else if (headReading != null) { value = headReading.Value; unit = headReading.Unit; label = headReading.Label; history = headReading.History; }
        else if (headRow != null)     { value = headRow.Now; unit = headRow.Unit; label = headRow.Name; history = headRow.History; }
        Headline = Join(value, unit);
        HeadlineHistory = history;
        HeroValue = value;
        HeroUnit = value is "--" or "asleep" ? "" : unit;
        HeroLabel = label;
        HasHero = label.Length > 0 && !Asleep;

        // The graph's range: a percentage is always 0 to 100 and a temperature starts at 20 and
        // reaches at least 100, so the line's height means the same thing every time it is seen.
        // Any other amount starts at 0 and reaches its highest of the minute, so a network
        // that is all but idle draws a line along the floor, not its noise blown up to full height.
        (double lo, double hi) range = unit switch
        {
            "%"  => (0, 100),
            "°C" => (20, Math.Max(100, history.Count > 0 ? Math.Ceiling(history.Max() / 10) * 10 : 100)),
            _    => (0, double.NaN),
        };
        HeroMin = range.lo;
        HeroMax = range.hi;
        if (history.Count == 0) { HeroTop = HeroBottom = ""; }
        else
        {
            HeroTop    = Join(Scale(double.IsNaN(range.hi) ? history.Max() : Math.Max(range.hi, history.Max())), unit);
            HeroBottom = Join(Scale(range.lo), unit);
        }

        // The overview card: Pulse's own readings first, then the part's other sensors.
        var rows = new List<KeyRow>();
        foreach (var r in Readings)
        {
            if (rows.Count == 3) break;
            if (r == headReading || r.Id == "cpu_cores" || !r.IsAvailable) continue;
            rows.Add(new KeyRow(r.Label, Join(r.Value, r.Unit)));
        }
        if (!Asleep)
        {
            foreach (var type in new[] { "Load", "Clock", "Temperature", "Power", "SmallData", "Data", "Throughput", "Level" })
                foreach (var node in Nodes)
                    foreach (var line in node.Rows.Where(l => l.Kind == type && l != headRow && l.Reading is null))
                    {
                        if (rows.Count == 3) break;
                        string name = ManyNodes ? node.ShortName + " · " + line.Name : line.Name;
                        if (rows.Any(k => k.Label == name)) continue;
                        rows.Add(new KeyRow(name, Join(line.Now, line.Unit)));
                    }
        }
        // Replaced only when a figure changed: a new list rebuilds the card's three figures.
        if (!rows.SequenceEqual(KeyRows)) KeyRows = rows;
    }

    public void Filter(string words)
    {
        bool all = words.Length == 0 || Title.Contains(words, StringComparison.OrdinalIgnoreCase);
        bool any = false;
        foreach (var r in Readings)
        {
            r.IsVisible = all || r.Label.Contains(words, StringComparison.OrdinalIgnoreCase);
            any |= r.IsVisible;
        }
        AnyReadingShown = any;
        foreach (var n in Nodes)
        {
            n.Filter(all ? "" : words);
            any |= n.IsVisible;
        }
        IsVisible = all || any;
    }

    /// "57" and "°C" as "57°C"; "3.57" and "GHz" as "3.57 GHz"; a missing value stays as it is.
    public static string Join(string value, string unit)
    {
        if (value is "--" or "asleep" || unit.Length == 0) return value;
        return unit is "%" or "°C" or "°F" ? value + unit : value + " " + unit;
    }

    /// "NVIDIA GeForce RTX 3050 6GB Laptop GPU" as "RTX 3050 6GB", short enough for the sidebar;
    /// "Intel(R) UHD Graphics" as "Intel UHD Graphics". The full name is under it on its page.
    public static string ShortGpuName(string name)
    {
        var s = name.Replace("(R)", "").Replace("(TM)", "").Replace("NVIDIA ", "").Replace("GeForce ", "");
        if (s.EndsWith(" GPU", StringComparison.Ordinal)) s = s[..^4];
        if (s.EndsWith(" Laptop", StringComparison.Ordinal)) s = s[..^7];
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Sync<T>(ObservableCollection<T> target, List<T> wanted)
    {
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public enum PartKind { Cpu, Gpu, Memory, Drives, Network, Battery, Games }

/// One line of an overview card.
public sealed record KeyRow(string Label, string Value);

/// <summary>
/// The last minute of one reading, for the small graphs, oldest first.
/// </summary>
/// <remarks>
/// Kept by time, not by count: Pulse reads every half second to every minute (2 s unless
/// changed), and sixty readings were two minutes at the default while the window said a minute.
/// </remarks>
public sealed class ReadingHistory
{
    public const long SpanMs = 60_000;
    private readonly Queue<(long At, float Value)> _points = new();

    /// A missing value is skipped rather than drawn as zero.
    public void Add(float? value)
    {
        long now = Environment.TickCount64;
        if (value is { } v && float.IsFinite(v)) _points.Enqueue((now, v));
        while (_points.Count > 0 && now - _points.Peek().At > SpanMs) _points.Dequeue();
    }

    public float[] Snapshot()
    {
        var a = new float[_points.Count];
        int i = 0;
        foreach (var p in _points) a[i++] = p.Value;
        return a;
    }
}

/// <summary>
/// One of Pulse's readings in the Sensors window: its value now, its last minute, and its two
/// switches.
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
    public bool IsAdvanced => Overlay.Definition.IsAdvanced;
    public string Unit => Overlay.DisplayUnit;

    /// The graph's range: 0 to 100 for a percentage, from 20 to at least 100 for a temperature,
    /// otherwise from 0 to the minute's highest (NaN: as high as it went).
    public double GraphMin => Unit switch { "%" => 0, "°C" => 20, _ => 0 };
    public double GraphMax => Unit switch { "%" => 100, "°C" => 100, _ => double.NaN };

    /// Whether the in-game list has this reading, so it can have an In game switch.
    public bool HasInGame => InGame != null;

    /// Whether this PC can give this reading; when not, the card says why instead of a switch.
    public bool IsAvailable => Overlay.IsAvailable;
    public string Unavailable => Overlay.StatusHint;

    private string _value = "--";
    public string Value { get => _value; private set => Set(ref _value, value); }

    public bool HasValue => _value is not ("--" or "asleep");

    private static readonly SolidColorBrush Calm = Frozen(0xEC, 0xEC, 0xF1), Warm = Frozen(0xFB, 0xBF, 0x24), Hot = Frozen(0xF8, 0x71, 0x71);
    private SolidColorBrush _valueBrush = Calm;
    /// White, amber or red, by the same thresholds the overlay colours this tile by.
    public SolidColorBrush ValueBrush { get => _valueBrush; private set => Set(ref _valueBrush, value); }

    private readonly ReadingHistory _history = new();
    private IReadOnlyList<float> _historyNow = Array.Empty<float>();
    public IReadOnlyList<float> History { get => _historyNow; private set => Set(ref _historyNow, value); }

    /// CPU Cores: each core's load, drawn as bars in place of a value and a graph.
    public bool IsCores => Id == "cpu_cores";
    private IReadOnlyList<float>? _coreLoads;
    public IReadOnlyList<float>? CoreLoads { get => _coreLoads; private set => Set(ref _coreLoads, value); }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    public void ShowCores(float[]? loads)
    {
        CoreLoads = loads is { Length: > 0 } ? loads : null;
        Value = loads is { Length: > 0 } ? $"{loads.Length} cores" : "--";
        OnPropertyChanged(nameof(HasValue));
    }

    /// <summary>
    /// Written the way the overlay writes it, by the shared core. Returns whether the value
    /// shown changed. <paramref name="record"/> adds a point to the graph.
    /// </summary>
    public bool Show(float? value, bool asleep, bool record = true)
    {
        string before = Value;
        double capacity = Id switch
        {
            "ram_used" => HardwareService.Instance.TotalRamGb,
            "gpu_vram" => HardwareService.Instance.TotalVramGb,
            _          => 0,
        };

        if (!value.HasValue) Value = asleep ? "asleep" : "--";
        else
        {
            var settings = SettingsService.Instance.Settings;
            Value = TileReading.FormatValue(Overlay.Definition, value.Value, settings.NetworkUnitBits, false, capacity);
        }

        ValueBrush = TileReading.State(Overlay.Definition, value, capacity) switch { 3 => Hot, 2 => Warm, _ => Calm };
        if (record)
        {
            _history.Add(value);
            History = _history.Snapshot();
        }
        if (before == Value) return false;
        OnPropertyChanged(nameof(HasValue));
        return true;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// One piece of hardware in the Sensors window and its sensors.
/// </summary>
public sealed class SensorGroup : BaseViewModel
{
    public string Id { get; }
    public SensorGroup(string id) { Id = id; }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    /// The sensor library's kind of hardware ("Cpu", "GpuNvidia", "Storage").
    public string Type { get; private set; } = "";

    /// The name without its maker's marks, for a label shared with a sensor's name.
    public string ShortName => Name.Length > 22 ? Name[..20].TrimEnd() + "…" : Name;

    private bool _asleep;
    /// A graphics card asleep and left alone: no values, and the group says so.
    public bool Asleep { get => _asleep; private set => Set(ref _asleep, value); }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }

    public ObservableCollection<SensorLine> Rows { get; } = new();
    private readonly Dictionary<string, SensorLine> _rows = new(StringComparer.Ordinal);

    /// <param name="readingOf">Pulse's reading for a tile id, for the rows a tile is read from.</param>
    public void Update(SensorNode node, Func<string?, PulseReading?> readingOf)
    {
        Name   = node.Name;
        Type   = node.Type;
        Asleep = node.Asleep;
        OnPropertyChanged(nameof(ShortName));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        foreach (var sensor in node.Sensors)
        {
            seen.Add(sensor.Id);
            if (!_rows.TryGetValue(sensor.Id, out var row))
            {
                row = new SensorLine(sensor.Id);
                _rows[sensor.Id] = row;
                Rows.Add(row);
                changed = true;
            }
            row.Update(sensor, readingOf(sensor.Tile));
        }

        // Asleep, a card sends no sensors: its rows go rather than stand there with old values.
        foreach (var gone in Rows.Where(r => !seen.Contains(r.Id)).ToList())
        {
            Rows.Remove(gone);
            _rows.Remove(gone.Id);
            changed = true;
        }

        if (changed) BuildSections();
    }

    /// The sensors grouped by kind, temperatures first, each group with a caption, rather than in
    /// the library's order, which mixes clocks and temperatures.
    public ObservableCollection<SensorSection> Sections { get; } = new();

    private static readonly (string Type, string Title)[] KindOrder =
    {
        ("Temperature", "Temperatures"), ("Load", "Load"), ("Clock", "Clocks"), ("Power", "Power"),
        ("Voltage", "Voltages"), ("Current", "Current"), ("Fan", "Fans"), ("Control", "Fan control"),
        ("Throughput", "Throughput"), ("SmallData", "Memory"), ("Data", "Data"), ("Level", "Levels"),
        ("Energy", "Energy"), ("Frequency", "Frequency"), ("Factor", "Factors"), ("TimeSpan", "Time"),
    };

    private void BuildSections()
    {
        Sections.Clear();
        var byType = Rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (type, title) in KindOrder)
            if (byType.Remove(type, out var rows)) Sections.Add(new SensorSection(title, rows));
        foreach (var (type, rows) in byType) Sections.Add(new SensorSection(type, rows));
    }

    public void Filter(string words)
    {
        bool groupMatches = words.Length == 0 || Name.Contains(words, StringComparison.OrdinalIgnoreCase);
        bool any = false;
        foreach (var r in Rows)
        {
            // By name or by kind: "clock" finds every clock, whatever its sensor is called.
            r.IsVisible = groupMatches || r.Name.Contains(words, StringComparison.OrdinalIgnoreCase)
                                       || r.Kind.Contains(words, StringComparison.OrdinalIgnoreCase);
            any |= r.IsVisible;
        }
        foreach (var s in Sections) s.Refresh();
        IsVisible = groupMatches || any;
    }
}

/// One kind of sensor on one piece of hardware: its caption and its rows.
public sealed class SensorSection : BaseViewModel
{
    public string Title { get; }
    public IReadOnlyList<SensorLine> Rows { get; }

    public SensorSection(string title, IReadOnlyList<SensorLine> rows)
    {
        Title = title;
        Rows = rows;
    }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }

    public void Refresh() => IsVisible = Rows.Any(r => r.IsVisible);
}

/// <summary>
/// One sensor: now, lowest and highest since the window opened, its last minute, and the reading
/// of Pulse's it feeds, if any, whose switches it then carries.
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

    /// "Pulse: CPU Temp" style tag: which of Pulse's readings this sensor is, if any.
    private string _tile = "";
    public string Tile { get => _tile; private set => Set(ref _tile, value); }

    private PulseReading? _reading;
    /// Pulse's reading this sensor is the source of, for the row's switches.
    public PulseReading? Reading { get => _reading; private set => Set(ref _reading, value); }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    private readonly ReadingHistory _history = new();
    private IReadOnlyList<float> _historyNow = Array.Empty<float>();
    public IReadOnlyList<float> History { get => _historyNow; private set => Set(ref _historyNow, value); }

    private float? _min, _max;
    private string _type = "";

    /// The kind of reading, as the sensor library names it ("Temperature", "Clock", "Load").
    public string Kind => _type;

    public void Update(SensorRow sensor, PulseReading? reading)
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
        _history.Add(sensor.Value);
        History = _history.Snapshot();

        Reading = reading;
        Tile = reading is null ? "" : "Pulse: " + reading.Label;
    }
}
