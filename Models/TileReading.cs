namespace Pulse.Models;

/// <summary>
/// How a tile shows a reading: the number as written, its colour, and how full its bar is.
/// </summary>
/// <remarks>
/// <para>
/// Part of the shared core (Core/Pulse.Core.csproj): no Windows, no interface, nothing but the
/// reading and the facts it is judged against, so the Windows app and the Linux one to come show
/// the same reading the same way. The Windows overlay's tile (ViewModels/OverlayViewModel.cs)
/// asks this and supplies what only it knows: the capacity of the hardware and the user's units.
/// </para>
/// <para>
/// Moved here unchanged from that tile on 18 Nov 2026; the reasons for each rule are kept with it.
/// </para>
/// </remarks>
public static class TileReading
{
    /// <summary>
    /// Warning and danger points for this tile.
    ///
    /// For capacity tiles these scale with the hardware actually fitted. The catalogue
    /// values assume 16 GB of RAM and 6 GB of VRAM, so on a 64 GB machine 14.5 GB used —
    /// under a quarter of it — was being coloured as dangerous, and on a 24 GB card the
    /// same nonsense at 5.5 GB. The ratios are taken from those defaults, roughly three
    /// quarters full for a warning and ninety percent for danger.
    /// </summary>
    /// <param name="capacity">The hardware's real capacity (total RAM or VRAM), 0 if unknown.</param>
    public static (float Warn, float Danger) Thresholds(SensorTileDefinition definition, double capacity)
    {
        if (!definition.HasKnownMax) return (definition.WarnThreshold, definition.DangerThreshold);

        // No capacity means no opinion. The catalogue thresholds are proportions of an
        // assumed 16 GB and 6 GB, so applying them to hardware whose size we do not know
        // is colouring by guesswork: a machine reporting no VRAM capacity would have shown
        // a warning at 4.5 GB of some total nobody had measured.
        if (capacity <= 0 || definition.BarMax <= 0) return (0, 0);

        double scale = capacity / definition.BarMax;
        return ((float)(definition.WarnThreshold * scale), (float)(definition.DangerThreshold * scale));
    }

    /// The colour state: 0 neutral, 1 normal, 2 warm, 3 hot.
    public static int State(SensorTileDefinition definition, float? value, double capacity)
    {
        if (!value.HasValue) return 0;

        var (warn, danger) = Thresholds(definition, capacity);
        if (danger == 0) return 0;

        // Battery is the one reading where a small number is the bad one. Every other tile
        // counts upward into trouble: hotter, busier, fuller. Colouring 8% the same calm
        // white as 80% would be the tile refusing to say the one thing it is for.
        if (definition.LowerIsWorse)
        {
            if (value <= danger) return 3;
            if (value <= warn)   return 2;
            return 1;
        }

        if (value >= danger) return 3;
        if (value >= warn)   return 2;
        return 1;
    }

    /// How full the tile's bar is, 0 to 1.
    public static double BarFraction(SensorTileDefinition definition, float? value, double capacity)
    {
        if (!value.HasValue || !definition.HasBar) return 0;

        // A capacity tile with no known capacity draws no bar. Falling back to the
        // catalogue figure filled the bar against an invented total, so a machine whose
        // video memory we could not measure still showed a confident half-full gauge.
        double max = definition.HasKnownMax ? capacity : definition.BarMax;
        if (max <= 0) return 0;

        return Math.Clamp((double)value.Value / max, 0, 1);
    }

    /// <summary>
    /// The number as the tile writes it.
    /// </summary>
    /// <param name="networkBits">The user chose megabits for network speed.</param>
    /// <param name="showMax">The user chose to see capacities beside the reading ("12.2 / 16").</param>
    public static string FormatValue(SensorTileDefinition definition, float value, bool networkBits, bool showMax, double capacity)
    {
        // Megabits, when asked for. Written where the number is formatted rather than
        // where it is measured: the reading itself stays in one unit, so nothing that
        // compares, stores or sends it has to know which the user prefers.
        if (definition.IsNetworkSpeed && networkBits)
            return NetworkUnits.Convert(value, bits: true).ToString("F1");

        var formatted = definition.Unit switch
        {
            "GHz"  => $"{value:F2}",
            "GB"   => $"{value:F1}",
            "%"    => $"{value:F0}",
            "MB/s" => $"{value:F2}",
            _      => $"{value:F0}"
        };

        if (definition.HasKnownMax && showMax)
        {
            // One decimal below a gigabyte. Integrated graphics can hold a few hundred
            // megabytes of dedicated memory, and rounding that to whole gigabytes printed
            // a capacity of "0".
            if (capacity >= 1) return $"{formatted} / {capacity:F0}";
            if (capacity >  0) return $"{formatted} / {capacity:F1}";
        }

        return formatted;
    }
}
