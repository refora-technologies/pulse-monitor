// Tests the sensor host wire format against the real Pulse assembly.
//
// This format carries every reading across a process boundary several times a second. If it
// loses a field, mangles a decimal under a European locale, or lets a stray newline split one
// reading into two, the symptom on a user's machine is "the numbers are wrong sometimes" with
// nothing in any log. So it gets tested properly before anything depends on it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

class Program
{
    static int _pass, _fail;
    static Type _proto = null!, _cmd = null!, _snapT = null!, _dataT = null!, _gpuT = null!;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static string? Serialize(object snap) => (string?)_proto.GetMethod("Serialize")!.Invoke(null, new[] { snap });
    static object? TryParse(string? line) => _proto.GetMethod("TryParse")!.Invoke(null, new object?[] { line });

    static object NewSnapshot() => Activator.CreateInstance(_snapT)!;
    static object NewData()     => Activator.CreateInstance(_dataT)!;

    static void Set(object o, string prop, object? value) => o.GetType().GetProperty(prop)!.SetValue(o, value);
    static object? Get(object o, string prop)             => o.GetType().GetProperty(prop)!.GetValue(o);

    static readonly string[] FloatFields =
    {
        "CpuTemp","CpuPower","CpuClock","CpuUsage","GpuTemp","GpuPower","GpuClock","GpuVram",
        "GpuUsage","RamUsed","SysPower","NetUpload","NetDownload","DiskActivity","BatteryLevel",
        "Fps","Fps1Low",
    };

    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }

        var asm = Assembly.LoadFrom(dll);
        _proto  = asm.GetType("Pulse.Services.SensorProtocol")!;
        _cmd    = asm.GetType("Pulse.Services.SensorCommand")!;
        _snapT  = asm.GetType("Pulse.Services.SensorSnapshot")!;
        _dataT  = asm.GetType("Pulse.Services.SensorData")!;
        _gpuT   = asm.GetType("Pulse.Services.GpuInfo")!;

        Console.WriteLine("Sensor host wire format tests\n");

        RoundTrip();
        Cultures();
        BadNumbers();
        RenamingFlags();
        Framing();
        Rubbish();
        Commands();
        Adapters.Run(asm, (n, ok, d) => Check(n, ok, d));
        Picker.Run(asm, (n, ok, d) => Check(n, ok, d));

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ── every field survives the journey ─────────────────────────────────────────────
    static void RoundTrip()
    {
        Console.WriteLine("Every reading must survive the journey intact:");

        var snap = NewSnapshot();
        var data = NewData();

        float v = 1.5f;
        foreach (var f in FloatFields) { Set(data, f, (float?)v); v += 1.25f; }
        Set(data, "TotalRamGb", 15.7f);
        Set(data, "TotalVramGb", 5.9f);
        Set(snap, "Data", data);
        Set(snap, "Ready", true);
        Set(snap, "ActiveGpuName", "NVIDIA GeForce RTX 3050 6GB Laptop GPU");

        var line = Serialize(snap);
        Check("a snapshot serialises", line != null);
        if (line == null) return;

        var back = TryParse(line);
        Check("and parses back", back != null);
        if (back == null) return;

        var backData = Get(back, "Data")!;
        var lost = FloatFields.Where(f =>
        {
            var a = (float?)Get(data, f);
            var b = (float?)Get(backData, f);
            return a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b!.Value) > 0.0001f);
        }).ToList();

        Check("all seventeen readings identical", lost.Count == 0,
              lost.Count == 0 ? "" : "lost: " + string.Join(",", lost));

        Check("total RAM preserved",  Math.Abs((float)Get(backData, "TotalRamGb")! - 15.7f) < 0.001f);
        Check("total VRAM preserved", Math.Abs((float)Get(backData, "TotalVramGb")! - 5.9f) < 0.001f);
        Check("active GPU name preserved", (string)Get(back, "ActiveGpuName")! ==
              "NVIDIA GeForce RTX 3050 6GB Laptop GPU");
        Check("ready flag preserved", (bool)Get(back, "Ready")! );

        // GpuInfo uses init-only setters, which some serialisers cannot write back.
        Console.WriteLine("\nThe GPU list uses init-only properties, which not every serialiser can set:");
        var gpus = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_gpuT))!;
        var gpu = Activator.CreateInstance(_gpuT)!;
        _gpuT.GetProperty("Id")!.SetValue(gpu, "/gpu-nvidia/0");
        _gpuT.GetProperty("Name")!.SetValue(gpu, "NVIDIA GeForce RTX 3050 6GB Laptop GPU");
        _gpuT.GetProperty("IsDiscrete")!.SetValue(gpu, true);
        gpus.Add(gpu);
        Set(snap, "Gpus", gpus);

        var back2 = TryParse(Serialize(snap));
        var backGpus = (System.Collections.IList)Get(back2!, "Gpus")!;
        Check("the GPU list arrives", backGpus.Count == 1, $"count {backGpus.Count}");

        if (backGpus.Count == 1)
        {
            var g = backGpus[0]!;
            Check("its id survives",   (string)_gpuT.GetProperty("Id")!.GetValue(g)! == "/gpu-nvidia/0");
            Check("its name survives", (string)_gpuT.GetProperty("Name")!.GetValue(g)! ==
                  "NVIDIA GeForce RTX 3050 6GB Laptop GPU");
            Check("its discrete flag survives", (bool)_gpuT.GetProperty("IsDiscrete")!.GetValue(g)!);
        }
    }

    // ── the bug that already bit us once, in FpsService ──────────────────────────────
    static void Cultures()
    {
        Console.WriteLine("\nDecimals must not depend on the machine's language:");

        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "fr-FR", "tr-TR", "ar-SA" })
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(name);

                var snap = NewSnapshot();
                var data = NewData();
                Set(data, "CpuTemp", (float?)54.7f);
                Set(data, "GpuClock", (float?)1837.5f);
                Set(snap, "Data", data);

                var line = Serialize(snap);
                bool commaDecimal = line != null && line.Contains("54,7");

                // Parsed back under a different culture again, as the reading crosses from a
                // host running in one locale to a UI thread that may have another.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
                var back = line == null ? null : TryParse(line);
                var temp = back == null ? null : (float?)Get(Get(back, "Data")!, "CpuTemp");

                Check($"{name} writes a dot, not a comma", !commaDecimal);
                Check($"{name} round trips to 54.7", temp.HasValue && Math.Abs(temp.Value - 54.7f) < 0.001f,
                      temp?.ToString(CultureInfo.InvariantCulture) ?? "null");
            }
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    // ── NaN and infinity ─────────────────────────────────────────────────────────────
    static void BadNumbers()
    {
        Console.WriteLine("\nSensors that report nonsense must arrive as no reading at all:");

        var snap = NewSnapshot();
        var data = NewData();
        Set(data, "CpuTemp",  (float?)float.NaN);
        Set(data, "GpuPower", (float?)float.PositiveInfinity);
        Set(data, "GpuTemp",  (float?)float.NegativeInfinity);
        Set(data, "CpuUsage", (float?)42f);
        Set(data, "TotalRamGb", float.NaN);
        Set(snap, "Data", data);

        var line = Serialize(snap);
        Check("a snapshot full of NaN still serialises", line != null);

        var back = line == null ? null : TryParse(line);
        Check("and still parses", back != null);
        if (back == null) return;

        var d = Get(back, "Data")!;
        Check("NaN becomes no reading",              (float?)Get(d, "CpuTemp")  == null);
        Check("positive infinity becomes no reading",(float?)Get(d, "GpuPower") == null);
        Check("negative infinity becomes no reading",(float?)Get(d, "GpuTemp")  == null);
        Check("the good value beside them survives", (float?)Get(d, "CpuUsage") == 42f);
        Check("a NaN total becomes zero, not NaN",   !float.IsNaN((float)Get(d, "TotalRamGb")!));
        // Every float? SensorData has, rather than a chosen handful.
        //
        // The chosen handful is exactly how the battery reading shipped unsanitised: the field
        // was added to the snapshot, to the protocol and to the tiles, and the one list nobody
        // thought about was the list of things to clean. A list maintained by hand is out of
        // date the next time something is added, so this asks the type what it has.
        var everySnap = NewSnapshot();
        var everyData = NewData();

        var readings = _dataT.GetProperties()
                             .Where(pr => pr.PropertyType == typeof(float?) && pr.CanWrite)
                             .ToList();

        foreach (var reading in readings) reading.SetValue(everyData, (float?)float.NaN);
        Set(everySnap, "Data", everyData);

        var everyLine = Serialize(everySnap);
        var everyBack = everyLine == null ? null : TryParse(everyLine);

        Check("a snapshot with every reading set to NaN still parses", everyBack != null,
              $"{readings.Count} readings");

        if (everyBack != null)
        {
            var cleaned = Get(everyBack, "Data")!;
            var survivors = readings.Where(pr => pr.GetValue(cleaned) != null)
                                    .Select(pr => pr.Name)
                                    .ToList();

            Check("and not one of them arrives as a number that is not one",
                  survivors.Count == 0,
                  survivors.Count == 0
                      ? $"all {readings.Count} cleaned, including anything added since this was written"
                      : "still not a real reading: " + string.Join(", ", survivors));
        }
    }

    // -- the flags that rename a tile -------------------------------------------------
    //
    // Two readings mean something different depending on where they came from: video memory
    // borrowed from system RAM, and a temperature taken from the processor because the
    // graphics have no sensor of their own. Both are measured in the sensor host and drawn in
    // Pulse, so the flag saying which is which has to survive the trip between them. If it
    // does not, the number arrives under a name that is a lie, which is the whole failure this
    // pair of flags exists to prevent.
    static void RenamingFlags()
    {
        Console.WriteLine("\nThe flags that decide a tile's name must cross the boundary:");

        foreach (var (flag, reading, value) in new[]
                 {
                     ("VramIsShared",         "GpuVram", 1.5f),
                     ("GpuTempIsDie", "GpuTemp", 75f),
                 })
        {
            var snap = NewSnapshot();
            var data = NewData();
            Set(data, flag, true);
            Set(data, reading, (float?)value);
            Set(snap, "Data", data);

            var line = Serialize(snap);
            var back = line == null ? null : TryParse(line);
            Check($"a snapshot carrying {flag} parses", back != null);
            if (back == null) continue;

            var d = Get(back, "Data")!;
            Check($"{flag} survives the round trip", (bool)Get(d, flag)! == true,
                  "otherwise the tile keeps the wrong name on the other side");
            Check($"and the reading it labels arrives with it",
                  (float?)Get(d, reading) == value);
        }

        // False is the ordinary case and must not be turned into true by the trip, which a
        // serialiser that drops defaults and a reader that assumes one could between them do.
        var plain = NewSnapshot();
        var plainData = NewData();
        Set(plainData, "GpuTemp", (float?)47f);
        Set(plain, "Data", plainData);

        var plainLine = Serialize(plain);
        var plainBack = plainLine == null ? null : TryParse(plainLine);
        Check("a reading with no flag set parses", plainBack != null);
        if (plainBack != null)
        {
            var d = Get(plainBack, "Data")!;
            Check("an ordinary temperature is not marked as borrowed",
                  (bool)Get(d, "GpuTempIsDie")! == false,
                  "a discrete card must not have its tile renamed");
            Check("and an ordinary video memory reading is not marked as shared",
                  (bool)Get(d, "VramIsShared")! == false);
        }
    }

    // ── one reading per line, always ─────────────────────────────────────────────────
    static void Framing()
    {
        Console.WriteLine("\nOne reading is exactly one line, whatever a device is called:");

        var snap = NewSnapshot();
        Set(snap, "Data", NewData());
        Set(snap, "ActiveGpuName", "Evil\nGPU\r\nWith\tControl \"Chars\" \\ and 日本語");

        var line = Serialize(snap);
        Check("a hostile device name still serialises", line != null);
        if (line == null) return;

        Check("the line ends with exactly one newline",
              line.EndsWith("\n") && line.Count(c => c == '\n') == 1,
              $"{line.Count(c => c == '\n')} newlines");
        Check("no carriage return sneaks in", !line.TrimEnd('\n').Contains('\r'));

        var back = TryParse(line);
        Check("the name survives verbatim",
              back != null && (string)Get(back, "ActiveGpuName")! == "Evil\nGPU\r\nWith\tControl \"Chars\" \\ and 日本語");

        // A long name must not be truncated or wrapped.
        var snap2 = NewSnapshot();
        Set(snap2, "Data", NewData());
        Set(snap2, "ActiveGpuName", new string('X', 8000));
        var long1 = Serialize(snap2);
        var backLong = TryParse(long1);
        Check("an 8000 character name is not truncated",
              backLong != null && ((string)Get(backLong, "ActiveGpuName")!).Length == 8000);
    }

    // ── anything else on the pipe ────────────────────────────────────────────────────
    static void Rubbish()
    {
        Console.WriteLine("\nAnything that is not a reading must be discarded, never thrown:");

        var cases = new (string Label, string? Line)[]
        {
            ("null",                    null),
            ("empty",                   ""),
            ("whitespace",              "   "),
            ("plain text",              "Starting sensor host..."),
            ("a warning from a driver", "WARNING: nvml.dll not found"),
            ("truncated json",          "{\"Data\":{\"CpuTemp\":5"),
            ("json array",              "[1,2,3]"),
            ("bare number",             "42"),
            ("wrong shape",             "{\"totallyDifferent\":true}"),
            ("null literal",            "null"),
            ("nested garbage",          "{\"Data\":\"not an object\"}"),
        };

        foreach (var (label, line) in cases)
        {
            object? result = null;
            bool threw = false;
            try { result = TryParse(line); }
            catch (TargetInvocationException) { threw = true; }
            catch { threw = true; }

            // Wrong shape may parse into an empty snapshot, which is harmless. What matters is
            // that nothing throws and nothing plausible-but-wrong gets through.
            bool ok = !threw;
            Check($"{label} is handled", ok, threw ? "THREW" : result == null ? "rejected" : "parsed as empty");
        }
    }

    // ── commands going the other way ─────────────────────────────────────────────────
    static void Commands()
    {
        Console.WriteLine("\nCommands to the host must survive the same journey:");

        var interval = (string)_cmd.GetMethod("Interval")!.Invoke(null, new object[] { 0.5 })!;
        var subsys   = (string)_cmd.GetMethod("Subsystems")!.Invoke(null, new object[] { "Cpu,Gpu" })!;
        var rescan   = (string)_cmd.GetMethod("RescanHardware")!.Invoke(null, null)!;

        var parse = _cmd.GetMethod("TryParse")!;

        var a = parse.Invoke(null, new object?[] { interval });
        Check("interval command parses", a != null);
        if (a != null)
        {
            Check("its name is right",    (string)a.GetType().GetField("Item1")!.GetValue(a)! == "interval");
            Check("its value is right",   Math.Abs((double)a.GetType().GetField("Item2")!.GetValue(a)! - 0.5) < 0.0001);
        }

        var b = parse.Invoke(null, new object?[] { subsys });
        Check("subsystems command parses", b != null);
        if (b != null)
            Check("its flags are right", (string)b.GetType().GetField("Item3")!.GetValue(b)! == "Cpu,Gpu");

        Check("rescan command parses", parse.Invoke(null, new object?[] { rescan }) != null);

        foreach (var (label, line) in new (string, string?)[]
                 { ("null", null), ("text", "hello"), ("empty object", "{}"), ("truncated", "{\"cmd\":") })
        {
            bool threw = false;
            object? r = null;
            try { r = parse.Invoke(null, new object?[] { line }); } catch { threw = true; }
            Check($"a {label} command is rejected safely", !threw && (line == "{}" || r == null),
                  threw ? "THREW" : r == null ? "rejected" : "parsed");
        }

        Check("every command is one line",
              interval.Count(c => c == '\n') == 1 && subsys.Count(c => c == '\n') == 1 &&
              rescan.Count(c => c == '\n') == 1);

        Reports();
    }

    /// The sensor report for diagnostics travels back on the readings channel, so it must be one
    /// line however many lines it holds, must come back exactly, and must never be mistaken for
    /// a reading by the parser on the other side.
    static void Reports()
    {
        Console.WriteLine("\nThe sensor report must survive the readings channel:");

        var describe = (string)_cmd.GetMethod("DescribeSensors")!.Invoke(null, null)!;
        var parse    = _cmd.GetMethod("TryParse")!;
        var asked    = parse.Invoke(null, new object?[] { describe });
        Check("the describe command parses", asked != null);
        if (asked != null)
            Check("its name is right", (string)asked.GetType().GetField("Item1")!.GetValue(asked)! == "describe");

        // Many lines, a tab, quotes, backslashes from identifiers, and non-ASCII from a driver.
        var text = "Library    : LibreHardwareMonitor 0.9.6\r\nTile sources:\n  cpu_temp\tIntel > CPU Package [/intelcpu/0/temperature/12]\n"
                 + "  \"quoted\" C:\\path\\x  Radeon\u2122 RX 7600  Gr\u00e4fik  \u6e29\u5ea6\n";
        var line = (string?)_cmd.GetMethod("ReportLine")!.Invoke(null, new object[] { text });
        Check("a many-line report becomes one line", line != null && line.EndsWith("\n") && line.Count(c => c == '\n') == 1 && !line.Contains('\r'));
        Check("and that line is pure ASCII", line != null && line.All(c => c < 128));

        var back = (string?)_cmd.GetMethod("TryParseReport")!.Invoke(null, new object?[] { line?.TrimEnd('\n') });
        Check("the report comes back exactly", back == text, back == text ? "" : back == null ? "nothing came back" : "it came back different");

        var asSnapshot = _proto.GetMethod("TryParse")!.Invoke(null, new object?[] { line?.TrimEnd('\n') });
        Check("the readings parser ignores a report line", asSnapshot == null);

        var snapshotLine = (string?)_proto.GetMethod("Serialize")!.Invoke(null, new[] { Activator.CreateInstance(_snapT) });
        Check("the report parser ignores a reading", _cmd.GetMethod("TryParseReport")!.Invoke(null, new object?[] { snapshotLine?.TrimEnd('\n') }) == null);
        Check("and ignores the host's banners",
              _cmd.GetMethod("TryParseReport")!.Invoke(null, new object?[] { "#pulse-sensor-host" }) == null
           && _cmd.GetMethod("TryParseReport")!.Invoke(null, new object?[] { "#pulse-sensor-host-opened" }) == null);

        var big = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"  Sensor {i,-4} Temperature  {i * 0.5:0.0}  /x/{i}"));
        var bigBack = (string?)_cmd.GetMethod("TryParseReport")!.Invoke(null,
            new object?[] { ((string?)_cmd.GetMethod("ReportLine")!.Invoke(null, new object[] { big }))?.TrimEnd('\n') });
        Check("a report of two thousand sensors comes back whole", bigBack == big);
    }
}
