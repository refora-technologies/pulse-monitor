using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Pulse.Services;

/// <summary>
/// One reading of everything, as it travels from the sensor host to Pulse.
///
/// Sensor reading runs in a separate process because a fault inside a graphics driver cannot
/// be caught. Reading GPU power through a handle belonging to a card that has just been
/// switched off raises an access violation, and .NET terminates the process without running
/// any handler — a try/catch around the poll is simply bypassed. Isolating it means such a
/// fault costs a restart and a second of blank tiles instead of taking Pulse with it.
///
/// Frame rate is deliberately absent. That is captured in the main process, which owns the
/// PresentMon child, and is filled in on arrival.
/// </summary>
public sealed class SensorSnapshot
{
    public SensorData Data { get; set; } = new();

    /// Graphics adapters as the host currently sees them.
    public List<GpuInfo> Gpus { get; set; } = new();

    /// The adapter the GPU readings above were taken from, for display.
    public string ActiveGpuName { get; set; } = "";

    /// False while the host has sensors open but nothing readable yet.
    public bool Ready { get; set; }

    /// Set when sensors could not be opened at all, for the About screen to explain.
    public string? Fault { get; set; }
}

/// <summary>
/// Turns snapshots into single lines and back.
///
/// Newline delimited, because the transport is the host's standard output and a line is the
/// simplest frame that cannot get out of step. Two rules make that safe: no serialised value
/// may contain a raw newline, and a line that does not parse is discarded rather than
/// resynchronised, since the next reading is at most a poll away.
/// </summary>
public static class SensorProtocol
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        // Compact: this crosses a pipe several times a second.
        Formatting = Formatting.None,

        // Absent rather than null. Most fields are empty on most machines.
        NullValueHandling = NullValueHandling.Ignore,

        // The invariant culture is not optional here. A German or French locale writes
        // decimals with a comma, which JSON does not accept, and the reading would arrive
        // either corrupted or unparseable on exactly the machines we could not test on.
        Culture = CultureInfo.InvariantCulture,

        // NaN and Infinity are not valid JSON. They reach us from sensors that are present
        // but unreadable, so they are written as quoted strings rather than bare literals
        // that no parser would accept. Sanitise() removes almost all of them first.
        FloatFormatHandling = FloatFormatHandling.String,

        // Keeps the wire pure ASCII. Adapter names come from a driver and are not always
        // ASCII, and the pipe between the two processes is a byte stream whose encoding both
        // ends have to agree on. They do agree — both set UTF-8 explicitly — but escaping as
        // well means it would take two independent mistakes, rather than one, to turn a GPU
        // name into mojibake on a machine we cannot test on.
        StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
    };

    /// <summary>
    /// Serialises one snapshot as a single line, terminated with a newline.
    ///
    /// Returns null rather than throwing: a snapshot that cannot be written must not bring
    /// down the host, and the next one is milliseconds away.
    /// </summary>
    public static string? Serialize(SensorSnapshot snapshot)
    {
        try
        {
            snapshot.Sanitise();

            var json = JsonConvert.SerializeObject(snapshot, Settings);

            // Belt and braces on the framing. Newtonsoft escapes control characters, so this
            // should never fire, but a stray newline would silently split one reading into
            // two unparseable halves and we would be debugging the symptom for hours.
            if (json.IndexOf('\n') >= 0 || json.IndexOf('\r') >= 0) return null;

            return json + "\n";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses one line. Returns null for anything unusable, including the diagnostic chatter
    /// a child process can emit before its first real output.
    /// </summary>
    public static SensorSnapshot? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Cheap rejection before handing anything to the parser.
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;

        try
        {
            var snapshot = JsonConvert.DeserializeObject<SensorSnapshot>(trimmed, Settings);
            if (snapshot == null) return null;

            // A snapshot with no payload is not worth raising to the UI.
            snapshot.Data ??= new SensorData();
            snapshot.Gpus ??= new List<GpuInfo>();
            snapshot.Sanitise();

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces values that are not real numbers with nothing at all.
    ///
    /// A sensor that reports NaN is a sensor with no reading, and the tiles already know how
    /// to show that as "--". Letting NaN through would mean formatting it into the overlay
    /// and comparing it against warning thresholds, where every comparison is false.
    /// </summary>
    private static void Sanitise(this SensorSnapshot snapshot)
    {
        var d = snapshot.Data;
        if (d == null) return;

        d.CpuTemp      = Clean(d.CpuTemp);
        d.CpuPower     = Clean(d.CpuPower);
        d.CpuClock     = Clean(d.CpuClock);
        d.CpuUsage     = Clean(d.CpuUsage);
        d.GpuTemp      = Clean(d.GpuTemp);
        d.GpuPower     = Clean(d.GpuPower);
        d.GpuClock     = Clean(d.GpuClock);
        d.GpuVram      = Clean(d.GpuVram);
        d.GpuUsage     = Clean(d.GpuUsage);
        d.RamUsed      = Clean(d.RamUsed);
        d.SysPower     = Clean(d.SysPower);
        d.NetUpload    = Clean(d.NetUpload);
        d.NetDownload  = Clean(d.NetDownload);
        d.DiskActivity = Clean(d.DiskActivity);
        d.Fps          = Clean(d.Fps);
        d.Fps1Low      = Clean(d.Fps1Low);

        if (float.IsNaN(d.TotalRamGb)  || float.IsInfinity(d.TotalRamGb))  d.TotalRamGb  = 0;
        if (float.IsNaN(d.TotalVramGb) || float.IsInfinity(d.TotalVramGb)) d.TotalVramGb = 0;
    }

    private static float? Clean(float? value)
        => value is { } v && !float.IsNaN(v) && !float.IsInfinity(v) ? v : null;
}

/// <summary>
/// Instructions sent the other way, from Pulse to the sensor host, one JSON object per line.
/// Kept as a handful of named strings rather than a class hierarchy; there are three of them.
/// </summary>
public static class SensorCommand
{
    public const string SetInterval   = "interval";
    public const string SetSubsystems = "subsystems";
    public const string SelectGpu     = "gpu";
    public const string Rescan        = "rescan";
    public const string Quit          = "quit";

    public static string Interval(double seconds) =>
        Line(new { cmd = SetInterval, seconds });

    public static string Subsystems(string flags) =>
        Line(new { cmd = SetSubsystems, flags });

    /// An empty id means "choose automatically", which is why this is not simply omitted.
    public static string Gpu(string? id) =>
        Line(new { cmd = SelectGpu, flags = id ?? "" });

    public static string RescanHardware() =>
        Line(new { cmd = Rescan });

    public static string Stop() =>
        Line(new { cmd = Quit });

    private static string Line(object command) =>
        JsonConvert.SerializeObject(command, Formatting.None,
            new JsonSerializerSettings
            {
                Culture              = CultureInfo.InvariantCulture,
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
            }) + "\n";

    /// <summary>
    /// Reads a command line. Returns null for anything unrecognised.
    ///
    /// Text carries whatever the command needs beyond a number: the subsystem flags, or the
    /// identifier of the adapter to read from.
    /// </summary>
    public static (string Cmd, double Seconds, string Text)? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;

        try
        {
            var parsed = JsonConvert.DeserializeObject<CommandShape>(trimmed);
            if (parsed?.Cmd is not { Length: > 0 }) return null;

            return (parsed.Cmd, parsed.Seconds, parsed.Flags ?? "");
        }
        catch
        {
            return null;
        }
    }

    private sealed class CommandShape
    {
        [JsonProperty("cmd")]     public string? Cmd { get; set; }
        [JsonProperty("seconds")] public double  Seconds { get; set; }
        [JsonProperty("flags")]   public string? Flags { get; set; }
    }
}
