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
    /// What this reading is, in a sentence or two, shown as a tooltip in the settings list.
    /// </summary>
    /// <remarks>
    /// Every tile has one. They used to be optional, and the twelve without a description fell
    /// back to showing a keyboard hint instead, so hovering most of the list explained nothing
    /// about the reading under the cursor. The hint is said once, under the section, where the
    /// other instructions already are.
    ///
    /// These describe what Pulse measures and nothing else. Naming other tools invites the
    /// reader to go and compare, and the tooltip cannot keep up with what those tools do.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// True for the tiles that show a network speed, which is the only reading the user can
    /// choose the unit of.
    /// </summary>
    /// Decided from the catalogue rather than by listing the two ids, so a third network speed
    /// tile added later is carried along instead of quietly keeping one unit.
    public bool IsNetworkSpeed => Category == SensorCategory.Network && Unit == NetworkUnits.Bytes;

    /// <summary>
    /// True when a low reading is the bad one, rather than a high one.
    /// </summary>
    /// Battery only. Everything else Pulse shows counts upward into trouble: hotter, busier,
    /// fuller. The thresholds are read the same way round for both, so this decides which
    /// direction crossing one means.
    public bool LowerIsWorse { get; set; }

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
    ///   FPS   | 1% Low      frame rate and its worst case belong together
    ///   Up    | Down        network pairs naturally
    ///   Disk  | CPU+GPU     what is left, and the summary total
    ///
    /// Users can drag tiles into any order they prefer; this is only the starting point.
    /// </summary>
    public static readonly List<SensorTileDefinition> All = new()
    {
        new() { Id = "cpu_usage",    Label = "CPU Usage",     Unit = "%",    Category = SensorCategory.CPU,     HasBar = true,  BarMax = 100, WarnThreshold = 70,   DangerThreshold = 90,
                Description = "How much of the processor is being used, across every core." },
        new() { Id = "cpu_temp",     Label = "CPU Temp",      Unit = "°C",   Category = SensorCategory.CPU,     HasBar = false, WarnThreshold = 75,  DangerThreshold = 90,
                Description = "The processor's temperature, from its own sensor." },
        new() { Id = "cpu_clock",    Label = "CPU Clock",     Unit = "GHz",  Category = SensorCategory.CPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "The average speed the processor's cores are running at." },
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
        new() { Id = "cpu_power",    Label = "CPU Power",     Unit = "W",    Category = SensorCategory.CPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "How much power the processor is drawing right now." },

        new() { Id = "gpu_usage",    Label = "GPU Usage",     Unit = "%",    Category = SensorCategory.GPU,     HasBar = true,  BarMax = 100, WarnThreshold = 70,   DangerThreshold = 95,
                Description = "How busy the graphics chip is, as the graphics driver reports it." },
        new() { Id = "gpu_temp",     Label = "GPU Temp",      Unit = "°C",   Category = SensorCategory.GPU,     HasBar = false, WarnThreshold = 75,  DangerThreshold = 90,
                Description = "The graphics chip's temperature. Where the graphics are built into the "
                            + "processor the two share one piece of silicon, and the tile is renamed to say "
                            + "so." },
        new() { Id = "gpu_clock",    Label = "GPU Clock",     Unit = "MHz",  Category = SensorCategory.GPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "The speed the graphics chip is running at." },
        // "GPU Power", not "GPU TDP". TDP is a fixed rating of the card; this sensor is what
        // it is drawing right now, which is a different thing and confused at least one user.
        new() { Id = "gpu_power",    Label = "GPU Power",     Unit = "W",    Category = SensorCategory.GPU,     HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "How much power the graphics card is drawing right now." },
        new() { Id = "gpu_vram",     Label = "VRAM Used",     Unit = "GB",   Category = SensorCategory.GPU,     HasBar = true,  BarMax = 6,   WarnThreshold = 4.5f, DangerThreshold = 5.5f, HasKnownMax = true,
                Description = "Video memory in use, out of what the card has. Graphics built into the "
                            + "processor borrow system memory instead, and the tile is renamed to say so." },
        new() { Id = "ram_used",     Label = "RAM Used",      Unit = "GB",   Category = SensorCategory.Memory,  HasBar = true,  BarMax = 16,  WarnThreshold = 12,   DangerThreshold = 14.5f, HasKnownMax = true,
                Description = "System memory in use, out of what is installed." },

        new() { Id = "fps",          Label = "FPS",           Unit = "fps",  Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                // "Presented", not "produced". PresentMon measures the gap between an app
                // handing finished frames to Windows, which is what every overlay calls FPS
                // and is not quite the same as frames the card has drawn or the monitor has
                // shown. The tile should say the thing it measures.
                Description = "Frames presented in the last second by the app in focus." },

        // One 1% low, computed the way the rest of the world computes it: the frame time at the
        // 99th percentile. Pulse briefly offered a second tile alongside this, the average of
        // everything past the boundary, which is NVIDIA's convention. Both were correct and
        // having both was not: two rows whose names differed by three characters, and nothing
        // to tell anyone which to believe. The id is unchanged from when this was the average,
        // so nobody loses a tile they had already turned on.
        new() { Id = "fps_1low",     Label = "1% Low",        Unit = "fps",  Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "How fast your slowest frames are, measured over the last 2,000 frames. A "
                            + "number well below your FPS means stutter, even when the average looks fine." },

        new() { Id = "net_upload",   Label = "Net Upload",    Unit = "MB/s", Category = SensorCategory.Network, HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "Data sent over your network connection each second." },
        new() { Id = "net_download", Label = "Net Download",  Unit = "MB/s", Category = SensorCategory.Network, HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "Data received over your network connection each second." },

        new() { Id = "disk_activity",Label = "Disk Activity", Unit = "%",    Category = SensorCategory.Storage, HasBar = true,  BarMax = 100, WarnThreshold = 70,   DangerThreshold = 90,
                Description = "How much of the time your drive is busy reading or writing." },

        // The one tile where a small number is the bad one, hence LowerIsWorse. Twenty percent
        // is the point Windows itself starts warning, and ten is where it suggests plugging in.
        //
        // Charge level only. The library offers seven more readings for a battery — capacity in
        // watt-hours, charge and discharge rates, voltage, wear, and an estimated time left —
        // and none of them earn a tile. The time estimate especially: it swings with whatever
        // the machine is doing, so it would read two hours and then forty minutes a moment
        // later, which is the kind of confident wrong number this project keeps removing.
        new() { Id = "battery",      Label = "Battery",       Unit = "%",    Category = SensorCategory.System,  HasBar = true,  BarMax = 100, WarnThreshold = 20,   DangerThreshold = 10, LowerIsWorse = true,
                Description = "Charge left in the battery. Amber below 20 percent and red below 10. A machine "
                            + "with no battery leaves the tile empty." },
        new() { Id = "sys_power",    Label = "CPU+GPU Power", Unit = "W",    Category = SensorCategory.System,  HasBar = false, WarnThreshold = 0,   DangerThreshold = 0,
                Description = "The processor and graphics power together. With a separate graphics card "
                            + "that is two readings added up. Where the graphics are built into the "
                            + "processor they are one chip drawing one amount, so this matches CPU Power." },
    };
}
