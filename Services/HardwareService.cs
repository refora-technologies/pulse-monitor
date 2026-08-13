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

    private HardwareService()
    {
        _computer = new Computer
        {
            IsCpuEnabled         = true,
            IsGpuEnabled         = true,
            IsMemoryEnabled      = true,
            IsMotherboardEnabled = false,
            IsStorageEnabled     = true,
            IsNetworkEnabled     = true,
        };

        try { _computer.Open(); } catch { }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(PollingIntervalSeconds)
        };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();

        _ = PollAsync(); // immediate first read
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

        try
        {
            IHardware? primaryGpu = null;

            foreach (var hw in _computer.Hardware)
            {
                hw.Accept(_updateVisitor);
                ReadHardware(hw, data);
                if (IsGpu(hw.HardwareType)) primaryGpu = PreferGpu(primaryGpu, hw);

                foreach (var sub in hw.SubHardware)
                {
                    sub.Accept(_updateVisitor);
                    ReadHardware(sub, data);
                    if (IsGpu(sub.HardwareType)) primaryGpu = PreferGpu(primaryGpu, sub);
                }
            }

            // Read GPU fields from a single chosen device so temp/power/clock/usage never
            // get mixed across an iGPU and a dGPU on the same poll.
            if (primaryGpu != null) ReadGpu(primaryGpu, data);
        }
        catch { }

        data.SysPower = (data.CpuPower ?? 0) + (data.GpuPower ?? 0);
        if (data.SysPower == 0) data.SysPower = null;

        data.Fps = FpsService.Instance.CurrentFps;

        return data;
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// Prefers a discrete GPU (Nvidia/AMD) over an integrated one (Intel) when a laptop or
    /// desktop exposes both, so all GPU tiles consistently describe the same physical device.
    private static IHardware PreferGpu(IHardware? current, IHardware candidate)
    {
        if (current == null) return candidate;
        bool currentIsDiscrete   = current.HardwareType   != HardwareType.GpuIntel;
        bool candidateIsDiscrete = candidate.HardwareType != HardwareType.GpuIntel;
        return !currentIsDiscrete && candidateIsDiscrete ? candidate : current;
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
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            switch (s.SensorType)
            {
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
