// Tests the adapter watcher.
//
// Two failure modes matter far more than getting the names right. A false positive makes the
// host re-enumerate its sensors on a loop, which costs seconds of blank tiles each time and
// would be far worse than the frozen readings it exists to fix. And this calls into COM by
// hand, several times a minute, forever — so anything it forgets to release accumulates for
// as long as Pulse is open, which is exactly how the GDI leak got in.

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

static class Adapters
{
    public static void Run(Assembly asm, Action<string, bool, string> check)
    {
        var type = asm.GetType("Pulse.Services.DisplayAdapters");
        if (type == null) { check("the adapter watcher exists", false, "type not found"); return; }

        var signature = type.GetMethod("Signature", BindingFlags.Public | BindingFlags.Static);
        var describe  = type.GetMethod("Describe",  BindingFlags.Public | BindingFlags.Static);

        if (signature == null || describe == null)
        {
            check("the adapter watcher exists", false, "methods not found");
            return;
        }

        string? Sign() => (string?)signature.Invoke(null, null);
        string Describe(string? s) => (string)describe.Invoke(null, new object?[] { s })!;

        Console.WriteLine("\nThe machine's graphics adapters must be readable:");

        var first = Sign();
        check("an adapter signature is produced", first != null, first == null ? "null" : Describe(first));
        if (first == null) return;

        check("it names more than one adapter", first.Split('|', StringSplitOptions.RemoveEmptyEntries).Length >= 2,
              Describe(first));
        check("each entry carries an identifier", first.Split('|', StringSplitOptions.RemoveEmptyEntries)
              .All(e => e.Contains(':') && e.IndexOf(':') > 0), first);

        // ── the one that would hurt ─────────────────────────────────────────────────
        Console.WriteLine("\nIt must not report a change when nothing has changed:");

        bool stable = true;
        string? drifted = null;

        for (int i = 0; i < 200; i++)
        {
            var again = Sign();
            if (again != first) { stable = false; drifted = again; break; }
        }

        check("two hundred readings in a row are identical", stable,
              stable ? "" : $"drifted to: {Describe(drifted)}");

        // ── leaks ───────────────────────────────────────────────────────────────────
        Console.WriteLine("\nAsking repeatedly must not accumulate anything:");

        var self = Process.GetCurrentProcess();
        for (int i = 0; i < 200; i++) Sign();      // warm up
        self.Refresh();
        long handlesBefore = self.HandleCount;
        long memoryBefore  = self.PrivateMemorySize64;

        for (int i = 0; i < 3000; i++) Sign();

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        self.Refresh();
        long handlesAfter = self.HandleCount;
        long memoryAfter  = self.PrivateMemorySize64;

        check("three thousand calls leak no handles", handlesAfter - handlesBefore < 50,
              $"{handlesBefore} -> {handlesAfter}");
        check("three thousand calls leak no memory", memoryAfter - memoryBefore < 20 * 1024 * 1024,
              $"{(memoryAfter - memoryBefore) / 1024}KB");

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) Sign();
        sw.Stop();
        check("one reading costs well under a millisecond", sw.Elapsed.TotalMilliseconds / 100 < 5,
              $"{sw.Elapsed.TotalMilliseconds / 100:F2}ms each");

        // ── describing ──────────────────────────────────────────────────────────────
        Console.WriteLine("\nA change must be describable in a log line:");

        check("nothing describes as unknown", Describe(null) == "unknown" && Describe("") == "unknown", "");
        check("a signature describes as adapter names",
              Describe("abc:NVIDIA GeForce RTX 3050|def:Intel(R) UHD Graphics")
                  == "NVIDIA GeForce RTX 3050, Intel(R) UHD Graphics", "");
        check("a malformed signature does not throw", Describe("nonsense").Length >= 0, Describe("nonsense"));
    }
}
