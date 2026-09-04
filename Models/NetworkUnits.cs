namespace Pulse.Models;

/// <summary>
/// The two ways a network speed can be written, and the conversion between them.
/// </summary>
/// <remarks>
/// Kept in one place because the choice has to reach four of them: the overlay, the compact
/// HUD, the tile chooser in the control panel, and the number itself. A unit label that agrees
/// with the value in three places out of four is worse than either unit on its own.
/// </remarks>
public static class NetworkUnits
{
    /// What the reading is measured and stored in.
    public const string Bytes = "MB/s";

    /// What it is shown as when the user asks for bits.
    public const string Bits = "Mbps";

    /// <summary>
    /// Megabytes per second to megabits per second.
    /// </summary>
    /// <remarks>
    /// Not eight, which is the answer everyone expects and the reason this constant is written
    /// out rather than inlined. The reading is bytes divided by 1,048,576, so it is really
    /// mebibytes, the same convention Windows Explorer uses when it says MB. Megabits, as Task
    /// Manager and every internet plan mean them, are decimal: a million bits, not 2^20 of
    /// them. So the factor is 1,048,576 x 8 / 1,000,000, and multiplying by eight instead
    /// would read about five percent low against the thing people are checking against.
    /// </remarks>
    public const float BytesToBits = 1_048_576f * 8f / 1_000_000f;

    /// The unit to print, for a tile that shows a network speed.
    public static string Label(bool bits) => bits ? Bits : Bytes;

    /// The value to print, given a reading in megabytes per second.
    public static float Convert(float megabytesPerSecond, bool bits) =>
        bits ? megabytesPerSecond * BytesToBits : megabytesPerSecond;
}
