namespace Pulse.Models;

/// <summary>
/// Whether a tile can show a real reading on this machine, and if not, why not.
/// </summary>
/// <remarks>
/// Decided from facts about the machine, never from a missing reading. Readings go missing all
/// the time for ordinary reasons: a graphics card asleep, sensors still opening at logon, a
/// host being replaced. Disabling a tile on any of those would make tiles flicker off and on
/// at every wake and every logon. So a tile is only unavailable for a reason that will still
/// be true a minute from now.
/// </remarks>
public enum TileStatus
{
    Ready,

    /// The reading comes from the processor's own registers, which need the PawnIO driver, and
    /// it is not installed. The user can fix this from the tile.
    NeedsDriver,

    /// The driver is installed but Windows is not running it. Usually a restart after installing,
    /// or something on the machine refusing to let it load.
    DriverNotLoading,

    /// The hardware is not there, so nothing can make the tile work. A desktop has no battery.
    NotOnThisPc,
}

public enum DriverState
{
    NotInstalled,
    Running,
    NotRunning,
}

/// The facts a tile's availability is decided from. Gathered by SensorDriver.
public sealed record MachineFacts(DriverState Driver, bool HasBattery);

public static class TileAvailability
{
    /// <summary>
    /// The tiles whose only source is the PawnIO driver.
    /// </summary>
    /// <remarks>
    /// Measured on 30 September 2026 by listing every sensor the library could read with and
    /// without the driver: processor temperature, processor power and core clocks disappear
    /// without it. Power does not even disappear cleanly. It reads 0.0 W, which is why these
    /// tiles are switched off rather than left to show what they receive.
    ///
    /// CPU+GPU Power is here because it is built on processor power and means nothing without it.
    /// GPU Temp is not, even though on an AMD APU it comes from the processor too: Windows has
    /// its own graphics temperature, which Pulse falls back to, so that tile is decided by what
    /// arrives rather than switched off in advance.
    /// </remarks>
    public static readonly IReadOnlySet<string> DriverTiles =
        new HashSet<string>(StringComparer.Ordinal) { "cpu_temp", "cpu_power", "cpu_clock", "sys_power" };

    public static TileStatus Evaluate(string tileId, MachineFacts facts)
    {
        if (DriverTiles.Contains(tileId))
        {
            return facts.Driver switch
            {
                DriverState.NotInstalled => TileStatus.NeedsDriver,
                DriverState.NotRunning   => TileStatus.DriverNotLoading,
                _                        => TileStatus.Ready,
            };
        }

        if (tileId == "battery" && !facts.HasBattery)
            return TileStatus.NotOnThisPc;

        return TileStatus.Ready;
    }
}
