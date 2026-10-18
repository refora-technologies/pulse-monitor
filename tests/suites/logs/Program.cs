// Tests Pulse's real LogService by reflecting into the built assembly.
//
// LogService writes to %APPDATA%\Refora\Pulse\logs, which is the user's genuine log folder,
// and the path is resolved through the shell so it cannot be redirected. The harness
// therefore moves the real folder aside first and puts it back at the end, and verifies the
// restore. Nothing of the user's is destroyed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static int _pass, _fail;
    static Type _log = null!;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Refora", "Pulse", "logs");

    static void Info(string src, string msg) => _log.GetMethod("Info")!.Invoke(null, new object[] { src, msg });
    static void Warn(string src, string msg) => _log.GetMethod("Warn")!.Invoke(null, new object[] { src, msg });
    static bool BeginSession(string v)       => (bool)_log.GetMethod("BeginSession")!.Invoke(null, new object[] { v })!;
    static void EndSession()                 => _log.GetMethod("EndSession")!.Invoke(null, null);
    static void RecordActivity(string a)     => _log.GetMethod("RecordActivity")!.Invoke(null, new object[] { a });
    static string? Export()                  => (string?)_log.GetMethod("Export")!.Invoke(null, null);

    static string Active   => Path.Combine(LogDir, "pulse.log");
    static string Archive(int i) => Path.Combine(LogDir, $"pulse.{i}.log");
    static string StateFile=> Path.Combine(LogDir, "session.state");

    static void ClearLogs()
    {
        if (!Directory.Exists(LogDir)) { Directory.CreateDirectory(LogDir); return; }
        foreach (var f in Directory.GetFiles(LogDir)) { try { File.Delete(f); } catch { } }
    }

    /// Writes until the active file has rotated `times` times.
    static void FillAndRotate(string marker, int times)
    {
        var padding = new string('x', 900);
        for (int r = 0; r < times; r++)
        {
            Info("Fill", $"{marker}-generation-{r}");
            long start = File.Exists(Active) ? new FileInfo(Active).Length : 0;
            int guard = 0;
            while ((File.Exists(Active) ? new FileInfo(Active).Length : 0) >= start && guard++ < 4000)
            {
                Info("Fill", padding);
                start = Math.Min(start, File.Exists(Active) ? new FileInfo(Active).Length : 0);
                if (!File.Exists(Active)) break;
                if (new FileInfo(Active).Length < 1000) break;   // just rotated
            }
        }
    }

    static int Main(string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }
        var pulse = Assembly.LoadFrom(dll);

        // ── be a sensor host when asked, exactly as Pulse.exe is ──────────────────────
        //
        // The export test asks LogService for a diagnostics report, and that report describes
        // the machine, which starts HardwareService, which starts its sensor host by launching
        // Environment.ProcessPath. Here that is this test. Without this the child re-ran the
        // whole suite against the same log folder while the parent was restoring it, which is
        // what left backup folders behind in %TEMP% and what made the export checks fail
        // intermittently and then pass on the retry.
        //
        // The flag is read off the product rather than written out here, so renaming it cannot
        // quietly disarm this.
        var host    = pulse.GetType("Pulse.Services.SensorHost")!;
        var hostArg = (string)host.GetField("Argument")!.GetValue(null)!;
        if (args.Contains(hostArg))
            return (int)host.GetMethod("Run")!.Invoke(null, new object[] { args })!;

        _log = pulse.GetType("Pulse.Services.LogService")!;

        // ── protect the user's real logs ──────────────────────────────────────────────
        string backup = Path.Combine(Path.GetTempPath(), "pulse-logs-backup-" + Guid.NewGuid().ToString("N"));
        bool hadRealLogs = Directory.Exists(LogDir);
        int realFileCount = 0;
        if (hadRealLogs)
        {
            realFileCount = Directory.GetFiles(LogDir).Length;
            Directory.CreateDirectory(backup);
            foreach (var f in Directory.GetFiles(LogDir))
                File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), true);
        }
        Console.WriteLine($"Pulse log tests   (backed up {realFileCount} existing files)\n");

        try
        {
            RunTests();
        }
        finally
        {
            // ── put the user's logs back ──────────────────────────────────────────────
            try
            {
                ClearLogs();
                if (hadRealLogs)
                    foreach (var f in Directory.GetFiles(backup))
                        File.Copy(f, Path.Combine(LogDir, Path.GetFileName(f)), true);

                int restored = Directory.Exists(LogDir) ? Directory.GetFiles(LogDir).Length : 0;
                Console.WriteLine();
                Check("the user's own logs were put back", restored == realFileCount,
                      $"{restored} of {realFileCount}");
                try { Directory.Delete(backup, true); } catch { }
            }
            catch (Exception ex) { Console.WriteLine("RESTORE FAILED: " + ex); }
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static void RunTests()
    {
        // ── durability ────────────────────────────────────────────────────────────────
        Console.WriteLine("A line must be on disk the moment it is written:");
        ClearLogs();
        Info("Durability", "written-then-read-immediately");
        string readBack;
        using (var fs = new FileStream(Active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs)) readBack = sr.ReadToEnd();
        Check("readable through a separate handle with no flush or close",
              readBack.Contains("written-then-read-immediately"));

        // ── redaction ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\nThe user's account name must not leak into a shareable file:");
        ClearLogs();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Info("Redact", $"opened {profile}\\Documents\\thing.txt");
        var text = File.ReadAllText(Active);
        Check("profile path replaced with %USERPROFILE%", text.Contains("%USERPROFILE%"));
        Check("raw profile path absent", !text.Contains(profile), profile);

        // ── retention ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\nHistory must survive many sessions, not just the last one:");
        ClearLogs();
        FillAndRotate("keep", 7);
        Info("Fill", "newest-line");

        var archives = Enumerable.Range(1, 8).Where(i => File.Exists(Archive(i))).ToList();
        Check("older logs are kept, not discarded", archives.Count >= 4, $"archives present: {string.Join(",", archives)}");
        Check("history is capped so it cannot grow without limit", !File.Exists(Archive(6)),
              File.Exists(Archive(6)) ? "pulse.6.log exists" : "nothing beyond pulse.5.log");

        long total = Directory.GetFiles(LogDir, "pulse*.log").Sum(f => new FileInfo(f).Length);
        Check("total on disk stays bounded", total < 8L * 1024 * 1024, $"{total / 1024 / 1024.0:F1} MB");

        Check("the newest line is in the active file", File.ReadAllText(Active).Contains("newest-line"));

        var oldestKept = File.ReadAllText(Archive(archives.Max()));
        Check("the oldest kept file still holds real content", oldestKept.Contains("Fill"));

        // ── crash detection ───────────────────────────────────────────────────────────
        Console.WriteLine("\nA session that never shut down must be reported on the next run:");
        ClearLogs();
        BeginSession("1.1.2-test");
        RecordActivity("reading the discrete GPU");
        Check("a session in progress leaves a marker", File.Exists(StateFile));

        // No EndSession: this is what a driver fault or a forced kill looks like.
        bool reported = BeginSession("1.1.2-test");
        Check("the next start reports the previous crash", reported);

        var afterCrash = File.ReadAllText(Active);
        Check("the report says what it was doing", afterCrash.Contains("reading the discrete GPU"),
              afterCrash.Contains("reading the discrete GPU") ? "activity carried through" : "activity missing");
        Check("it is recorded as a warning", afterCrash.Contains("WARN"));

        // ── clean exit ────────────────────────────────────────────────────────────────
        Console.WriteLine("\nA clean exit must not be mistaken for a crash:");
        ClearLogs();
        BeginSession("1.1.2-test");
        RecordActivity("idling");
        EndSession();
        Check("the marker is cleared on a clean exit", !File.Exists(StateFile));
        bool falseAlarm = BeginSession("1.1.2-test");
        Check("the next start reports no crash", !falseAlarm);
        EndSession();

        // ── concurrency ───────────────────────────────────────────────────────────────
        Console.WriteLine("\nLogging from many threads at once must not lose or corrupt lines:");
        ClearLogs();
        const int threads = 8, each = 150;
        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < each; i++) Info("Thread" + t, $"line-{t}-{i}");
        });
        var all = File.ReadAllLines(Active);
        int found = all.Count(l => l.Contains("line-"));
        Check("every line arrived", found == threads * each, $"{found} of {threads * each}");
        Check("no line was interleaved with another",
              all.Where(l => l.Contains("line-")).All(l => l.Split("line-").Length == 2));

        // ── export ────────────────────────────────────────────────────────────────────
        Console.WriteLine("\nThe exported report must stand on its own:");
        ClearLogs();
        FillAndRotate("history", 2);
        Info("Export", "current-session-line");
        BeginSession("1.1.2-test");
        RecordActivity("about to export");

        var path = Export();
        Check("an export file was produced", path != null && File.Exists(path));

        if (path != null && File.Exists(path))
        {
            var report = File.ReadAllText(path);
            Check("it names the version",            report.Contains("Version"));
            Check("it names the Windows build",      report.Contains("Windows"));
            Check("it reports the startup task",     report.Contains("Startup"));
            Check("it reports the GPU picture",      report.Contains("GPU"));
            Check("it includes the current log",     report.Contains("current-session-line"));
            // Older logs are counted rather than reprinted. Six log files pasted in whole is
            // up to six megabytes, which is an awkward thing to ask a user to send and a worse
            // thing to read. What has to survive is the history, not the wording of it.
            Check("older logs are represented rather than reprinted",
                  report.Contains("older log file(s)"),
                  "counted, with the span they cover and the versions that ran");

            Check("and their routine lines are not pasted in whole",
                  !report.Contains("history-generation-0"),
                  "the reason the file was six megabytes");
            Check("it shows the session in progress",report.Contains("about to export"));
            Check("it does not leak the account name", !report.Contains(profile));
            // Kept when anything failed, deleted otherwise. These export checks used to fail
            // intermittently and then pass on the retry, with the run-specific markers missing
            // while the generic sections were present. The cause was the missing sensor host
            // guard at the top of Main: this report starts the host, the host was a second copy
            // of this test, and it rewrote the log folder underneath the assertions. The report
            // still survives a failure rather than being deleted along with the evidence.
            if (_fail == 0) { try { File.Delete(path); } catch { } }
            else { Console.WriteLine("    export kept for inspection: " + path); }
        }

        EndSession();
    }
}
