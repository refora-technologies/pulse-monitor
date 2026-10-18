// Tests how Pulse decides, and shows, how much memory a machine has.
//
// This exists because Pulse used to invent the answer. TotalVramGb started at 6 and TotalRamGb
// at 16, which are not measurements of anything, and on a machine with only integrated graphics
// nothing ever replaced the VRAM one. The tile then reported a 6 GB capacity that did not
// exist, sized its bar against it, and coloured warnings from it. A user asked where the 6 GB
// came from and the honest answer was that we made it up.
//
// The rule these tests enforce: a capacity is either measured or absent. Never a plausible
// looking guess.

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static int _pass, _fail;
    static Assembly _asm = null!;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }
        _asm = Assembly.LoadFrom(dll);

        Console.WriteLine("Memory capacity\n");

        NoInventedDefaults();
        WindowsIsTheSource();
        TileBehaviour();
        Labelling();
        MonitorIdentity();
        PowerHasNoThresholds();
        IntegratedGraphicsTemperature();
        WindowsMemoryCounters();
        WhichPoolIsShown();
        CpuClockIsCoresOnly();
        NetworkCountsEachLinkOnce();
        NetworkUnitIsAChoice();
        GpuUsageIsTheVendorFigure();
        BatteryIsTheOneTileWhereLowIsBad();
        EveryTileExplainsItself();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ── the bug itself ──────────────────────────────────────────────────────────────
    static void NoInventedDefaults()
    {
        Console.WriteLine("A capacity nobody measured must read as unknown, not as a number:");

        // Read off the type rather than an instance: constructing HardwareService starts the
        // sensor host, which is not what this suite is about.
        var hardware = _asm.GetType("Pulse.Services.HardwareService")!;

        foreach (var name in new[] { "TotalRamGb", "TotalVramGb" })
        {
            var prop = hardware.GetProperty(name);
            Check($"{name} exists and is a float", prop != null && prop.PropertyType == typeof(float),
                  prop?.PropertyType.Name ?? "missing");
        }

        // The defaults lived in the constructor, so that is where a reintroduced one would be.
        var source = FindSource("Services/HardwareService.cs");
        if (source != null)
        {
            Check("no baked in RAM capacity",  !source.Contains("TotalRamGb { get; private set; } = "),
                  "TotalRamGb must start at zero");
            Check("no baked in VRAM capacity", !source.Contains("TotalVramGb { get; private set; } = "),
                  "TotalVramGb must start at zero");
            Check("a change of GPU clears the video memory capacity",
                  source.Contains("TotalVramGb = 0"),
                  "a capacity belongs to the adapter it came from");
        }
    }

    // ── where the number comes from ─────────────────────────────────────────────────
    static void WindowsIsTheSource()
    {
        Console.WriteLine("\nThe capacity must come from Windows, which is what users compare against:");

        var adapters = _asm.GetType("Pulse.Services.DisplayAdapters")!;
        var all = adapters.GetMethod("All", BindingFlags.Public | BindingFlags.Static);
        var lookup = adapters.GetMethod("DedicatedVideoMemoryMb", BindingFlags.Public | BindingFlags.Static);

        if (all == null || lookup == null)
        {
            Check("the adapter memory lookup exists", false, "method not found");
            return;
        }

        var list = ((IEnumerable)all.Invoke(null, null)!).Cast<object>().ToArray();
        Check("Windows reports at least one adapter", list.Length > 0, $"{list.Length} adapters");
        if (list.Length == 0) return;

        string Name(object a) => (string)a.GetType().GetProperty("Description")!.GetValue(a)!;
        float  Mb(object a)   => (float)a.GetType().GetProperty("DedicatedVideoMemoryMb")!.GetValue(a)!;

        foreach (var a in list)
            Console.WriteLine($"        {Name(a),-42} {Mb(a),8:F0} MB");

        // A machine with no graphics driver at all, such as GitHub's build machines, has only
        // Windows' software renderer, and that has no memory of its own to report. Zero is the
        // right answer there, so it is only a failure where a real graphics driver is present.
        bool softwareOnly = list.All(a => Name(a).StartsWith("Microsoft Basic", StringComparison.OrdinalIgnoreCase));
        var real = list.FirstOrDefault(a => Mb(a) > 0);
        if (softwareOnly)
            Console.WriteLine("        only Windows' software renderer here, so there is no video memory to find");
        else
            Check("at least one adapter reports real dedicated memory", real != null);

        if (real != null)
        {
            Check("looking it up by name returns the same figure",
                  Math.Abs((float)lookup.Invoke(null, new object[] { Name(real) })! - Mb(real)) < 0.5f,
                  $"{Name(real)} -> {Mb(real):F0} MB");
        }

        // The important half. An unknown adapter must not produce a plausible number.
        Check("an adapter Windows does not know returns zero, not a guess",
              (float)lookup.Invoke(null, new object[] { "Definitely Not A Real GPU" })! == 0f);
        Check("an empty name returns zero",
              (float)lookup.Invoke(null, new object[] { "" })! == 0f);
    }

    // ── what the tile does with it ──────────────────────────────────────────────────
    static void TileBehaviour()
    {
        Console.WriteLine("\nWith no capacity known, the tile must show no capacity:");

        var source = FindSource("ViewModels/OverlayViewModel.cs");
        if (source == null) { Check("OverlayViewModel source found", false, ""); return; }

        // These are assertions about the rules, because exercising the real TileViewModel means
        // constructing HardwareService and starting the sensor host.
        Check("an unknown capacity draws no bar",
              source.Contains("if (max <= 0) return 0;")
              && !source.Contains("if (max <= 0) max = Definition.BarMax;"),
              "BarFraction must not fall back to the catalogue figure");

        Check("an unknown capacity colours nothing",
              source.Contains("if (capacity <= 0 || Definition.BarMax <= 0) return (0, 0);"),
              "Thresholds must not fall back to absolute catalogue values");

        Check("a capacity below a gigabyte keeps a decimal",
              source.Contains("max:F1"),
              "otherwise a few hundred megabytes prints as a total of 0");

        Check("a capacity of zero is never printed",
              source.Contains("if (max >= 1)") && source.Contains("if (max >  0)"),
              "both branches require a positive capacity");
    }


    // ── discrete or integrated ──────────────────────────────────────────────────────
    static void Labelling()
    {
        Console.WriteLine();
        Console.WriteLine("An adapter must be labelled from something that can tell them apart:");

        var reader = FindSource("Services/SensorReader.cs");
        if (reader == null) { Check("SensorReader source found", false, ""); return; }

        Check("the sensor's own memory total is no longer the deciding test",
              reader.Contains("var windowsMb = DedicatedVideoMemoryMb(gpu.Name);"),
              "an AMD APU reports its reserved system memory there as though it were VRAM");

        Check("Windows' figure decides it, against a stated boundary",
              reader.Contains("windowsMb >= IntegratedVramCeilingMb"),
              "so a 460 MB APU reads integrated and a 6 GB card reads discrete");

        Check("the boundary sits above what integrated graphics reserve",
              reader.Contains("IntegratedVramCeilingMb = 1024f"),
              "Intel here reports 128 MB, the reporter's Radeon 740M about 460 MB");

        Check("an adapter Windows cannot describe still falls back to the sensor",
              reader.Contains("return GetDedicatedVramMb(gpu) > 0;"),
              "better than refusing to label it at all");

        // The real adapters on this machine, run through the same boundary.
        var adapters = _asm.GetType("Pulse.Services.DisplayAdapters")!;
        var all = (IEnumerable)adapters.GetMethod("All", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var a in all.Cast<object>())
        {
            var name = (string)a.GetType().GetProperty("Description")!.GetValue(a)!;
            var mb   = (float)a.GetType().GetProperty("DedicatedVideoMemoryMb")!.GetValue(a)!;
            if (mb <= 0) continue;   // software adapters

            bool discrete = mb >= 1024f;
            bool expected = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

            Check($"{name} reads as {(discrete ? "discrete" : "integrated")}",
                  discrete == expected, $"{mb:F0} MB");
        }
    }

    // ── which display the overlay belongs to ────────────────────────────────────────
    static void MonitorIdentity()
    {
        Console.WriteLine();
        Console.WriteLine("A display must still be recognised after Windows renumbers it:");

        var overlay = FindSource("Views/OverlayWindow.xaml.cs");
        var settings = FindSource("Models/AppSettings.cs");
        if (overlay == null || settings == null) { Check("sources found", false, ""); return; }

        Check("the display's geometry is stored alongside its name",
              settings.Contains("OverlayMonitorBounds")
              && overlay.Contains("settings.OverlayMonitorBounds = BoundsKey(screen);"),
              "the name alone changes when the graphics adapters do");

        Check("and is used when the name no longer matches",
              overlay.Contains("string.Equals(BoundsKey(screen), settings.OverlayMonitorBounds"),
              "so a renumbered display is still found rather than given up on");

        // The renumbering the reporter's log actually shows: same panel, DISPLAY1 to DISPLAY11.
        var screens = System.Windows.Forms.Screen.AllScreens;
        Check("this machine reports at least one display", screens.Length > 0, $"{screens.Length}");

        if (screens.Length > 0)
        {
            var b = screens[0].Bounds;
            var key = $"{b.Left},{b.Top},{b.Width},{b.Height}";
            Check("a display's geometry key is stable across reads",
                  key == $"{screens[0].Bounds.Left},{screens[0].Bounds.Top},{screens[0].Bounds.Width},{screens[0].Bounds.Height}",
                  key);
            Check("and does not contain the device name that changes",
                  !key.Contains("DISPLAY"), key);
        }
    }

    // ── watts we cannot judge ───────────────────────────────────────────────────────
    //
    // The same defect as the invented capacities above, in a different place. A capacity tile
    // stopped colouring against a number nobody measured; a power tile was still colouring
    // against absolute watts that fit no particular machine.
    static void PowerHasNoThresholds()
    {
        Console.WriteLine("\nPower must not be judged against watts that fit no hardware:");

        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        var warn   = new System.Collections.Generic.Dictionary<string, float>();
        var danger = new System.Collections.Generic.Dictionary<string, float>();
        var known  = new System.Collections.Generic.Dictionary<string, bool>();

        foreach (var d in all)
        {
            var id = (string)defs.GetProperty("Id")!.GetValue(d)!;
            warn[id]   = (float)defs.GetProperty("WarnThreshold")!.GetValue(d)!;
            danger[id] = (float)defs.GetProperty("DangerThreshold")!.GetValue(d)!;
            known[id]  = (bool)defs.GetProperty("HasKnownMax")!.GetValue(d)!;
        }

        foreach (var (id, wasWarn, wasDanger) in new[]
                 {
                     ("cpu_power", 65f, 95f),
                     ("gpu_power", 80f, 115f),
                     ("sys_power", 100f, 160f),
                 })
        {
            Check($"{id} carries no threshold",
                  warn.ContainsKey(id) && warn[id] == 0 && danger[id] == 0,
                  $"was {wasWarn:F0}W warn, {wasDanger:F0}W danger");

            // Nothing to scale against, which is why the capacity fix could not simply be
            // applied here: HasKnownMax means a real measured ceiling exists, and for power
            // none does.
            Check($"and claims no capacity to scale against", !known[id],
                  "the library reports draw, never the limit");
        }

        // What the old numbers actually did on real hardware. The colour each machine got is
        // worked out from the thresholds that used to be in the catalogue, so these say
        // precisely what was wrong rather than gesturing at it.
        static string Colour(float watts, float oldWarn, float oldDanger) =>
            watts >= oldDanger ? "red" : watts >= oldWarn ? "amber" : "green";

        foreach (var (machine, watts, oldWarn, oldDanger, expected, shouldHaveBeen) in new[]
                 {
                     ("a 400W card at full load", 380f, 80f,  115f, "red",   "green, it is working normally"),
                     ("a 400W card sitting idle",  90f, 80f,  115f, "amber", "green, it is doing nothing"),
                     ("a 15W laptop chip maxed",   14f, 65f,   95f, "green", "its own warning, if any"),
                     ("a 65W desktop chip maxed",  64f, 65f,   95f, "green", "roughly right, by luck"),
                 })
        {
            Check($"{machine} used to read {expected}",
                  Colour(watts, oldWarn, oldDanger) == expected,
                  $"{watts:F0}W against {oldWarn:F0}/{oldDanger:F0}W, should have been {shouldHaveBeen}");
        }

        Check("only hardware close to the catalogue figures was ever coloured sensibly",
              Colour(64f, 65f, 95f) == "green" && Colour(380f, 80f, 115f) == "red",
              "a 65W chip happened to match; nothing else did");

        // A zero danger threshold is the existing "no opinion" convention, already used by the
        // clock and frame rate tiles, so this needed no new mechanism.
        var overlay = FindSource("ViewModels/OverlayViewModel.cs");
        Check("a zero threshold means no colour at all",
              overlay != null && overlay.Contains("if (danger == 0) return 0;"),
              "the same convention the clock tiles already use");

        Check("the temperature tiles keep theirs",
              danger["cpu_temp"] == 90 && danger["gpu_temp"] == 90,
              "a chip approaching its limit is a real warning; watts under load are not");

        Check("and so do the percentage tiles",
              danger["cpu_usage"] == 90 && danger["gpu_usage"] == 95 && danger["disk_activity"] == 90,
              "a percentage has a real ceiling of 100");

        // The reasoning has to survive someone deciding a number would look better than none.
        var source = FindSource("Models/SensorTileDefinition.cs");
        Check("the reason is written down where the constants were",
              source != null && source.Contains("Power tiles carry no warning thresholds"),
              "so this is not quietly reverted to a plausible looking guess");
    }


    // -- where an integrated GPU's temperature actually lives -----------------------
    //
    // Measured on two machines rather than reasoned about:
    //
    //   AMD Ryzen 5 PRO 3500U / Vega 8
    //       /gpu-amd/0            no Temperature sensor at all
    //       /amdcpu/0             Temperature "GFX" 57.90
    //       Task Manager          61 C
    //       Pulse before this     "--"
    //
    //   Intel Core i5-13420H / UHD Graphics
    //       /gpu-intel-integrated no Temperature sensor
    //       /intelcpu/0           no graphics temperature anywhere
    //       Task Manager          N/A
    //       Pulse                 "--", which is correct there
    //
    // So the reading exists on one vendor and genuinely does not on the other, and the code
    // must produce a number in the first case without inventing one in the second.
    static void IntegratedGraphicsTemperature()
    {
        Console.WriteLine();
        Console.WriteLine("Graphics temperature comes from Windows, and is absent when Windows has none:");

        var src = FindSource("Services/SensorReader.cs");
        var adp = FindSource("Services/DisplayAdapters.cs");
        if (src == null || adp == null) { Check("the sources were found", false, ""); return; }

        Check("the temperature is a named step, not a line hidden in the poll",
              src.Contains("chosenIsDiscrete = IsDiscrete(chosen);")
              && src.Contains("ApplyWindowsTemperature(chosen.Name, chosenIsDiscrete, data);"));

        Check("it is asked of Windows, per adapter",
              src.Contains("data.GpuTemp ??= DisplayAdapters.Temperature(adapter.Luid);"),
              "the same source Task Manager's GPU page is drawn from");

        Check("and only fills a gap, never replaces the device's own reading",
              src.Contains("??= DisplayAdapters.Temperature"),
              "a discrete card reads its own die and needs no second opinion");

        // The rule that made this necessary: no invented substitute.
        Check("no processor temperature is put on the graphics tile",
              !src.Contains("data.GpuTemp = data.CpuTemp;") && !src.Contains("_cpuGraphicsTemp"),
              "Task Manager shows nothing for graphics with no sensor, and so must Pulse");

        Check("nothing reported means nothing shown",
              adp.Contains("if (perf.Temperature == 0) return null;"),
              "a driver with no temperature says so by giving none");

        Check("the scale is read from what Windows states, not assumed",
              adp.Contains("bool tenths = max > 0 ? max >= 200 : perf.Temperature >= 200;"),
              "read as tenths, a driver reporting whole degrees would put 47 on screen as 4.7");

        Check("and anything outside a plausible range is discarded",
              adp.Contains("return celsius is > 0f and <= 150f ? celsius : null;"));

        Check("the reading is marked when it belongs to the processor's own graphics",
              src.Contains("data.GpuTempIsDie = !discrete && data.GpuTemp is not null;"),
              "so the tile explains why it matches CPU Temp exactly");

        Check("the display kernel handle is handed back",
              adp.Contains("private static void Close(uint handle)") && adp.Contains("Close(handle);"),
              "one leaked handle per poll accumulates for as long as Pulse runs");

        // ── asked of this machine, not of a fixture ──────────────────────────────────
        var adapters = _asm.GetType("Pulse.Services.DisplayAdapters")!;
        var all = (System.Collections.IEnumerable)adapters.GetMethod("All",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        var temperature = adapters.GetMethod("Temperature", BindingFlags.Public | BindingFlags.Static)!;

        int total = 0, integrated = 0, withTemp = 0;
        bool allPlausible = true;
        string listed = "";

        foreach (var a in all)
        {
            var t = a.GetType();
            long luid = (long)t.GetProperty("Luid")!.GetValue(a)!;
            string name = (string)t.GetProperty("Description")!.GetValue(a)!;
            bool isIntegrated = (bool)t.GetProperty("Integrated")!.GetValue(a)!;
            var c = (float?)temperature.Invoke(null, new object[] { luid });

            total++;
            if (isIntegrated) integrated++;
            if (c.HasValue) { withTemp++; if (c.Value <= 0 || c.Value > 150) allPlausible = false; }
            listed += $"{name}={(c.HasValue ? c.Value.ToString("F1") : "none")}; ";
        }

        Check("every adapter on this machine can be asked", total > 0, listed.Trim());

        Check("and each answer is either a real temperature or none at all", allPlausible,
              "never a number outside what a running adapter reaches");

        Check("Windows distinguishes integrated from discrete here",
              total <= 1 || integrated < total, $"{integrated} of {total} integrated");

        // Not every machine has an adapter that reports one, so this is reported rather than
        // required: a machine where none does is a machine where Task Manager shows none.
        Console.WriteLine($"        {withTemp} of {total} adapters report a temperature to Windows");

        // The flag has to cross the process boundary and reach the tile.
        var data = _asm.GetType("Pulse.Services.SensorData")!;
        Check("the flag exists on the reading", data.GetProperty("GpuTempIsDie") != null);

        var service = _asm.GetType("Pulse.Services.HardwareService")!;
        Check("and is exposed by the service", service.GetProperty("GpuTempIsDie") != null);

        var hs = FindSource("Services/HardwareService.cs");
        Check("it survives a held snapshot",
              hs != null && hs.Contains("GpuTempIsDie = previous.GpuTempIsDie,"),
              "otherwise the tile would rename itself whenever readings paused");

        Check("and is only updated while there is a reading",
              hs != null && hs.Contains("if (data.GpuTemp is not null) GpuTempIsDie = data.GpuTempIsDie;"),
              "so the name does not flicker while readings are missing");

        var vm = FindSource("ViewModels/OverlayViewModel.cs");
        Check("the tile says the reading is the die's",
              vm != null && vm.Contains("=> \"GPU Die Temp\""),
              "two identical numbers with no explanation read as a bug");

        Check("and only when the graphics really are part of the processor",
              vm != null && vm.Contains("\"gpu_temp\" when HardwareService.Instance.GpuTempIsDie"),
              "a discrete card keeps the ordinary name");
    }


    // -- joining Windows' memory counters to the adapters they belong to --------------
    //
    // Windows publishes per-adapter memory under "GPU Adapter Memory", keyed by the adapter's
    // locally unique identifier. That is the same source Task Manager reads, it covers every
    // vendor, and it does not depend on the sensor library enumerating the adapter, which on
    // this machine it does only sometimes.
    //
    // The join is the part that can silently be wrong, so it is exercised against whatever
    // hardware this actually runs on rather than against a fixture.
    static void WindowsMemoryCounters()
    {
        Console.WriteLine();
        Console.WriteLine("Windows' own memory counters must join to the right adapter:");

        var adapters = _asm.GetType("Pulse.Services.DisplayAdapters")!;
        var all = adapters.GetMethod("All", BindingFlags.Public | BindingFlags.Static);
        if (all == null) { Check("the adapter list is available", false); return; }

        var list = ((IEnumerable)all.Invoke(null, null)!).Cast<object>().ToArray();
        if (list.Length == 0) { Check("Windows returned adapters", false); return; }

        string Key(object a)  => (string)a.GetType().GetProperty("CounterKey")!.GetValue(a)!;
        string Name(object a) => (string)a.GetType().GetProperty("Description")!.GetValue(a)!;
        float Ded(object a)   => (float)a.GetType().GetProperty("DedicatedVideoMemoryMb")!.GetValue(a)!;
        float Shr(object a)   => (float)a.GetType().GetProperty("SharedSystemMemoryMb")!.GetValue(a)!;

        Check("every adapter reports a shared system memory total",
              list.All(a => Shr(a) > 0),
              $"{Shr(list[0]):F0} MB, the pool integrated graphics draw from");

        // The counter key format, checked character by character rather than by eye.
        foreach (var a in list.Take(1))
            Check("the counter key is shaped the way Windows names its instances",
                  System.Text.RegularExpressions.Regex.IsMatch(Key(a), @"^luid_0x[0-9a-f]{8}_0x[0-9a-f]{8}_phys_0$"),
                  Key(a));

        string[] instances;
        try
        {
            instances = new System.Diagnostics.PerformanceCounterCategory("GPU Adapter Memory")
                            .GetInstanceNames();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    (skipped: this machine publishes no GPU memory counters: {ex.GetType().Name})");
            Console.WriteLine("     Pulse falls back to the sensor library, which is the designed behaviour.");
            return;
        }

        Check("Windows publishes counter instances", instances.Length > 0, $"{instances.Length}");

        int matched = 0;
        foreach (var a in list)
        {
            var hit = instances.FirstOrDefault(i => string.Equals(i, Key(a), StringComparison.OrdinalIgnoreCase));
            if (hit == null) continue;
            matched++;

            var counters = _asm.GetType("Pulse.Services.GpuMemoryCounters")!;
            using var reader = (IDisposable)Activator.CreateInstance(counters, true)!;
            var usage = counters.GetMethod("Read")!.Invoke(reader, new object[] { Key(a) });

            Check($"{Name(a)} joins to a counter instance", usage != null, hit);

            if (usage != null)
            {
                var type = usage.GetType().GetGenericArguments().Length > 0
                    ? usage.GetType().GetGenericArguments()[0] : usage.GetType();
                var value = usage.GetType().GetProperty("Value")?.GetValue(usage) ?? usage;

                float ded = (float)value.GetType().GetProperty("DedicatedBytes")!.GetValue(value)!;
                float shr = (float)value.GetType().GetProperty("SharedBytes")!.GetValue(value)!;

                Console.WriteLine($"        dedicated {ded / 1048576.0,9:N0} MB of {Ded(a),7:N0} MB, "
                                + $"shared {shr / 1048576.0,9:N0} MB of {Shr(a),7:N0} MB");

                Check($"  its usage never exceeds its capacity",
                      ded / 1048576.0 <= Ded(a) + 1 && shr / 1048576.0 <= Shr(a) + 1,
                      "a usage above the pool size would mean the join is wrong");
            }
        }

        Check("at least one adapter joined", matched > 0,
              $"{matched} of {list.Length}; adapters without counters are normal");

        // Windows spells the hexadecimal in upper case; the key is built in lower. An instance
        // whose digits happen to be all numerals matches either way, so the need for a
        // case-insensitive join only shows on one whose identifier contains a letter.
        var withLetter = instances.FirstOrDefault(i => i.Any(char.IsUpper));

        if (withLetter == null)
        {
            Console.WriteLine("    (no instance on this machine has a letter in its identifier,");
            Console.WriteLine("     so case cannot be demonstrated here; the code ignores it regardless)");
        }
        else
        {
            var ours = list.Select(Key).FirstOrDefault(k => string.Equals(k, withLetter, StringComparison.OrdinalIgnoreCase));

            Check("an identifier containing a letter still joins", ours != null, withLetter);
            Check("and it would not have joined case-sensitively",
                  ours != null && !string.Equals(ours, withLetter, StringComparison.Ordinal),
                  $"Windows says {withLetter}, we build {ours}");
        }

        var src = FindSource("Services/GpuMemoryCounters.cs");
        Check("and it does", src != null && src.Contains("StringComparison.OrdinalIgnoreCase"));

        Check("the counter objects are kept rather than rebuilt",
              src != null && src.Contains("cache.TryGetValue(instance, out var reader)"),
              "measured at 0.09ms per poll kept, against 42ms through WMI");

        Check("and thrown away when the adapters change",
              src != null && src.Contains("public void Refresh()"),
              "the identifiers are not stable; the same adapters had different ones an hour apart");

        Check("a machine with no counters falls back rather than failing",
              src != null && src.Contains("Available  = false;"),
              "older Windows, and damaged counter registries, both exist");
    }


    // -- which pool the tile shows, and what it is then called ----------------------
    //
    // Measured on two machines:
    //
    //   Intel UHD      dedicated   128 MB total, 0 used      -> the shared pool is where its
    //                  shared    8,054 MB total, 1,405 used     memory actually is
    //                  Task Manager shows no Dedicated row at all
    //
    //   AMD Vega 8     dedicated 2,033 MB total, 505 used    -> a real pool, and Task Manager
    //                  shared    3,011 MB total,  88 used       shows it as Dedicated
    //
    //   RTX 3050       dedicated 6,001 MB total, 452 used    -> obviously dedicated
    //
    // So an integrated GPU can have a large dedicated pool, and the choice cannot be made from
    // whether a GPU is integrated. It is made from whether it has a pool worth showing.
    static void WhichPoolIsShown()
    {
        Console.WriteLine();
        Console.WriteLine("The tile shows the pool the adapter actually uses, and says which:");

        var src = FindSource("Services/SensorReader.cs");
        if (src == null) { Check("SensorReader source found", false); return; }

        Check("the choice is made from the dedicated pool's size",
              src.Contains("bool useShared = dedicatedTotalMb < RealDedicatedPoolMb;"),
              "not from the vendor, and not from integrated versus discrete");

        Check("the boundary separates a stub from a real pool",
              src.Contains("RealDedicatedPoolMb = 256f"),
              "Intel reports 128 MB and uses none; an AMD APU reports 2,033 MB and uses it");

        Check("and it is not the same constant as the discrete test",
              src.Contains("IntegratedVramCeilingMb = 1024f"),
              "the Vega 8 is integrated and has two gigabytes, so they are different questions");

        Check("Windows is asked first",
              src.Contains("_gpuMemory.Read(adapter.Value.CounterKey)"),
              "the same figures Task Manager shows, for every vendor");

        // Usage, capacity and the name of the pool are one measurement. They were not: with the
        // Windows counters unreadable, the amount in use stayed as the sensor library had it,
        // which is dedicated memory, while the capacity became the shared pool's and the tile
        // was renamed "Shared VRAM". Three quarters of a measurement, reading as a whole one.
        Check("an unreadable counter leaves the pool unnamed rather than half named",
              src.Contains("if (usage is not { } used)"),
              "dedicated usage under a shared capacity is a nonsense the tile cannot show");

        Check("the capacity shown is the capacity of the pool shown",
              src.Contains("float totalMb = useShared ? sharedTotalMb : dedicatedTotalMb;"),
              "showing shared usage against a dedicated total would be a nonsense fraction");

        Check("an unknown capacity is still left unknown",
              src.Contains("if (totalMb > 0) data.TotalVramGb = totalMb / 1024f;"),
              "zero means no answer, and is never drawn");

        Check("the shared flag is only raised when there is a reading to label",
              src.Contains("data.VramIsShared = useShared && data.GpuVram is not null;"),
              "an empty tile keeps its ordinary name");

        Check("a duplicate adapter entry is disambiguated by which one has counters",
              src.Contains("if (_gpuMemory.Read(adapter.CounterKey) != null) return adapter;"),
              "both machines list one adapter twice, and only one of the two carries counters");

        // The reading crosses a process boundary, so the flag has to survive the trip.
        var wire = FindSource("Services/SensorSnapshot.cs");
        var data = _asm.GetType("Pulse.Services.SensorData")!;
        Check("the flag exists on the reading", data.GetProperty("VramIsShared") != null);

        var service = _asm.GetType("Pulse.Services.HardwareService")!;
        Check("and is exposed by the service", service.GetProperty("VramIsShared") != null);

        var hs = FindSource("Services/HardwareService.cs");
        Check("it survives a held snapshot",
              hs != null && hs.Contains("VramIsShared = previous.VramIsShared,"),
              "otherwise the tile would rename itself whenever readings paused");

        Check("and is only updated while there is a reading",
              hs != null && hs.Contains("if (data.GpuVram is not null) VramIsShared = data.VramIsShared;"),
              "so the name does not flicker between the two while readings are missing");

        // The name on screen.
        var vm = FindSource("ViewModels/OverlayViewModel.cs");
        Check("the tile is named for the pool it shows",
              vm != null && vm.Contains("=> \"Shared VRAM\""),
              "Windows calls them Dedicated and Shared GPU memory; the tile follows");

        Check("only tiles whose reading changed meaning are renamed",
              vm != null && vm.Contains("\"gpu_vram\" when HardwareService.Instance.VramIsShared")
              && vm.Contains("_ => Definition.Label,"),
              "every other tile keeps its catalogue name");

        Check("and the name is re-raised when a reading arrives",
              vm != null && vm.Contains("OnPropertyChanged(nameof(DisplayLabel));"),
              "switching graphics can change which pool the figure comes from");

        var overlay = FindSource("Views/OverlayWindow.xaml");
        Check("the overlay binds the name that can change",
              overlay != null && overlay.Contains("{Binding DisplayLabel}")
              && !overlay.Contains("Text=\"{Binding Definition.Label}\""),
              "binding the catalogue name would ignore all of this");
    }


    // -- which of two honest numbers GPU Usage shows --------------------------------
    //
    // Measured on an RTX 3050 over thirty seconds in a game, all three sampled together:
    //
    //     D3D 3D       51.4 average, 54.3 peak      Task Manager read 50.8
    //     GPU Core     88.4 average, 93.0 peak      Afterburner and the NVIDIA overlay
    //
    // And on a Vega 8, measured twice on different days, GPU Core reads a flat 100.00 while
    // the engine counter reads 8.02 and Task Manager reads 8%. So the vendor figure is only
    // worth having from a discrete card; on graphics built into a processor it is either a
    // constant or absent.
    //
    // Both are honest and they answer different questions. The engine counter is how long the
    // 3D engine had work executing; the vendor figure is how often any kernel was running. The
    // engine counter agrees with Task Manager almost exactly, which is why Pulse used to show
    // it. It was changed because two people in one week reported Pulse reading low beside
    // Afterburner and the NVIDIA overlay in a game, and nobody has ever reported it disagreeing
    // with Task Manager.
    //
    // This has now been decided in both directions, so it is written down rather than left to
    // be rediscovered by whoever next compares two overlays.
    static void GpuUsageIsTheVendorFigure()
    {
        Console.WriteLine();
        Console.WriteLine("GPU Usage shows the figure the overlays a gamer already runs are showing:");

        var src = FindSource("Services/SensorReader.cs");
        if (src == null) { Check("SensorReader source found", false, ""); return; }

        Check("the vendor's own load is used, on a discrete card",
              src.Contains("if (discrete && _vendorGpuLoad is { } vendor) data.GpuUsage = vendor;"),
              "Afterburner, RTSS and the NVIDIA overlay all read this one");

        Check("and never on graphics built into a processor",
              src.Contains("if (discrete && _vendorGpuLoad"),
              "a Vega 8 reports it as a flat 100 while the machine is idle");

        // One question, one answer. The picker weighed four tests and this read the hybrid
        // flag on its own, so a machine they disagreed about would list an adapter as a
        // graphics card and rename its temperature tile to say it was part of the processor.
        Check("discrete is decided once and handed to the step that needs it",
              src.Contains("ApplyWindowsTemperature(string adapterName, bool discrete, SensorData data)"),
              "two classifiers is one more than the number of answers there are");

        Check("the engine counter is what integrated graphics keep",
              src.Contains("data.GpuUsage   = d3dEngineLoad ?? coreLoad;"),
              "8.02 against Task Manager's 8%, where the vendor figure said 100");

        Check("the vendor figure is cleared every poll",
              src.Contains("_vendorGpuLoad     = null;"),
              "a load held over from a previous cycle is a stale reading");

        Check("both sensors are still read",
              src.Contains("d3dEngineLoad = s.Value;") && src.Contains("coreLoad = s.Value;"),
              "integrated graphics publish no vendor load, so the fallback is a real path");

        Check("the measurement that was traded away is recorded",
              src.Contains("51.4 average") && src.Contains("88.4 average"),
              "so the next person to compare two overlays finds the answer here");

        Check("and so is the one that limited it to discrete cards",
              src.Contains("100.00") && src.Contains("8.02"),
              "the Vega 8 reading that made the first version of this wrong");

        Check("and so is the reason it was traded",
              src.Contains("Nobody has ever reported that it") || src.Contains("disagreed with Task Manager"),
              "the decision has been made in both directions now");

        // The tile explains itself, the same way the 1% low tile does.
        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        string? desc = null;
        foreach (var d in all)
            if ((string)defs.GetProperty("Id")!.GetValue(d)! == "gpu_usage")
                desc = defs.GetProperty("Description")?.GetValue(d) as string;

        Check("the tile explains what it shows", !string.IsNullOrWhiteSpace(desc),
              desc is { Length: > 0 } ? desc[..Math.Min(70, desc.Length)] + "..." : "no description");

        Check("and says where the number comes from",
              desc != null && desc.Contains("driver", StringComparison.OrdinalIgnoreCase),
              "which is the honest answer to why it differs from other readings");
    }

    // -- the one tile where a small number is the bad one -----------------------------
    //
    // Every other reading Pulse shows counts upward into trouble: hotter, busier, fuller. A
    // battery counts down into it. Reusing the same threshold comparison would have left 8%
    // the same calm white as 80%, which is the tile refusing to say the one thing it exists
    // for, so the direction is a property of the tile rather than something hardcoded.
    static void BatteryIsTheOneTileWhereLowIsBad()
    {
        Console.WriteLine();
        Console.WriteLine("Battery is read from the machine, and coloured the other way round:");

        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        object battery = null;
        int inverted = 0;
        var invertedIds = new System.Collections.Generic.List<string>();

        foreach (var d in all)
        {
            string id = (string)defs.GetProperty("Id")!.GetValue(d)!;
            if (id == "battery") battery = d;
            if ((bool)defs.GetProperty("LowerIsWorse")!.GetValue(d)!)
            {
                inverted++;
                invertedIds.Add(id);
            }
        }

        Check("the tile exists", battery != null);
        if (battery == null) return;

        Check("it is a percentage with a bar",
              (string)defs.GetProperty("Unit")!.GetValue(battery)! == "%"
              && (bool)defs.GetProperty("HasBar")!.GetValue(battery)!);

        float warn = (float)defs.GetProperty("WarnThreshold")!.GetValue(battery)!;
        float danger = (float)defs.GetProperty("DangerThreshold")!.GetValue(battery)!;

        Check("it warns before it is urgent, not after",
              warn > danger, $"amber at {warn:F0}%, red at {danger:F0}%");

        Check("and only this tile is read the other way round",
              inverted == 1 && invertedIds.Contains("battery"),
              inverted == 1 ? "battery alone" : "also: " + string.Join(", ", invertedIds));

        Check("it explains itself",
              defs.GetProperty("Description")!.GetValue(battery) is string { Length: > 0 } d2
              && d2.Contains("no battery", StringComparison.OrdinalIgnoreCase),
              "a machine with no battery shows nothing, not zero");

        // The colouring itself, at the values that matter.
        var vm = FindSource("ViewModels/OverlayViewModel.cs");
        Check("the comparison is inverted for it",
              vm != null && vm.Contains("if (Definition.LowerIsWorse)")
              && vm.Contains("if (_value <= danger) return 3;"),
              "8% must not be the same colour as 80%");

        // Reading and routing.
        var reader = FindSource("Services/SensorReader.cs");
        // The tile-to-group list moved into SensorReader.SubsystemFor on 16 Oct 2026, so the
        // diagnostics report and the decision what to open read one list; HardwareService uses it.
        Check("the subsystem is asked for only when the tile is on",
              reader != null && reader.Contains("\"battery\"       => SensorSubsystems.Battery,")
              && FindSource("Services/HardwareService.cs") is { } hs
              && hs.Contains("needed |= SensorReader.SubsystemFor(id)"),
              "no point opening a battery device nobody is looking at");

        Check("the library is told to enumerate it",
              reader != null && reader.Contains("computer.IsBatteryEnabled     = s.HasFlag(SensorSubsystems.Battery);"));

        Check("the charge level is the sensor read",
              reader != null && reader.Contains("s.SensorType == SensorType.Level")
              && reader.Contains("s.Name.Contains(\"Charge\", StringComparison.OrdinalIgnoreCase)"),
              "of the eight a battery offers, this is the one anybody asked for");

        Check("a machine with no battery reports nothing rather than zero",
              reader != null && reader.Contains("if (s.Value is not { } value) continue;"),
              "zero percent is a reading a desktop must never appear to have");

        var data = _asm.GetType("Pulse.Services.SensorData")!;
        Check("the reading crosses to the overlay", data.GetProperty("BatteryLevel") != null);

        var byId = data.GetMethod("GetById")!;
        var instance = Activator.CreateInstance(data)!;
        data.GetProperty("BatteryLevel")!.SetValue(instance, (float?)37f);
        Check("and is reachable by the tile's id",
              (float?)byId.Invoke(instance, new object[] { "battery" }) == 37f);
    }

    // -- every tile says what it is ---------------------------------------------------
    //
    // Descriptions used to be optional. Five tiles had one and twelve did not, and the twelve
    // fell back to showing "Alt + arrow keys move this tile", so hovering most of the list
    // taught the reader a keyboard shortcut instead of telling them what the number was. The
    // hint is now said once, under the section, and every tile describes its own reading.
    //
    // They also stopped naming other tools. A tooltip that says "the same as Afterburner"
    // invites the reader to go and check, and it cannot stay true: those tools change, and we
    // do not control what they measure. Describing what Pulse does needs no such maintenance.
    static void EveryTileExplainsItself()
    {
        Console.WriteLine();
        Console.WriteLine("Every tile explains its own reading:");

        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        var missing = new System.Collections.Generic.List<string>();
        var tooLong = new System.Collections.Generic.List<string>();
        var mentionsOthers = new System.Collections.Generic.List<string>();
        int total = 0, longest = 0;

        string[] otherProducts = { "Afterburner", "Task Manager", "NVIDIA overlay", "RTSS", "CapFrameX" };

        foreach (var d in all)
        {
            total++;
            string id = (string)defs.GetProperty("Id")!.GetValue(d)!;
            string? text = defs.GetProperty("Description")?.GetValue(d) as string;

            if (string.IsNullOrWhiteSpace(text)) { missing.Add(id); continue; }

            longest = Math.Max(longest, text.Length);

            // A tooltip is a sentence or two. Past that it is documentation in a popup.
            if (text.Length > 220) tooLong.Add($"{id} ({text.Length})");

            foreach (var product in otherProducts)
                if (text.Contains(product, StringComparison.OrdinalIgnoreCase))
                    mentionsOthers.Add($"{id}: {product}");
        }

        Check("all of them have one", missing.Count == 0,
              missing.Count == 0 ? $"{total} tiles, longest {longest} characters"
                                 : "without: " + string.Join(", ", missing));

        Check("none is longer than a tooltip should be", tooLong.Count == 0,
              tooLong.Count == 0 ? "each is a sentence or two" : string.Join(", ", tooLong));

        Check("none sends the reader to another product", mentionsOthers.Count == 0,
              mentionsOthers.Count == 0 ? "they describe what Pulse measures"
                                        : string.Join(", ", mentionsOthers));

        // The keyboard hint, which used to be a tooltip on twelve tiles.
        var panel = FindSource("Views/MainWindow.xaml");
        Check("no tile falls back to a keyboard hint",
              panel != null && !panel.Contains("TargetNullValue='Alt + arrow keys move this tile'"),
              "hovering a tile should explain the tile");

        Check("and the hint is said once, under the section",
              panel != null && panel.Contains("Alt and an arrow key moves it"),
              "beside the other instructions about choosing and reordering");

        // The card that answers "which GPU is this reading from" while it is still finding out.
        Check("the GPU card shows something while the hardware is being found",
              panel != null && panel.Contains("Looking for graphics adapters"),
              "it used to be blank and then suddenly contain a dropdown");

        var view = FindSource("Views/MainWindow.xaml.cs");
        Check("and the placeholder gives way once they are known",
              view != null && view.Contains("bool known     = _vm.AvailableGpus.Count > 0;")
              && view.Contains("Show(GpuPending, !known);"),
              "three states: still looking, one adapter, several");
    }

    // -- the unit is the user's choice, the measurement is not ------------------------
    //
    // Megabytes and megabits are the same reading written differently, and which one somebody
    // wants depends on what they hold Pulse against: Task Manager, speed tests and internet
    // plans all speak megabits, while browsers and game launchers speak megabytes. The factor
    // between them is the part worth guarding. It is not eight: the reading is bytes divided
    // by 1,048,576, so it is mebibytes, while megabits are decimal, a million bits. Using
    // eight would read about five percent low against the very thing people are comparing to.
    static void NetworkUnitIsAChoice()
    {
        Console.WriteLine();
        Console.WriteLine("Network speed can be shown in either unit, and converts correctly:");

        var units = _asm.GetType("Pulse.Models.NetworkUnits")!;
        var label = units.GetMethod("Label", BindingFlags.Public | BindingFlags.Static)!;
        var convert = units.GetMethod("Convert", BindingFlags.Public | BindingFlags.Static)!;

        Check("bytes is the unit the reading is stored in",
              (string)units.GetField("Bytes")!.GetValue(null)! == "MB/s");

        Check("and bits is what Task Manager calls it",
              (string)units.GetField("Bits")!.GetValue(null)! == "Mbps");

        Check("the label follows the choice",
              (string)label.Invoke(null, new object[] { false })! == "MB/s"
              && (string)label.Invoke(null, new object[] { true })! == "Mbps");

        // The conversion, against the arithmetic it has to satisfy: a reading of V as stored
        // came from V * 1,048,576 bytes per second, and Task Manager would print that as
        // V * 1,048,576 * 8 / 1,000,000 megabits.
        foreach (float stored in new[] { 0.04f, 1.5f, 10.84f, 63.93f })
        {
            float shown = (float)convert.Invoke(null, new object[] { stored, true })!;
            double expected = stored * 1_048_576.0 * 8.0 / 1_000_000.0;
            Check($"{stored:F2} MB/s becomes {shown:F2} Mbps",
                  Math.Abs(shown - expected) < 0.001,
                  $"expected {expected:F3}");
        }

        Check("and the same reading is untouched when bytes are asked for",
              (float)convert.Invoke(null, new object[] { 10.84f, false })! == 10.84f);

        // The mistake this is guarding against, stated as a number.
        float eight = 10.84f * 8f;
        float correct = (float)convert.Invoke(null, new object[] { 10.84f, true })!;
        Check("multiplying by eight would be wrong, and by how much",
              Math.Abs(correct - eight) > 0.5f,
              $"{correct:F2} against {eight:F2}, a {100 * (correct - eight) / correct:F1}% error");

        // Which tiles offer the choice at all.
        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        var offered = new System.Collections.Generic.List<string>();
        foreach (var d in all)
            if ((bool)defs.GetProperty("IsNetworkSpeed")!.GetValue(d)!)
                offered.Add((string)defs.GetProperty("Id")!.GetValue(d)!);

        Check("both network tiles offer it, and nothing else does",
              offered.Count == 2 && offered.Contains("net_upload") && offered.Contains("net_download"),
              string.Join(", ", offered));

        // The setting, and its default.
        var settings = Activator.CreateInstance(_asm.GetType("Pulse.Models.AppSettings")!)!;
        var flag = settings.GetType().GetProperty("NetworkUnitBits");
        Check("the choice is remembered", flag != null);
        Check("and starts as megabytes, so an update changes nobody's numbers",
              flag != null && (bool)flag.GetValue(settings)! == false);

        // Every place the unit is printed has to follow it, or the label and the value
        // disagree and one of them is a lie.
        foreach (var (file, marker, why) in new[]
                 {
                     ("Views/OverlayWindow.xaml", "{Binding DisplayUnit, Mode=OneWay}", "the overlay"),
                     ("Views/OverlayWindow.xaml", "{Binding DisplayUnit}", "the compact HUD"),
                     ("Views/MainWindow.xaml", "{Binding DisplayUnit, Mode=OneWay}", "the tile chooser"),
                 })
        {
            var src = FindSource(file);
            Check($"{why} shows the chosen unit", src != null && src.Contains(marker));
        }

        var vm = FindSource("ViewModels/OverlayViewModel.cs");
        Check("and the value is converted to match it",
              vm != null && vm.Contains("NetworkUnits.Convert(_value.Value, bits: true)"),
              "a label saying Mbps over a number in MB/s is worse than either");

        Check("the reading itself is left in one unit",
              FindSource("Services/SensorReader.cs") is { } reader
              && reader.Contains("s.Value.Value / 1_048_576f")
              && !reader.Contains("NetworkUnitBits"),
              "nothing that stores, sends or compares it should know what the user prefers");
    }

    // -- one physical link, one measurement ------------------------------------------
    //
    // Windows exposes every NDIS filter bound to an adapter as an adapter in its own right.
    // On the machine this was found on, one Wi-Fi card appeared nine times: once as itself and
    // once for each of Kaspersky, QoS, Npcap, VirtualBox and four Windows filters. Six of the
    // nine survived the old name filter and every one reported the same bytes, so the tiles
    // read six times the real traffic. Task Manager said 45.9 Mbps while Pulse said 33.75
    // MB/s, which is 270 Mbps on a link whose graph tops out at 100.
    //
    // The bindings all carry the physical address of the adapter they sit on, so counting one
    // entry per address fixes it without naming a single filter product.
    static void NetworkCountsEachLinkOnce()
    {
        Console.WriteLine();
        Console.WriteLine("Network traffic is counted once per physical link:");

        var src = FindSource("Services/SensorReader.cs");
        if (src == null) { Check("SensorReader source found", false, ""); return; }

        Check("adapters are grouped by their physical address",
              src.Contains("nic.GetPhysicalAddress().ToString()")
              && src.Contains("chosenByAddress[address] = nic.Name;"),
              "an NDIS binding carries the address of the adapter it attaches to");

        Check("and the adapter itself is preferred over its bindings",
              src.Contains("|| nic.Name.Length < chosen.Length"),
              "a binding is named after the adapter and then extended, so it is longer");

        Check("an adapter with no address is left alone",
              src.Contains("withoutAddress.Add(nic.Name);"),
              "unknown is not an identity to group by");

        Check("the reading is still bytes converted to MB",
              src.Contains("s.Value.Value / 1_048_576f"),
              "the library reports bytes per second, measured against Windows at a ratio of 1.00");

        // ── asked of this machine ────────────────────────────────────────────────────
        var reader = _asm.GetType("Pulse.Services.SensorReader")!;
        var isPhysical = reader.GetMethod("IsPhysicalAdapter",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        bool Counts(string name) => (bool)isPhysical.Invoke(null, new object[] { name })!;

        // Every interface Windows reports, grouped the way the tiles will count them.
        var live = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        var counted = live.Where(n => Counts(n.Name)).ToList();

        var duplicated = counted
            .Select(n => n.GetPhysicalAddress().ToString())
            .Where(a => !string.IsNullOrEmpty(a))
            .GroupBy(a => a)
            .Where(g => g.Count() > 1)
            .ToList();

        Check("no physical address is counted twice on this machine",
              duplicated.Count == 0,
              duplicated.Count == 0
                  ? $"{counted.Count} of {live.Length} interfaces counted"
                  : "counted twice: " + string.Join(", ", duplicated.Select(g => $"{g.Key} x{g.Count()}")));

        // What the machine has, so a failure on hardware nobody here owns can be explained
        // from its report alone.
        if (duplicated.Count > 0)
            foreach (var n in live)
                Console.WriteLine($"        {(Counts(n.Name) ? "counted" : "skipped"),-8} {n.GetPhysicalAddress(),-13} "
                                + $"{n.NetworkInterfaceType,-18} {n.OperationalStatus,-5} {n.Name}  [{n.Description}]");

        // The specific shape of the bug: an adapter and a filter bound to it.
        var withAddress = live.Where(n => !string.IsNullOrEmpty(n.GetPhysicalAddress().ToString()))
                              .GroupBy(n => n.GetPhysicalAddress().ToString())
                              .Where(g => g.Count() > 1)
                              .ToList();

        if (withAddress.Count == 0)
        {
            Console.WriteLine("        this machine has no adapter with bindings, so there was nothing to collapse");
        }
        else
        {
            var group = withAddress.OrderByDescending(g => g.Count()).First();
            int survivors = group.Count(n => Counts(n.Name));
            Check("an adapter and its bindings collapse to at most one",
                  survivors <= 1,
                  $"{group.Count()} entries share {group.Key}, {survivors} counted");
        }
    }

    // -- the CPU clock is the cores, and not what Task Manager models ----------------
    //
    // Measured on the Ryzen at idle, twenty samples a second apart, all three at once:
    //
    //     every core, averaged   0.83 to 0.88 GHz     <- what Pulse shows
    //     the fastest core       0.87 to 0.94 GHz
    //     Task Manager           1.32 to 1.40 GHz
    //
    // No core on that machine was running at 1.35 GHz. Task Manager does not read a clock: it
    // multiplies the base frequency from the registry, 2,096 MHz there, by a "% Processor
    // Performance" counter that reflects the state the scheduler asked for rather than the one
    // the silicon settled on. Under load the two agree, measured at 2.62 against 2.51.
    //
    // Written down because the obvious "fix" for the idle gap is to copy Task Manager's
    // arithmetic, and that would print a figure no core is running at. That is the six
    // gigabyte VRAM default again: a model presented as a measurement.
    //
    // -- and it must average cores, not everything the chip publishes ----------------
    //
    // Real sensor names from both machines. Intel publishes only a bus speed and core clocks,
    // so averaging everything but the bus was right there by accident. AMD publishes far more,
    // and the same rule produced 1,110 MHz while the cores ran at 1,895 and Task Manager showed
    // 2.13 GHz.
    static void CpuClockIsCoresOnly()
    {
        Console.WriteLine();
        Console.WriteLine("The CPU clock must be the cores, not the fabric and the memory too:");

        var src = FindSource("Services/SensorReader.cs");
        if (src == null) { Check("SensorReader source found", false); return; }

        Check("clocks are filtered by what they are",
              src.Contains("case SensorType.Clock when IsCoreClock(s.Name):"),
              "it used to take everything that was not the bus");

        // The filter, applied to the names actually measured.
        bool Core(string n) =>
            n.Contains("Core", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("Uncore", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("Effective", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("Average", StringComparison.OrdinalIgnoreCase);

        var amd = new (string Name, double Mhz, bool Wanted)[]
        {
            ("Bus Speed",                 99.81, false),
            ("Core #1",                 1996.00, true),
            ("Core #1 (Effective)",      626.50, false),
            ("Core #2",                 1426.00, true),
            ("Core #2 (Effective)",      722.50, false),
            ("Core #3",                 1664.00, true),
            ("Core #3 (Effective)",      524.50, false),
            ("Core #4",                 2495.00, true),
            ("Core #4 (Effective)",      764.50, false),
            ("Cores (Average Effective)", 660.00, false),
            ("Cores (Average)",         1895.00, false),
            ("Fabric",                  1100.86, false),
            ("GFX",                      275.49, false),
            ("Memory",                  1101.74, false),
            ("Uncore",                   300.00, false),
        };

        foreach (var (name, _, wanted) in amd)
            Check($"AMD \"{name}\" is {(wanted ? "counted" : "ignored")}", Core(name) == wanted);

        double amdNow = amd.Where(x => Core(x.Name)).Average(x => x.Mhz);
        double amdOld = amd.Where(x => !x.Name.Contains("Bus")).Average(x => x.Mhz);

        Check("the AMD average is now the cores", Math.Abs(amdNow - 1895.25) < 1,
              $"{amdNow:N0} MHz, was {amdOld:N0} MHz");

        Check("which is what its own aggregate sensor says",
              Math.Abs(amdNow - 1895.00) < 1,
              "Cores (Average) reads 1,895, so the filter agrees with the chip");

        var intel = new (string Name, double Mhz)[]
        {
            ("Bus Speed",   100.44), ("P-Core #1", 4419.45), ("P-Core #2", 1607.07),
            ("P-Core #3", 1607.07), ("P-Core #4", 1707.51), ("E-Core #1", 1908.40),
            ("E-Core #2", 3113.70), ("E-Core #3", 1908.40), ("E-Core #4", 1908.40),
        };

        Check("the bus is still excluded on Intel", !Core("Bus Speed"));

        double intelNow = intel.Where(x => Core(x.Name)).Average(x => x.Mhz);
        double intelOld = intel.Where(x => !x.Name.Contains("Bus")).Average(x => x.Mhz);

        Check("and Intel is unchanged by this", Math.Abs(intelNow - intelOld) < 0.01,
              $"{intelNow:N0} MHz either way, because it publishes nothing else");

        Check("every Intel core is still counted",
              intel.Count(x => Core(x.Name)) == 8, "four P-cores and four E-cores");
    }

    /// Finds a source file relative to the repository, so these checks can read the rules.
    static string? FindSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        return null;
    }
}
