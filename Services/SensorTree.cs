using System.Globalization;
using Newtonsoft.Json;

namespace Pulse.Services;

/// <summary>
/// Everything the sensor host can see, for the Sensors page: each piece of hardware and every
/// sensor on it, as it was at one reading.
/// </summary>
/// <remarks>
/// <para>
/// Sent only while the Sensors page is open (SensorCommand.Tree), on a line of its own next to the
/// ordinary reading, so a Pulse without the page, or with it closed, pays nothing for it.
/// </para>
/// <para>
/// Short member names on the wire: this can be a few hundred sensors, once a poll.
/// </para>
/// </remarks>
public sealed class SensorTree
{
    [JsonProperty("hw")] public List<SensorNode> Hardware { get; set; } = new();
}

/// One piece of hardware: a processor, a graphics adapter, a drive, a battery.
public sealed class SensorNode
{
    [JsonProperty("id")]   public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";

    /// The sensor library's kind of hardware ("Cpu", "GpuNvidia", "Storage", ...), or "Memory"
    /// for the reading Pulse takes from Windows itself.
    [JsonProperty("type")] public string Type { get; set; } = "";

    /// A graphics card asleep and deliberately not read: its sensors are left out rather than sent
    /// with the values they had before it slept.
    [JsonProperty("asleep", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Asleep { get; set; }

    [JsonProperty("s")] public List<SensorRow> Sensors { get; set; } = new();
}

/// One sensor and its value at this reading.
public sealed class SensorRow
{
    [JsonProperty("id")]   public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";

    /// The sensor library's kind of reading ("Temperature", "Load", "Clock", ...).
    [JsonProperty("type")] public string Type { get; set; } = "";

    /// Null when the sensor has no value right now.
    [JsonProperty("v")] public float? Value { get; set; }

    /// The tile this sensor feeds, when it is the one a tile was read from at this reading.
    [JsonProperty("tile", NullValueHandling = NullValueHandling.Ignore)]
    public string? Tile { get; set; }
}

public static partial class SensorTreeLine
{
    /// <summary>
    /// The start of a tree line: this, a space, the tree as JSON.
    /// </summary>
    /// Not JSON as a whole, so the reading parser rejects it on its first character and a Pulse
    /// that does not know about trees ignores one, as with the diagnostics report.
    public const string Prefix = "#pulse-sensor-tree ";

    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting           = Formatting.None,
        NullValueHandling    = NullValueHandling.Ignore,
        Culture              = CultureInfo.InvariantCulture,
        FloatFormatHandling  = FloatFormatHandling.String,
        StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
    };

    /// The tree as one line, newline included, or null if it cannot be written.
    public static string? Write(SensorTree tree)
    {
        try
        {
            // Not-a-number and infinity are not readings; they are left out like a missing value.
            foreach (var node in tree.Hardware)
                foreach (var row in node.Sensors)
                    if (row.Value is { } v && !float.IsFinite(v)) row.Value = null;

            var json = JsonConvert.SerializeObject(tree, Settings);
            return json.IndexOfAny(new[] { '\n', '\r' }) >= 0 ? null : Prefix + json + "\n";
        }
        catch
        {
            return null;
        }
    }

    /// The tree carried by a line, or null if the line is not one.
    public static SensorTree? TryRead(string? line)
    {
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return JsonConvert.DeserializeObject<SensorTree>(line[Prefix.Length..], Settings); }
        catch { return null; }
    }
}
