// Tests the 1.3.2 stability changes against the real Pulse.dll, by reflection.
//
// Each section names the failure it exists for. Where a change could be checked two ways, it is
// checked against what actually happens (a real locked file, a real no-execute DLL, the real
// clock) rather than against a description of what should.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static int _pass, _fail;
    static Assembly _asm;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static Type T(string name) => _asm.GetType(name, throwOnError: true);
    static object CallStatic(string type, string method, params object[] args) =>
        T(type).GetMethods(Any).First(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(null, args);

    [STAThread]
    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }
        _asm = Assembly.LoadFrom(dll);

        Console.WriteLine("1.3.2 stability\n");

        UpdateSchedule();
        NetworkFailuresAreNotBugs();
        WatchdogPatience();
        AwakeClockIgnoresNothingItShouldNot();
        PauseDetection();
        GraphicsPowerState();
        SleepingCardOnTheWire();
        CornerDisplayChoice();
        SettingsReadStates();
        SettingsGuardEndToEnd();
        ProbeSeesExecuteDenied();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ── update checks ────────────────────────────────────────────────────────────────

    static void UpdateSchedule()
    {
        Console.WriteLine("Update checks: soon while failing, daily once working, never hammering GitHub:");

        TimeSpan At(int failures) => (TimeSpan)CallStatic("Pulse.Services.UpdateService", "NextCheckIn", failures);

        Check("after a success, the next check is a day away", At(0) == TimeSpan.FromHours(24), At(0).ToString());
        Check("after the first failure, half a minute", At(1) == TimeSpan.FromSeconds(30), At(1).ToString());

        bool growing = true;
        for (int f = 1; f < 12; f++) if (At(f + 1) < At(f)) growing = false;
        Check("the wait never shrinks as failures continue", growing);

        Check("it settles at an hour, however long it fails", At(6) == TimeSpan.FromHours(1) && At(1000) == TimeSpan.FromHours(1),
              $"{At(6)} / {At(1000)}");
        Check("a negative count is treated as a success, not a tight loop", At(-5) == TimeSpan.FromHours(24));

        // A whole day offline, simulated: GitHub allows sixty unauthenticated requests an hour.
        var elapsed = TimeSpan.Zero; int attempts = 0, worstHour = 0;
        var times = new List<TimeSpan>();
        for (int f = 1; elapsed < TimeSpan.FromDays(1); f++) { elapsed += At(f); attempts++; times.Add(elapsed); }
        for (int i = 0; i < times.Count; i++)
            worstHour = Math.Max(worstHour, times.Count(t => t >= times[i] && t < times[i] + TimeSpan.FromHours(1)));
        Check("a machine offline for a day makes a couple of dozen attempts", attempts <= 30, $"{attempts} attempts");
        Check("and never more than a handful in any hour", worstHour <= 6, $"worst hour {worstHour}");
    }

    static void NetworkFailuresAreNotBugs()
    {
        Console.WriteLine("\nNo network is a warning; a reply Pulse cannot read is an error:");

        bool Net(Exception e) => (bool)CallStatic("Pulse.Services.UpdateService", "IsNetworkFailure", e);

        Check("no such host", Net(new HttpRequestException("No such host is known.")));
        Check("the client's own timeout", Net(new TaskCanceledException()));
        Check("a socket refused", Net(new System.Net.Sockets.SocketException(10061)));
        Check("a JSON reply Pulse cannot parse is not a network failure", !Net(new Newtonsoft.Json.JsonReaderException("bad")));
        Check("a bug in Pulse is not a network failure", !Net(new NullReferenceException()));
        Check("nor is a bad argument", !Net(new ArgumentException()));
    }

    // ── sensor host watchdog ─────────────────────────────────────────────────────────

    static void WatchdogPatience()
    {
        Console.WriteLine("\nWatchdog patience grows with each silent replacement, within a cap:");

        TimeSpan P(double seconds, int n) =>
            (TimeSpan)CallStatic("Pulse.Services.HardwareService", "Patience", TimeSpan.FromSeconds(seconds), n);

        Check("no history: the ordinary allowance", P(49, 0) == TimeSpan.FromSeconds(49), P(49, 0).ToString());
        Check("one silent replacement: twice as long", P(49, 1) == TimeSpan.FromSeconds(98), P(49, 1).ToString());
        Check("opening a replacement after one: 180s", P(90, 1) == TimeSpan.FromSeconds(180), P(90, 1).ToString());
        Check("capped at five minutes", P(49, 50) == TimeSpan.FromMinutes(5), P(49, 50).ToString());
        Check("an allowance already past the cap is not cut short", P(400, 3) == TimeSpan.FromSeconds(400), P(400, 3).ToString());
        Check("a negative count is no history", P(49, -2) == TimeSpan.FromSeconds(49), P(49, -2).ToString());

        bool monotone = true;
        for (int n = 0; n < 20; n++) if (P(49, n + 1) < P(49, n)) monotone = false;
        Check("never shrinks as replacements continue", monotone);

        var opening = (TimeSpan)T("Pulse.Services.HardwareService").GetField("OpenAllowance", Any).GetValue(null);
        var silent  = (TimeSpan)T("Pulse.Services.HardwareService").GetField("SilentAfter", Any).GetValue(null);
        Check("opening gets more time than the forty-two seconds measured under load", opening > TimeSpan.FromSeconds(42), opening.ToString());
        Check("and more than an opened host gets between readings", opening > silent, $"{opening} vs {silent}");

        var banner = (string)T("Pulse.Services.SensorHost").GetField("OpenedBanner", Any).GetValue(null);
        var ready  = (string)T("Pulse.Services.SensorHost").GetField("ReadyBanner", Any).GetValue(null);
        Check("the opened banner is not JSON, so the snapshot parser ignores it", !banner.TrimStart().StartsWith("{"), banner);
        Check("and it cannot be mistaken for the ready banner", banner != ready);
    }

    static void AwakeClockIgnoresNothingItShouldNot()
    {
        Console.WriteLine("\nThe awake clock keeps time while awake:");

        long Now() => (long)T("Pulse.Services.AwakeClock").GetProperty("Milliseconds", Any).GetValue(null);

        long a = Now(), t = Environment.TickCount64;
        Thread.Sleep(1500);
        long b = Now(), u = Environment.TickCount64;

        Check("it moves forward", b > a, $"{a} -> {b}");
        Check("at the same rate as the ordinary clock while awake", Math.Abs((b - a) - (u - t)) < 100,
              $"awake {b - a} ms, tick {u - t} ms");
        Check("and it counts from boot, like the ordinary clock", Math.Abs(a - t) < TimeSpan.FromDays(365).TotalMilliseconds && a > 0, $"{a}");
        // It must be behind or level with TickCount64, which also counts sleep. Equal on a machine
        // that has never slept since boot.
        Check("never ahead of a clock that also counts sleep", a <= t + 50, $"awake {a}, tick {t}");
    }

    static void PauseDetection()
    {
        Console.WriteLine("\nA timer that did not tick is noticed, one that did is not:");

        var type = T("Pulse.Services.PauseDetector");
        var detector = Activator.CreateInstance(type, TimeSpan.FromMilliseconds(300));
        TimeSpan Tick() => (TimeSpan)type.GetMethod("Check").Invoke(detector, null);

        Check("ticking on time is not a pause", Tick() == TimeSpan.Zero);
        Thread.Sleep(100);
        Check("nor is a tick a little late", Tick() == TimeSpan.Zero);

        Thread.Sleep(900);
        var gap = Tick();
        Check("a gap past the threshold is reported", gap > TimeSpan.Zero, gap.ToString());
        Check("with its real length", gap >= TimeSpan.FromMilliseconds(850) && gap < TimeSpan.FromMilliseconds(2000), gap.ToString());
        Check("and only once", Tick() == TimeSpan.Zero);

        // Both watchdogs must use it, with a threshold well above their own interval.
        var hwField = T("Pulse.Services.HardwareService").GetField("_paused", Any);
        var fpsField = T("Pulse.Services.FpsService").GetField("_paused", Any);
        Check("the sensor watchdog has one", hwField != null && hwField.FieldType == type);
        Check("so does the frame rate watchdog", fpsField != null && fpsField.FieldType == type);
    }

    static void GraphicsPowerState()
    {
        Console.WriteLine("\nWhether a graphics adapter is asleep, asked of Windows:");

        bool? Asleep(string name) => (bool?)CallStatic("Pulse.Services.GpuPower", "IsAsleep", name);

        // The adapters this machine actually has, by the names Windows gives them.
        var names = new List<string>();
        using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            foreach (var o in searcher.Get()) names.Add((string)o["Name"]);
        Console.WriteLine("    adapters: " + string.Join(", ", names));

        foreach (var n in names)
        {
            var answer = Asleep(n);
            Check($"an answer for {n}", answer.HasValue, answer.HasValue ? (answer.Value ? "asleep" : "on") : "unknown");
        }

        Check("an adapter that does not exist is unknown, not asleep", Asleep("No Such Graphics 9000") == null);
        Check("an empty name is unknown", Asleep("") == null);
        Check("names match regardless of case and spacing",
              names.Count == 0 || Asleep("  " + names[0].ToUpperInvariant() + " ") == Asleep(names[0]));

        // Asked every poll, so it has to be cheap.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) foreach (var n in names) Asleep(n);
        var each = sw.Elapsed.TotalMilliseconds / Math.Max(1, 50 * names.Count);
        Check("each answer takes a few milliseconds at most", each < 10, $"{each:F2} ms");
    }

    static void SleepingCardOnTheWire()
    {
        Console.WriteLine("\nA sleeping card crosses from the sensor host to Pulse as asleep, with no old numbers:");

        var snapType  = T("Pulse.Services.SensorSnapshot");
        var protoType = T("Pulse.Services.SensorProtocol");
        var snapshot  = Activator.CreateInstance(snapType);
        var data      = snapType.GetProperty("Data").GetValue(snapshot);
        var dataType  = data.GetType();

        // What the card's own sensors still hold from before it slept.
        dataType.GetProperty("GpuAsleep").SetValue(data, true);
        dataType.GetProperty("GpuTemp").SetValue(data, (float?)52f);
        dataType.GetProperty("GpuUsage").SetValue(data, (float?)31f);
        dataType.GetProperty("GpuClock").SetValue(data, (float?)1500f);
        dataType.GetProperty("GpuPower").SetValue(data, (float?)18f);
        dataType.GetProperty("GpuVram").SetValue(data, (float?)0.4f);   // from Windows, still true

        var line   = (string)protoType.GetMethod("Serialize").Invoke(null, new[] { snapshot });
        var parsed = protoType.GetMethod("TryParse").Invoke(null, new object[] { line });
        var pd     = snapType.GetProperty("Data").GetValue(parsed);
        object F(string name) => dataType.GetProperty(name).GetValue(pd);

        Check("the asleep flag survives the trip", (bool)F("GpuAsleep"));
        Check("no temperature from before it slept", F("GpuTemp") == null, $"{F("GpuTemp")}");
        Check("no usage from before it slept", F("GpuUsage") == null, $"{F("GpuUsage")}");
        Check("no clock from before it slept", F("GpuClock") == null, $"{F("GpuClock")}");
        Check("no power from before it slept", F("GpuPower") == null, $"{F("GpuPower")}");
        Check("video memory, which Windows holds, is kept", F("GpuVram") is float v && Math.Abs(v - 0.4f) < 0.001f, $"{F("GpuVram")}");

        // And an awake card is untouched by any of this.
        dataType.GetProperty("GpuAsleep").SetValue(data, false);
        dataType.GetProperty("GpuTemp").SetValue(data, (float?)52f);
        var awake = protoType.GetMethod("TryParse").Invoke(null, new object[] { (string)protoType.GetMethod("Serialize").Invoke(null, new[] { snapshot }) });
        var ad = snapType.GetProperty("Data").GetValue(awake);
        Check("an awake card keeps its readings", dataType.GetProperty("GpuTemp").GetValue(ad) is float t && t == 52f);
    }

    // ── overlay display ──────────────────────────────────────────────────────────────

    static void CornerDisplayChoice()
    {
        Console.WriteLine("\nA corner position finds its display by where it is, then by number:");

        int Pick(string[] screens, string saved, int index) =>
            (int)CallStatic("Pulse.Views.OverlayWindow", "PickPresetScreen", screens.ToList(), saved, index);

        var laptop   = "0,0,1920,1080";
        var external = "1920,0,2560,1440";

        Check("the saved display, where Windows lists it first", Pick(new[] { external, laptop }, external, 1) == 0);
        Check("the saved display, after Windows swaps the order", Pick(new[] { laptop, external }, external, 0) == 1,
              "this is the renumbering case: the index points at the wrong screen");
        Check("no saved bounds: the index, as before 1.3.2", Pick(new[] { laptop, external }, "", 1) == 1);
        Check("saved bounds no longer present: the index", Pick(new[] { laptop, "0,0,3840,2160" }, external, 1) == 1);
        Check("an index past the end: nothing, so the caller falls back", Pick(new[] { laptop }, "", 3) == -1);
        Check("a negative index: nothing", Pick(new[] { laptop }, "", -1) == -1);
        Check("bounds must match exactly, not by prefix", Pick(new[] { "1920,0,2560,14400" }, external, -1) == -1);
    }

    // ── settings ─────────────────────────────────────────────────────────────────────

    static string ReadState(string path)
    {
        var tuple = CallStatic("Pulse.Models.AppSettings", "TryLoad", path);
        return tuple.GetType().GetField("Item2").GetValue(tuple).ToString();
    }

    static void SettingsReadStates()
    {
        Console.WriteLine("\nThree kinds of missing settings are told apart:");

        var dir = Path.Combine(Path.GetTempPath(), "pulse-stability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, "{ \"OverlayOpacity\": 0.5 }");
            Check("a readable file is Read", ReadState(good) == "Read", ReadState(good));

            Check("no file is Missing", ReadState(Path.Combine(dir, "none.json")) == "Missing");

            var bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ this is not json");
            Check("a file that is not JSON is Corrupt", ReadState(bad) == "Corrupt", ReadState(bad));

            var empty = Path.Combine(dir, "empty.json");
            File.WriteAllText(empty, "");
            Check("an empty file is Corrupt, not a crash", ReadState(empty) == "Corrupt", ReadState(empty));

            using (new FileStream(good, FileMode.Open, FileAccess.Read, FileShare.None))
                Check("a file held open with no sharing is Unreadable", ReadState(good) == "Unreadable", ReadState(good));

            Check("and Read again once it is released", ReadState(good) == "Read", ReadState(good));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void SettingsGuardEndToEnd()
    {
        Console.WriteLine("\nSaved settings that cannot be read are never overwritten with defaults:");

        var folder  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Refora", "Pulse");
        var main    = Path.Combine(folder, "settings.json");
        var bak     = main + ".bak";
        var keepMain = main + ".stability-backup";
        var keepBak  = bak + ".stability-backup";

        Directory.CreateDirectory(folder);
        bool hadMain = File.Exists(main), hadBak = File.Exists(bak);
        if (hadMain) File.Copy(main, keepMain, true);
        if (hadBak)  File.Copy(bak, keepBak, true);

        try
        {
            // Something a user set up, recognisable afterwards.
            const string mine = "{ \"OverlayOpacity\": 0.42, \"ActiveTileIds\": [\"ram_used\"] }";
            File.WriteAllText(main, mine);
            File.WriteAllText(bak, mine);

            object service;
            using (new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new FileStream(bak,  FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Built while both files are locked, exactly as at a bad logon.
                service = T("Pulse.Services.SettingsService").GetProperty("Instance", Any).GetValue(null);
                var settings = service.GetType().GetProperty("Settings").GetValue(service);

                Check("Pulse still starts, on defaults", settings != null);
                Check("and knows they are standing in for something",
                      (bool)settings.GetType().GetProperty("ReadFailed").GetValue(settings));

                // The startup task check would save at this point. So would any setting changed.
                var saved = (bool)service.GetType().GetMethod("Save").Invoke(service, null);
                Check("saving refuses while locked", !saved);
            }

            // Released now, the moment an unguarded save would have gone through.
            var saved2 = (bool)service.GetType().GetMethod("Save").Invoke(service, null);
            Check("saving still refuses once the file is readable again", !saved2,
                  "the defaults in memory are still defaults");

            Check("the user's settings are untouched", File.ReadAllText(main) == mine, File.ReadAllText(main));
            Check("and so is the backup", File.ReadAllText(bak) == mine, File.ReadAllText(bak));
            Check("the panel is told, with a reason that is not about disk space",
                  (bool)service.GetType().GetProperty("LastSaveFailed").GetValue(service));
        }
        finally
        {
            try
            {
                if (hadMain) File.Copy(keepMain, main, true); else File.Delete(main);
                if (hadBak)  File.Copy(keepBak, bak, true);   else File.Delete(bak);
                File.Delete(keepMain); File.Delete(keepBak);
            }
            catch (Exception ex) { Console.WriteLine("WARNING: could not restore settings: " + ex.Message); }
        }

        // The other half: a corrupt file has nothing to protect, so it must not block saving
        // forever. Checked on the loader directly, since the service is a singleton already built.
        var tmp = Path.Combine(Path.GetTempPath(), "pulse-corrupt-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(tmp, "not json at all");
        Check("a corrupt file does not block saving", ReadState(tmp) == "Corrupt");
        File.Delete(tmp);
    }

    // ── native library probe ─────────────────────────────────────────────────────────

    static void ProbeSeesExecuteDenied()
    {
        Console.WriteLine("\nThe native library probe tells readable apart from loadable:");

        string Describe(string path, bool asCode) =>
            (string)CallStatic("Pulse.Services.NativeLibraryProbe", "Describe", path, Path.GetFileName(path), asCode);

        var source = Directory.GetFiles(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App", "wpfgfx_cor3.dll",
                                        SearchOption.AllDirectories).FirstOrDefault();
        if (source == null) { Check("a WPF library to test with", false, "none found"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "pulse-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "wpfgfx_cor3.dll");
        File.Copy(source, copy);

        try
        {
            var normal = Describe(copy, true);
            Check("a normal copy maps as data and as code", normal.Contains("map=ok") && normal.Contains("exec=ok"), normal);

            var info = new FileInfo(copy);
            var acl = info.GetAccessControl();
            var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ExecuteFile, AccessControlType.Deny);
            acl.AddAccessRule(deny);
            info.SetAccessControl(acl);

            var noExec = Describe(copy, true);
            Check("with execute denied it still reads, which is why read=ok proved nothing",
                  noExec.Contains("read=ok") && noExec.Contains("map=ok"), noExec);
            Check("and the code mapping is what catches it, with access denied", noExec.Contains("exec=FAILED(5)"), noExec);

            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);

            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = Describe(copy, true);
                Check("a file held with no sharing is reported as a sharing violation",
                      locked.Contains("exec=FAILED(32)"), locked);
            }

            var export = Describe(copy, false);
            Check("the export, on a background thread, does not map as code", export.Contains("exec=not tried"), export);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
