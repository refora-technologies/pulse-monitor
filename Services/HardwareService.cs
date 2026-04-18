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

    public float? GetById(string id) => id switch
    {
        "cpu_temp"  => CpuTemp,
        "cpu_power" => CpuPower,
        "cpu_clock" => CpuClock,
        "cpu_usage" => CpuUsage,
        "gpu_temp"  => GpuTemp,
        "gpu_power" => GpuPower,
        "gpu_clock" => GpuClock,
        "gpu_vram"  => GpuVram,
        "gpu_usage" => GpuUsage,
        "ram_used"  => RamUsed,
        "sys_power" => SysPower,
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

    public SensorData Current { get; private set; } = new();
    public event EventHandler<SensorData>? SensorsUpdated;
    public double PollingIntervalSeconds { get; set; } = 2;

    private HardwareService()
    {
        _computer = new Computer
        {
            IsCpuEnabled        = true,
            IsGpuEnabled        = true,
            IsMemoryEnabled     = true,
            IsMotherboardEnabled = false,
            IsStorageEnabled    = false,
            IsNetworkEnabled    = false,
        };

        try { _computer.Open(); } catch { }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(PollingIntervalSeconds)
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        Poll(); // immediate first read
    }

    public void SetInterval(double seconds)
    {
        PollingIntervalSeconds = seconds;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
    }

    private void Poll()
    {
        var data = new SensorData();

        try
        {
            foreach (var hw in _computer.Hardware)
            {
                hw.Accept(_updateVisitor);
                ReadHardware(hw, data);
                foreach (var sub in hw.SubHardware)
                {
                    sub.Accept(_updateVisitor);
                    ReadHardware(sub, data);
                }
            }
        }
        catch { }

        // Derive total system power
        data.SysPower = (data.CpuPower ?? 0) + (data.GpuPower ?? 0);
        if (data.SysPower == 0) data.SysPower = null;

        Current = data;
        SensorsUpdated?.Invoke(this, data);
    }

    private static void ReadHardware(IHardware hw, SensorData data)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:
                ReadCpu(hw, data);
                break;
            case HardwareType.GpuNvidia:
            case HardwareType.GpuAmd:
            case HardwareType.GpuIntel:
                ReadGpu(hw, data);
                break;
            case HardwareType.Memory:
                ReadMemory(hw, data);
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
                case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && data.GpuUsage is null:
                    data.GpuUsage = s.Value;
                    break;
            }
        }
    }

    private static void ReadMemory(IHardware hw, SensorData data)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            // Only pick physical memory used — skip virtual/pagefile sensors
            if (s.SensorType == SensorType.Data
                && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase)
                && !s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            {
                data.RamUsed = MathF.Round(s.Value.Value, 2);
            }
        }
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
