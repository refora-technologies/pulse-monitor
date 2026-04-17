namespace Pulse.Models;

public class SensorTileDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public SensorCategory Category { get; set; }
    public bool HasBar { get; set; }
    public float BarMax { get; set; } = 100f;
    public float WarnThreshold { get; set; }
    public float DangerThreshold { get; set; }

    public static readonly List<SensorTileDefinition> All = new()
    {
        new() { Id = "cpu_temp",   Label = "CPU Temp",    Unit = "°C",  Category = SensorCategory.CPU,    HasBar = false, WarnThreshold = 75,  DangerThreshold = 90  },
        new() { Id = "cpu_power",  Label = "CPU Power",   Unit = "W",   Category = SensorCategory.CPU,    HasBar = false, WarnThreshold = 65,  DangerThreshold = 95  },
        new() { Id = "cpu_clock",  Label = "CPU Clock",   Unit = "GHz", Category = SensorCategory.CPU,    HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
        new() { Id = "cpu_usage",  Label = "CPU Usage",   Unit = "%",   Category = SensorCategory.CPU,    HasBar = true,  BarMax = 100, WarnThreshold = 70,  DangerThreshold = 90 },
        new() { Id = "gpu_temp",   Label = "GPU Temp",    Unit = "°C",  Category = SensorCategory.GPU,    HasBar = false, WarnThreshold = 75,  DangerThreshold = 90  },
        new() { Id = "gpu_power",  Label = "GPU TDP",     Unit = "W",   Category = SensorCategory.GPU,    HasBar = false, WarnThreshold = 80,  DangerThreshold = 115 },
        new() { Id = "gpu_clock",  Label = "GPU Clock",   Unit = "MHz", Category = SensorCategory.GPU,    HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
        new() { Id = "gpu_vram",   Label = "VRAM Used",   Unit = "GB",  Category = SensorCategory.GPU,    HasBar = true,  BarMax = 6,   WarnThreshold = 4.5f, DangerThreshold = 5.5f },
        new() { Id = "gpu_usage",  Label = "GPU Usage",   Unit = "%",   Category = SensorCategory.GPU,    HasBar = true,  BarMax = 100, WarnThreshold = 70,  DangerThreshold = 95 },
        new() { Id = "ram_used",   Label = "RAM Used",    Unit = "GB",  Category = SensorCategory.Memory, HasBar = true,  BarMax = 16,  WarnThreshold = 12,  DangerThreshold = 14.5f },
        new() { Id = "sys_power",  Label = "Total Power", Unit = "W",   Category = SensorCategory.System, HasBar = false, WarnThreshold = 100, DangerThreshold = 160 },
    };
}
