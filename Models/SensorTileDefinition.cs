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

    /// <summary>
    /// What this reading actually is, shown as a tooltip in the settings list.
    ///
    /// Added for the frame rate tiles, where the name alone is not enough. "1% Low" is used by
    /// different tools for two different statistics, and ours reads lower than MSI Afterburner
    /// on the same scene because it averages the worst frames rather than reporting the one on
    /// the boundary. Users reasonably concluded one of us was broken.
    /// </summary>
    /// Null rather than empty for the tiles that do not need one, so the settings list can
    /// fall back to its keyboard hint through TargetNullValue.
    public string? Description { get; set; }

    /// True only for tiles where Pulse knows a real hardware capacity (total RAM/VRAM),
    /// as opposed to warn/danger thresholds, which are just our own guessed defaults and
    /// would be misleading if shown as a "max".
    public bool HasKnownMax { get; set; }

    /// <summary>
    /// Catalog order, which is also the default overlay order for a fresh install.
    ///
    /// Both the settings list and the overlay lay tiles out two per row, so this is
    /// arranged in pairs that belong together rather than by strict category:
    ///
    ///   Usage | Temp        the two people watch most, per chip
    ///   Clock | Power
    ///   ...            CPU block, then the GPU block mirroring it
    ///   VRAM  | RAM         the two memory readings, side by side
    ///   FPS   | 1% Low Avg  frame rate and its worst case belong together
    ///   P1    | Displayed   the two alternative frame rate readings, both off by default
    ///   Up    | Down        network pairs naturally
    ///   Disk  | CPU+GPU     what is left, and the summary total
    ///
    /// Users can drag tiles into any order they prefer; this is only the starting point.
    /// </summary>
    public static readonly List<SensorTileDefinition> All = new()
    {
        new() { Id = "cpu_usage",    Label = "CPU Usage",     Unit = "%",    Category = SensorCategory.CPU,     HasBar = true,  BarMax = 100, WarnThreshold = 70,   DangerThreshold = 90 },
        new() { Id = "cpu_temp",     Label = "CPU Temp",      Unit = "°C",   Category = SensorCategory.CPU,     HasBar = false, WarnThreshold = 75,  DangerThreshold = 90  },
        new() { Id = "cpu_clock",    Label = "CPU Clock",     Unit = "GHz",  Category = SensorCategory.CPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
        // Power tiles carry no warning thresholds, deliberately.
        //
        // They used to: 65/95W for the CPU, 80/115W for the GPU, 100/160W for the pair. Those
        // are absolute watts, so a 400W desktop card sat permanently in danger and a 15W laptop
        // never left green. Capacity tiles solve this by scaling against the hardware actually
        // fitted, and power has nothing to scale against: the library reports what is being
        // drawn and never the limit it is drawn against. Checked against the shipped 0.9.6,
        // which exposes GPU Power, Core Power, Total Power and nvmlDeviceGetPowerUsage, and no
        // power limit sensor of any kind.
        //
        // Learning a ceiling from what a machine has drawn does not rescue it either. Power
        // near its own maximum is the normal state under load, unlike temperature, where
        // approaching a limit is the warning itself. A card whose real limit is 400W but which
        // only ever games at 250W would learn 250 and then colour ordinary play as dangerous.
        //
        // So the number is shown and no claim is made about it, the same as clocks and frame
        // rates. A colour we cannot justify is worse than no colour: it is the 6GB VRAM default
        // again, an invented figure presented as a judgement.
        new() { Id = "cpu_power",    Label = "CPU Power",     Unit = "W",    Category = SensorCategory.CPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },

        new() { Id = "gpu_usage",    Label = "GPU Usage",     Unit = "%",    Category = SensorCategory.GPU,     HasBar = true,  BarMax = 100, WarnThreshold = 70,   DangerThreshold = 95 },
        new() { Id = "gpu_temp",     Label = "GPU Temp",      Unit = "°C",   Category = SensorCategory.GPU,     HasBar = false, WarnThreshold = 75,  DangerThreshold = 90  },
        new() { Id = "gpu_clock",    Label = "GPU Clock",     Unit = "MHz",  Category = SensorCategory.GPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
        // "GPU Power", not "GPU TDP". TDP is a fixed rating of the card; this sensor is what
        // it is drawing right now, which is a different thing and confused at least one user.
        new() { Id = "gpu_power",    Label = "GPU Power",     Unit = "W",    Category = SensorCategory.GPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
        new() { Id = "gpu_vram",     Label = "VRAM Used",     Unit = "GB",   Category = SensorCategory.GPU,     HasBar = true,  BarMax = 6,   WarnThreshold = 4.5f, DangerThreshold = 5.5f, HasKnownMax = true },
        new() { Id = "ram_used",     Label = "RAM Used",      Unit = "GB",   Category = SensorCategory.Memory,  HasBar = true,  BarMax = 16,  WarnThreshold = 12,   DangerThreshold = 14.5f, HasKnownMax = true },

        new() { Id = "fps",          Label = "FPS",           Unit = "fps",  Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "Frames your graphics card produced in the last second, for whichever app is in focus." },

        // "Avg" is in the name on purpose. Two different statistics are called "1% low": the
        // average of the slowest frames, which is NVIDIA's, and the value at the 1% boundary,
        // which is RTSS and MSI Afterburner's. Ours always reads lower on the same scene
        // because a single deep stutter pulls an average down and does not move a boundary.
        // Without saying which one this is, the difference looks like a bug in Pulse.
        new() { Id = "fps_1low",     Label = "1% Low Avg",    Unit = "fps",  Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "The average of your slowest 1% of frames over the last minute. "
                            + "Lower than the 1% low shown by MSI Afterburner and RTSS, which report the "
                            + "frame on the boundary instead of averaging the worst ones. This one reacts "
                            + "to a single deep stutter; theirs does not." },

        // Both 1% lows are offered rather than one being declared correct, because they answer
        // different questions and people compare overlays side by side. Off by default: a fresh
        // install should not show two tiles whose names differ by three characters.
        new() { Id = "fps_1low_p1",  Label = "1% Low P1",     Unit = "fps",  Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "The same minute of frames measured the way MSI Afterburner, RTSS and "
                            + "CapFrameX measure it: the frame time at the 99th percentile, rather than "
                            + "the average of everything past it. Reads the same or higher than 1% Low Avg, "
                            + "never lower. Turn this on to compare Pulse against those tools." },

        // Also off by default. Most people want the rate their card is producing, which is what
        // FPS already shows; this one is for seeing how much of that the monitor never receives.
        new() { Id = "fps_displayed",Label = "Displayed FPS", Unit = "fps",  Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "Frames that actually reached your monitor, which your refresh rate caps. "
                            + "A 60Hz screen cannot show more than 60 however fast the game runs, so this "
                            + "sitting well below FPS is normal and means frames are being replaced before "
                            + "they are shown. With V-Sync on it should sit at your refresh rate." },

        new() { Id = "net_upload",   Label = "Net Upload",    Unit = "MB/s", Category = SensorCategory.Network, HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
        new() { Id = "net_download", Label = "Net Download",  Unit = "MB/s", Category = SensorCategory.Network, HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },

        new() { Id = "disk_activity",Label = "Disk Activity", Unit = "%",    Category = SensorCategory.Storage, HasBar = true,  BarMax = 100, WarnThreshold = 70,   DangerThreshold = 90 },
        new() { Id = "sys_power",    Label = "CPU+GPU Power", Unit = "W",    Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0   },
    };
}
