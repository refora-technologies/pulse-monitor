// The shared core (Core/Pulse.Core.csproj), run on plain .NET with no Windows in the process:
// the frame rate and 1% low arithmetic, how a tile colours, fills and writes a reading, and the
// tile catalogue. Called directly, not through reflection, because this is what the Linux
// version will call. Every figure below is worked out by hand in the comment beside it.

using System;
using System.Linq;
using System.Reflection;
using Pulse.Models;

class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static SensorTileDefinition Tile(string id) => SensorTileDefinition.All.Single(t => t.Id == id);

    static int Main()
    {
        Console.WriteLine("Shared core\n");
        NoWindows();
        FrameRates();
        TileColours();
        TileBars();
        TileNumbers();
        Catalogue();
        Settings();
        SensorNumbers();
        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static void NoWindows()
    {
        Console.WriteLine("Nothing of Windows in it:");
        var core = typeof(TileReading).Assembly;
        var refs = core.GetReferencedAssemblies().Select(a => a.Name).ToList();
        var windows = refs.Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase"
                                      or "System.Windows.Forms" or "Microsoft.Win32.Registry" or "System.Management").ToList();
        Check("the core references no Windows libraries", windows.Count == 0, string.Join(", ", refs));
        Check("and this test runs on plain .NET, as the Linux version will",
              !AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "PresentationFramework"));
    }

    static void FrameRates()
    {
        Console.WriteLine("\nFrame rate and 1% low:");

        // Sixty frames of 1000/60 ms each is one second: 60 fps.
        Check("sixty frames in a second is 60 fps", Math.Abs(FrameMath.Fps(1000.0, 60)!.Value - 60f) < 0.001f);
        Check("one frame is not a rate", FrameMath.Fps(16.7, 1) == null);
        Check("nothing is not a rate", FrameMath.Fps(0, 0) == null);

        // 200 frames: 197 at 10 ms and 3 at 50 ms. Nearest rank: ceil(0.99 * 200) - 1 = 197, the
        // first of the three slow ones, so the 1% low is 1000/50 = 20 fps.
        var times = Enumerable.Repeat(10.0, 197).Concat(Enumerable.Repeat(50.0, 3)).Reverse().ToArray();
        Check("three stutters in two hundred frames: 20 fps", FrameMath.OnePercentLow(times, times.Length) == 20f);

        // 100 frames: 99 at 10 ms, one at 100 ms. ceil(99) - 1 = 98, still a 10 ms frame: 100 fps.
        // Truncating 0.99 * n, the common mistake, would land on the same one here; at exactly a
        // multiple of a hundred the two rules differ, which is the case this pins.
        var hundred = Enumerable.Repeat(10.0, 99).Append(100.0).ToArray();
        Check("one stutter in a hundred frames is past the 99th percentile: 100 fps",
              FrameMath.OnePercentLow(hundred, hundred.Length) == 100f);

        Check("frame times, sorted: the slow ones end up last",
              times[^1] == 50.0 && times[0] == 10.0);
        Check("no frames, no 1% low", FrameMath.OnePercentLow(new double[0], 0) == null);
        Check("only the first count are used",
              FrameMath.OnePercentLow(new[] { 20.0, 20.0, 1000.0 }, 2) == 50f);
    }

    static void TileColours()
    {
        Console.WriteLine("\nA tile's colour (0 none, 1 green, 2 amber, 3 red):");
        var cpu = Tile("cpu_temp");   // 75 amber, 90 red
        Check("CPU Temp 74 is green, 75 amber, 90 red",
              TileReading.State(cpu, 74, 0) == 1 && TileReading.State(cpu, 75, 0) == 2 && TileReading.State(cpu, 90, 0) == 3);
        Check("no reading, no colour", TileReading.State(cpu, null, 0) == 0);

        var battery = Tile("battery"); // low is bad: 20 amber, 10 red
        Check("Battery counts the other way: 50 green, 15 amber, 9 red",
              TileReading.State(battery, 50, 0) == 1 && TileReading.State(battery, 15, 0) == 2 && TileReading.State(battery, 9, 0) == 3);

        Check("power has no thresholds, so no colour", TileReading.State(Tile("cpu_power"), 120, 0) == 0);

        // RAM: catalogue 12 and 14.5 of an assumed 16 GB. With 64 GB fitted they scale by 4:
        // 48 and 58. 14.5 GB used of 64 is green.
        var ram = Tile("ram_used");
        Check("RAM thresholds scale with what is fitted: 14.5 of 64 GB is green",
              TileReading.State(ram, 14.5f, 64) == 1 && TileReading.State(ram, 50f, 64) == 2);
        Check("with no capacity known, a capacity tile has no colour", TileReading.State(ram, 15f, 0) == 0);
        var (warn, danger) = TileReading.Thresholds(ram, 64);
        Check("and the scaled points are exact", warn == 48f && danger == 58f, $"{warn} / {danger}");
    }

    static void TileBars()
    {
        Console.WriteLine("\nA tile's bar:");
        Check("8 of 16 GB is half full", TileReading.BarFraction(Tile("ram_used"), 8, 16) == 0.5);
        Check("no capacity known, no bar", TileReading.BarFraction(Tile("gpu_vram"), 2, 0) == 0);
        Check("a percentage fills against 100", TileReading.BarFraction(Tile("cpu_usage"), 25, 0) == 0.25);
        Check("never past full", TileReading.BarFraction(Tile("cpu_usage"), 140, 0) == 1);
        Check("a tile without a bar is empty", TileReading.BarFraction(Tile("cpu_temp"), 60, 0) == 0);
    }

    static void TileNumbers()
    {
        Console.WriteLine("\nA tile's number:");
        Check("gigabytes to one decimal", TileReading.FormatValue(Tile("ram_used"), 12.26f, false, false, 16) == "12.3");
        Check("with the capacity when asked: 12.3 / 16",
              TileReading.FormatValue(Tile("ram_used"), 12.26f, false, true, 16) == "12.3 / 16");
        Check("a capacity under a gigabyte keeps its decimal, never 0",
              TileReading.FormatValue(Tile("gpu_vram"), 0.4f, false, true, 0.5) == "0.4 / 0.5");
        Check("a clock in GHz to two decimals", TileReading.FormatValue(Tile("cpu_clock"), 2.456f, false, false, 0) == "2.46");
        Check("percentages whole", TileReading.FormatValue(Tile("cpu_usage"), 37.6f, false, false, 0) == "38");
        // 1 MB/s is 1,048,576 bytes, 8,388,608 bits, 8.4 decimal megabits.
        Check("network in megabits when asked: 1 MB/s is 8.4 Mbps",
              TileReading.FormatValue(Tile("net_download"), 1f, true, false, 0) == "8.4");
        Check("millivolts whole", TileReading.FormatValue(Tile("gpu_voltage"), 631.25f, false, false, 0) == "631");
    }

    static void Settings()
    {
        Console.WriteLine();
        Console.WriteLine("Settings and their file:");
        var defaults = new AppSettings();
        Check("out of the box: five tiles, none of them advanced, the in-game look of its own",
              defaults.ActiveTileIds.Count == 5 && defaults.InGameOwnLook
              && defaults.ActiveTileIds.All(id => !SensorTileDefinition.All.Single(t => t.Id == id).IsAdvanced));
        Check("a setting an older Pulse wrote and this one no longer has is ignored",
              AppSettings.FromJson("{ \"ShowAdvancedTiles\": true, \"OverlayOpacity\": 0.5 }").OverlayOpacity == 0.5);

        // Newtonsoft's default appends saved lists to the defaults; the five default tiles then
        // came back on every launch. The file's list must replace them.
        var one = AppSettings.FromJson("{ \"ActiveTileIds\": [\"ram_used\"] }");
        Check("a saved list replaces the default, never adds to it",
              one.ActiveTileIds.SequenceEqual(new[] { "ram_used" }), string.Join(",", one.ActiveTileIds));

        var bad = AppSettings.FromJson("{ \"OverlayOpacity\": 0, \"PollingIntervalSeconds\": -1, \"OverlayPosition\": \"Middle\", "
                                     + "\"InGameLayout\": \"Sideways\", \"OverlayAnchorFx\": 2, \"OverlayAnchorFy\": 0.5, \"OverlayScale\": \"NaN\" }");
        Check("values nobody could have meant fall back to the defaults",
              bad.OverlayOpacity == 0.85 && bad.PollingIntervalSeconds == 2.0 && bad.OverlayPosition == "TopRight"
              && bad.InGameLayout == "List" && bad.OverlayScale == 1.0,
              $"{bad.OverlayOpacity} {bad.PollingIntervalSeconds} {bad.OverlayPosition} {bad.InGameLayout} {bad.OverlayScale}");
        Check("a position outside the screen is no position at all", bad.OverlayAnchorFx == -1 && bad.OverlayAnchorFy == -1);

        var ids = AppSettings.FromJson("{ \"ActiveTileIds\": [\"cpu_temp\", \"\", \"cpu_temp\", \"a_tile_from_a_newer_pulse\"] }");
        Check("blanks and repeats go, a tile from a newer Pulse stays",
              ids.ActiveTileIds.SequenceEqual(new[] { "cpu_temp", "a_tile_from_a_newer_pulse" }), string.Join(",", ids.ActiveTileIds));

        var mine = new AppSettings { OverlayOpacity = 0.42, NetworkUnitBits = true, InGameTileIds = new() { "fps", "cpu_cores" } };
        var back = AppSettings.FromJson(mine.ToJson());
        Check("written and read back, the same settings",
              back.OverlayOpacity == 0.42 && back.NetworkUnitBits && back.InGameTileIds.SequenceEqual(new[] { "fps", "cpu_cores" }));
        Check("written readably, one setting a line",
              mine.ToJson().Split('\n').Any(line => line.Trim() == "\"OverlayOpacity\": 0.42,"));
        Check("an empty file holds no settings", AppSettings.FromJson("null") == null);

        bool threw = false;
        try { AppSettings.FromJson("not json"); } catch { threw = true; }
        Check("text that is not JSON is refused, for the caller to say which file it was", threw);

        // Shortcut keys are each platform's own; the shared core leaves the text as written.
        var keys = AppSettings.FromJson("{ \"ShortcutToggleOverlay\": \"shift + alt + x\" }");
        Check("shortcut text is left to the platform to repair", keys.ShortcutToggleOverlay == "shift + alt + x");
    }

    static void SensorNumbers()
    {
        Console.WriteLine();
        Console.WriteLine("The Sensors page's numbers:");
        Check("a temperature to a tenth, in degrees", SensorFormat.Format("Temperature", 59.6875f) == ("59.7", "°C"));
        Check("a clock whole, in MHz", SensorFormat.Format("Clock", 7001.4f) == ("7001", "MHz"));
        Check("a voltage to the millivolt, in volts", SensorFormat.Format("Voltage", 0.63125f) == ("0.631", "V"));
        // The library counts throughput in bytes a second: 14,099,460 is 13.45 MB/s.
        Check("throughput in MB/s, from bytes a second", SensorFormat.Format("Throughput", 14_099_460f) == ("13.45", "MB/s"));
        Check("small data whole, in MB", SensorFormat.Format("SmallData", 407.137f) == ("407", "MB"));
        Check("no value is a dash, with its unit still known", SensorFormat.Format("Power", null) == ("--", "W"));
        Check("not a number is no value", SensorFormat.Format("Load", float.NaN).Value == "--");
        Check("a kind the page does not know still shows its number", SensorFormat.Format("SomethingNew", 1.26f) == ("1.3", ""));
    }

    static void Catalogue()
    {
        Console.WriteLine("\nThe tile catalogue:");
        var all = SensorTileDefinition.All;
        Check("every id once", all.Select(t => t.Id).Distinct().Count() == all.Count, $"{all.Count} tiles");
        Check("every tile explains itself", all.All(t => !string.IsNullOrWhiteSpace(t.Description)));
        Check("the five advanced readings are marked",
              all.Where(t => t.IsAdvanced).Select(t => t.Id).OrderBy(x => x)
                 .SequenceEqual(new[] { "cpu_core_max", "cpu_cores", "gpu_hotspot", "gpu_mem_clock", "gpu_voltage" }));
    }
}
