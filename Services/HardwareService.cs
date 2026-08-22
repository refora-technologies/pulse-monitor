using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services;

public class SensorData
{
    public float? CpuTemp { get; set; }
    public float? CpuPower { get; set; }
    public float? CpuClock { get; set; }
    public float? CpuUsage { get; set; }
    public float? GpuTemp { get; set; }
    public float? GpuPower { get; set; }
    public float? GpuClock { get; set; }
    public float? GpuVram { get; set; }
    public float? GpuUsage { get; set; }
    public float? RamUsed { get; set; }
    public float? SysPower { get; set; }
    public float? NetUpload { get; set; }
    public float? NetDownload { get; set; }
    public float? DiskActivity { get; set; }
    public float? Fps { get; set; }
    public float TotalRamGb { get; set; }
    public float TotalVramGb { get; set; }

    public float? GetById(string id) => id switch
    {
        "cpu_temp"     => CpuTemp,
        "cpu_power"    => CpuPower,
        "cpu_clock"    => CpuClock,
        "cpu_usage"    => CpuUsage,
        "gpu_temp"     => GpuTemp,
        "gpu_power"    => GpuPower,
        "gpu_clock"    => GpuClock,
        "gpu_vram"     => GpuVram,
        "gpu_usage"    => GpuUsage,
        "ram_used"     => RamUsed,
        "sys_power"    => SysPower,
        "net_upload"   => NetUpload,
        "net_download" => NetDownload,
        "disk_activity"=> DiskActivity,
        "fps"          => Fps,
        _ => null
    };
}

/// A GPU Pulse can read from, as offered in the settings GPU picker.
public class GpuInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// True when the adapter has its own dedicated video memory.
    public bool IsDiscrete { get; init; }
}

public class HardwareService : IDisposable
{
    private static HardwareService? _instance;
    public static HardwareService Instance => _instance ??= new HardwareService();

    private readonly Computer _computer;
    private readonly DispatcherTimer _timer;
    private readonly UpdateVisitor _updateVisitor = new();
    private bool _isPolling;

    public SensorData Current { get; private set; } = new();
    public event EventHandler<SensorData>? SensorsUpdated;
    public double PollingIntervalSeconds { get; set; } = 2;

    public float TotalRamGb { get; private set; } = 16f;
    public float TotalVramGb { get; private set; } = 6f;

    /// Every GPU detected on this machine, for the settings picker.
    public IReadOnlyList<GpuInfo> AvailableGpus { get; private set; } = Array.Empty<GpuInfo>();

    /// Accumulates adapters seen this session, keyed by identifier. See PublishGpuList.
    private readonly Dictionary<string, GpuInfo> _seenGpus = new();

    /// Name of the GPU the GPU tiles are currently reading from.
    public string ActiveGpuName { get; private set; } = "";

    /// Raised when the set of detected GPUs changes (first poll, or an eGPU appearing).
    public event EventHandler? GpuListChanged;

    private HardwareService()
    {
        _computer = new Computer();
        ConfigureSubsystems(_computer, RequiredSubsystems());

        // Opening enumerates every device and loads the sensor driver, which takes long
        // enough to visibly stall the window if it runs inline. Do it in the background
        // and let polls skip until it's ready.
        _openTask = Task.Run(() => { try { _computer.Open(); } catch { } });

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(PollingIntervalSeconds)
        };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();

        // Reconfigure if the user turns a whole category of tiles on or off.
        SettingsService.Instance.SettingsChanged += (_, _) => SyncSubsystems();

        _ = PollAsync(); // immediate first read
    }

    private readonly Task _openTask;
    private readonly object _computerLock = new();
    private Subsystems _activeSubsystems;

    [Flags]
    private enum Subsystems
    {
        None = 0, Cpu = 1, Gpu = 2, Memory = 4, Storage = 8, Network = 16,
    }

    /// <summary>
    /// Works out which sensor groups are actually needed for the tiles in use.
    ///
    /// LibreHardwareMonitor updates every sensor in an enabled group on each poll, and
    /// that isn't cheap — reading the discrete GPU alone dominates a poll. Enabling only
    /// what the visible tiles require avoids paying for data nothing displays.
    /// </summary>
    private static Subsystems RequiredSubsystems()
    {
        var active = SettingsService.Instance.Settings.ActiveTileIds;
        var needed = Subsystems.None;

        foreach (var id in active)
        {
            needed |= id switch
            {
                "cpu_usage" or "cpu_temp" or "cpu_clock" or "cpu_power" => Subsystems.Cpu,
                "gpu_usage" or "gpu_temp" or "gpu_clock" or "gpu_power" or "gpu_vram" => Subsystems.Gpu,
                "ram_used"      => Subsystems.Memory,
                "disk_activity" => Subsystems.Storage,
                "net_upload" or "net_download" => Subsystems.Network,
                // Total power is derived from both chips, so it needs each of them.
                "sys_power"     => Subsystems.Cpu | Subsystems.Gpu,
                _               => Subsystems.None,
            };
        }

        // LibreHardwareMonitor only creates its Intel GPU group when CPU monitoring is on
        // (`if (_cpuEnabled) Add(new IntelGpuGroup(GetIntelCpus(), ...))`), because the
        // integrated GPU's sensors hang off the CPU package. Gating the CPU subsystem away
        // therefore removes every GPU reading on an Intel-iGPU-only machine — the user only
        // finds out after a restart. The CPU read costs ~14ms, which is worth paying.
        if (needed.HasFlag(Subsystems.Gpu)) needed |= Subsystems.Cpu;

        return needed;
    }

    private void ConfigureSubsystems(Computer computer, Subsystems s)
    {
        computer.IsCpuEnabled         = s.HasFlag(Subsystems.Cpu);
        computer.IsGpuEnabled         = s.HasFlag(Subsystems.Gpu);
        computer.IsMemoryEnabled      = s.HasFlag(Subsystems.Memory);
        computer.IsStorageEnabled     = s.HasFlag(Subsystems.Storage);
        computer.IsNetworkEnabled     = s.HasFlag(Subsystems.Network);
        computer.IsMotherboardEnabled = false;
        _activeSubsystems             = s;
    }

    /// Applies a change in which sensor groups are needed. Toggling the flags makes
    /// LibreHardwareMonitor add or drop that hardware, so no reopen is required.
    private void SyncSubsystems()
    {
        var needed = RequiredSubsystems();
        if (needed == _activeSubsystems) return;

        lock (_computerLock)
        {
            ConfigureSubsystems(_computer, needed);
        }
    }

    public void SetInterval(double seconds)
    {
        PollingIntervalSeconds = seconds;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Runs the sensor read on a background thread so a slow driver/device can't freeze the
    /// UI (this timer ticks on the dispatcher thread). The re-entrancy guard skips a tick if
    /// the previous read hasn't finished yet, instead of piling up overlapping reads.
    /// </summary>
    private async Task PollAsync()
    {
        if (_isPolling) return;
        if (!_openTask.IsCompleted) return;   // still enumerating hardware
        _isPolling = true;
        try
        {
            var data = await Task.Run(ReadAll);

            if (data.TotalRamGb > 0) TotalRamGb = data.TotalRamGb;
            if (data.TotalVramGb > 0) TotalVramGb = data.TotalVramGb;

            Current = data;
            SensorsUpdated?.Invoke(this, data); // resumes on the UI thread via the captured SynchronizationContext
        }
        finally
        {
            _isPolling = false;
        }
    }

    private SensorData ReadAll()
    {
        var data = new SensorData();
        bool gpuListChanged = false;

        // Held so a subsystem change can't alter the hardware list mid-enumeration.
        lock (_computerLock)
        try
        {
            var gpus = new List<IHardware>();

            foreach (var hw in _computer.Hardware)
            {
                // Accept recurses: UpdateVisitor.VisitHardware updates this device and then
                // visits its children, so the children must not be Accept'ed again below or
                // every subhardware sensor is read twice per poll.
                hw.Accept(_updateVisitor);
                ReadHardware(hw, data);
                if (IsGpu(hw.HardwareType)) gpus.Add(hw);

                foreach (var sub in hw.SubHardware)
                {
                    ReadHardware(sub, data);
                    if (IsGpu(sub.HardwareType)) gpus.Add(sub);
                }
            }

            gpuListChanged = PublishGpuList(gpus);

            // Read GPU fields from a single chosen device so temp/power/clock/usage never
            // get mixed across an iGPU and a dGPU on the same poll.
            var chosen = SelectGpu(gpus);
            if (chosen != null)
            {
                ActiveGpuName = chosen.Name;
                ReadGpu(chosen, data);
            }
        }
        catch { }

        // Raised only after the lock is released. Subscribers marshal to the UI thread, and
        // the UI thread takes _computerLock whenever the tile selection changes, so firing
        // this while still holding the lock deadlocks the two threads against each other.
        if (gpuListChanged) GpuListChanged?.Invoke(this, EventArgs.Empty);

        data.SysPower = (data.CpuPower ?? 0) + (data.GpuPower ?? 0);
        if (data.SysPower == 0) data.SysPower = null;

        data.Fps = FpsService.Instance.CurrentFps;

        return data;
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>
    /// Publishes the GPU picker list. Adapters are accumulated for the lifetime of the
    /// process rather than replaced each poll: LibreHardwareMonitor stops enumerating an
    /// integrated GPU entirely while a game has the discrete one active, so rebuilding
    /// from each poll would make the picker empty itself mid-session and reappear later.
    ///
    /// Returns whether the list changed. The caller raises GpuListChanged once it has let
    /// go of _computerLock; see ReadAll.
    /// </summary>
    private bool PublishGpuList(List<IHardware> gpus)
    {
        bool changed = false;

        foreach (var g in gpus)
        {
            var id = g.Identifier.ToString();
            if (_seenGpus.ContainsKey(id)) continue;

            _seenGpus[id] = new GpuInfo
            {
                Id         = id,
                Name       = g.Name,
                IsDiscrete = GetDedicatedVramMb(g) > 0,
            };
            changed = true;
        }

        if (!changed) return false;

        // Discrete first, so the picker reads in the order people expect.
        var list = new List<GpuInfo>(_seenGpus.Values);
        list.Sort((a, b) => b.IsDiscrete.CompareTo(a.IsDiscrete));

        AvailableGpus = list;
        return true;
    }

    /// <summary>
    /// Chooses which GPU every GPU tile reads from. An explicit user choice always wins.
    /// Otherwise we prefer the adapter with real dedicated video memory, because that is
    /// what actually distinguishes a discrete GPU from integrated graphics. Vendor alone
    /// is not a reliable signal: an AMD APU's Radeon iGPU reports as HardwareType.GpuAmd
    /// exactly like a discrete Radeon does, which previously caused Pulse to lock onto
    /// the integrated GPU on Ryzen laptops that also have an NVIDIA card.
    /// </summary>
    private static IHardware? SelectGpu(List<IHardware> gpus)
    {
        if (gpus.Count == 0) return null;

        var pinned = SettingsService.Instance.Settings.SelectedGpuId;
        if (!string.IsNullOrEmpty(pinned))
        {
            foreach (var g in gpus)
                if (g.Identifier.ToString() == pinned) return g;
            // Pinned GPU is gone (eGPU unplugged, driver change) — fall through to auto.
        }

        IHardware? best = null;
        float bestVram = -1f;
        int   bestVendor = -1;

        foreach (var g in gpus)
        {
            float vram   = GetDedicatedVramMb(g);
            int   vendor = VendorRank(g);

            if (vram > bestVram || (vram == bestVram && vendor > bestVendor))
            {
                best       = g;
                bestVram   = vram;
                bestVendor = vendor;
            }
        }

        return best;
    }

    /// Dedicated video memory in MB, or 0 for an adapter that only carves out of system
    /// RAM. Integrated graphics report shared memory only ("D3D Shared Memory *"), while
    /// a discrete card reports "GPU Memory Total" and/or "D3D Dedicated Memory Used".
    private static float GetDedicatedVramMb(IHardware gpu)
    {
        float total = 0f, dedicated = 0f;

        foreach (var s in gpu.Sensors)
        {
            if (s.SensorType != SensorType.SmallData || s.Value is null) continue;

            // Exact match so "D3D Shared Memory Total" can never be mistaken for this.
            if (s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                total = s.Value.Value;
            else if (s.Name.Contains("Dedicated Memory", StringComparison.OrdinalIgnoreCase))
                dedicated = MathF.Max(dedicated, s.Value.Value);
        }

        return total > 0f ? total : dedicated;
    }

    /// Tiebreaker only, used when two adapters report the same dedicated memory (usually
    /// when neither reports any). There are no integrated NVIDIA parts in this context.
    private static int VendorRank(IHardware gpu)
    {
        // LibreHardwareMonitor tags integrated adapters in the identifier itself,
        // e.g. "/gpu-intel-integrated/...". Trust that when it is present.
        if (gpu.Identifier.ToString().Contains("integrated", StringComparison.OrdinalIgnoreCase))
            return 0;

        return gpu.HardwareType switch
        {
            HardwareType.GpuNvidia => 3,
            HardwareType.GpuAmd    => 2,
            HardwareType.GpuIntel  => 1,
            _                      => 0,
        };
    }

    private static void ReadHardware(IHardware hw, SensorData data)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:
                ReadCpu(hw, data);
                break;
            case HardwareType.Memory:
                ReadMemory(hw, data);
                break;
            case HardwareType.Network:
                ReadNetwork(hw, data);
                break;
            case HardwareType.Storage:
                ReadStorage(hw, data);
                break;
        }
    }

    private static void ReadCpu(IHardware hw, SensorData data)
    {
        float clockSum = 0; int clockCount = 0;
        float usageSum = 0; int usageCount = 0;

        // Priority-based temp/power tracking for Intel + AMD compatibility
        // Intel: "CPU Package" (temp), "CPU Package" (power)
        // AMD:   "Core (Tctl/Tdie)" or "Tdie" (temp), "Package" or "PPT" (power)
        float? tempPackage = null, tempTctl = null, tempFallback = null;
        float? powerPackage = null, powerPpt = null, powerFallback = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        tempPackage = s.Value;
                    else if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                          || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                        tempTctl = s.Value;
                    else if (tempFallback is null && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        tempFallback = s.Value;
                    break;

                case SensorType.Power:
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        powerPackage = s.Value;
                    else if (s.Name.Contains("PPT", StringComparison.OrdinalIgnoreCase))
                        powerPpt = s.Value;
                    else if (powerFallback is null)
                        powerFallback = s.Value;
                    break;

                case SensorType.Clock when !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase):
                    clockSum += s.Value.Value; clockCount++;
                    break;

                case SensorType.Load when s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                    data.CpuUsage = s.Value;
                    break;

                case SensorType.Load:
                    usageSum += s.Value.Value; usageCount++;
                    break;
            }
        }

        // Apply priority: Package > Tctl/Tdie > any Core sensor
        data.CpuTemp  = tempPackage  ?? tempTctl  ?? tempFallback;
        data.CpuPower = powerPackage ?? powerPpt  ?? powerFallback;

        if (clockCount > 0 && data.CpuClock is null)
            data.CpuClock = MathF.Round(clockSum / clockCount / 1000f, 2);

        if (data.CpuUsage is null && usageCount > 0)
            data.CpuUsage = usageSum / usageCount;
    }

    private static void ReadGpu(IHardware hw, SensorData data)
    {
        // Intel integrated GPUs have no "GPU Core" load sensor — they report per-engine
        // D3D loads instead ("D3D 3D", "D3D Video Decode", ...). Fall back to the 3D
        // engine so GPU Usage isn't permanently blank when an iGPU is selected.
        float? d3dEngineLoad = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            switch (s.SensorType)
            {
                case SensorType.Load when s.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase)
                                       && d3dEngineLoad is null:
                    d3dEngineLoad = s.Value;
                    break;
                case SensorType.Temperature when data.GpuTemp is null:
                    data.GpuTemp = s.Value;
                    break;
                case SensorType.Power when data.GpuPower is null:
                    data.GpuPower = s.Value;
                    break;
                case SensorType.Clock when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && data.GpuClock is null:
                    data.GpuClock = s.Value;
                    break;
                case SensorType.SmallData when s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase):
                    data.GpuVram = MathF.Round(s.Value.Value / 1024f, 2);
                    break;
                case SensorType.SmallData when s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase) && data.TotalVramGb == 0:
                    data.TotalVramGb = MathF.Round(s.Value.Value / 1024f, 0);
                    break;
                case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && data.GpuUsage is null:
                    data.GpuUsage = s.Value;
                    break;
            }
        }

        data.GpuUsage ??= d3dEngineLoad;
    }

    private static void ReadMemory(IHardware hw, SensorData data)
    {
        float? used = null, available = null;
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.SensorType != SensorType.Data) continue;
            var name = s.Name;
            if (name.Contains("Used", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                used = s.Value.Value;
            else if (name.Contains("Available", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                available = s.Value.Value;
        }
        if (used.HasValue)
            data.RamUsed = MathF.Round(used.Value, 2);
        if (used.HasValue && available.HasValue)
            data.TotalRamGb = MathF.Round(used.Value + available.Value, 0);
    }

    private static void ReadNetwork(IHardware hw, SensorData data)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.SensorType != SensorType.Throughput) continue;
            if (s.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase))
                data.NetUpload = (data.NetUpload ?? 0) + s.Value.Value / 1_048_576f;
            else if (s.Name.Contains("Download", StringComparison.OrdinalIgnoreCase))
                data.NetDownload = (data.NetDownload ?? 0) + s.Value.Value / 1_048_576f;
        }
    }

    private static void ReadStorage(IHardware hw, SensorData data)
    {
        float? activity = null;
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.SensorType != SensorType.Load) continue;
            if (s.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
            {
                activity = s.Value.Value;
                break;
            }
            activity ??= s.Value.Value;
        }
        if (activity.HasValue && activity > (data.DiskActivity ?? -1f))
            data.DiskActivity = activity;
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _computer.Close(); } catch { }
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var s in hardware.SubHardware) s.Accept(this); }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
