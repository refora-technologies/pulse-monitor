namespace Pulse.Models;

/// <summary>
/// How the Sensors page writes a raw sensor's value: the number and its unit, by the kind of
/// reading the sensor library says it is.
/// </summary>
/// <remarks>
/// Part of the shared core (Core/Pulse.Core.csproj), so the Linux version writes the same reading
/// the same way. Tiles are written by <see cref="TileReading"/>; this is for the rows that are not
/// tiles, which can be any kind the library knows. The units are the library's own: data in GB,
/// small data in MB, throughput in bytes a second, shown here as MB/s.
/// </remarks>
public static class SensorFormat
{
    /// The number and unit for a reading of this kind. "--" when there is no value.
    public static (string Value, string Unit) Format(string type, float? value)
    {
        string unit = Unit(type);
        if (value is not { } v || !float.IsFinite(v)) return ("--", unit);

        string text = type switch
        {
            "Temperature" => v.ToString("F1"),
            "Load" or "Control" or "Level" or "Humidity" => v.ToString("F1"),
            "Clock" or "Fan" or "Flow" or "Frequency" or "Energy" or "Noise" or "SmallData" => v.ToString("F0"),
            "Power" => v.ToString("F1"),
            "Voltage" or "Factor" => v.ToString("F3"),
            "Current" => v.ToString("F2"),
            "Data" => v.ToString("F1"),
            "Throughput" => (v / 1_048_576f).ToString("F2"),
            "TimeSpan" => TimeSpan.FromSeconds(Math.Max(0, v)).ToString(@"h\:mm\:ss"),
            _ => v.ToString("F1"),
        };
        return (text, unit);
    }

    /// The unit for a reading of this kind, or "" when it has none.
    public static string Unit(string type) => type switch
    {
        "Temperature" => "°C",
        "Load" or "Control" or "Level" or "Humidity" => "%",
        "Clock" => "MHz",
        "Power" => "W",
        "Voltage" => "V",
        "Current" => "A",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "MB/s",
        "Fan" => "RPM",
        "Flow" => "L/h",
        "Frequency" => "Hz",
        "Energy" => "mWh",
        "Noise" => "dBA",
        _ => "",
    };
}
