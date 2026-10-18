// Tests that Pulse survives the failures it can survive, and stops when it cannot.
//
// Pulse lives in the tray with no window open most of the time. When it dies there is nothing
// on screen to say so: the overlay disappears, the tray icon disappears, and the user finds out
// by noticing an absence. That makes "keep running" worth more here than it would be in an
// ordinary application, and it makes an unbounded failure loop worse, because nobody would see
// that either.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

class Program
{
    static int _pass, _fail;
    static Assembly _asm;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    [STAThread]
    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }
        _asm = Assembly.LoadFrom(dll);

        Console.WriteLine("Resilience\n");

        SurvivingAFault();
        TheStormGuard();
        HandlersThatCannotReturnAFailure();
        ThePanelFollowsTheRealWorld();
        TimersOnTheRightThread();
        SayingNothingRatherThanSayingFalse();
        BoundedRendering();
        WhatIsDeliberatelyKept();
        EverySilentCatchIsAccountedFor();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        return "";
    }

    // ── one fault must not end the application ──────────────────────────────────────
    static void SurvivingAFault()
    {
        Console.WriteLine("A failure in the interface must not take the whole application down:");

        var app = Source("App.xaml.cs");

        Check("the dispatcher handler marks the exception handled",
              app.Contains("args.Handled = ShouldKeepRunningAfter();"),
              "it used to log and then let the process end");

        Check("and it is still written to the log first",
              app.Contains("Unhandled exception on the UI thread"),
              "surviving silently would hide the fault instead");

        // Deliberately unchanged: this one really is terminal.
        Check("a fault outside the interface still ends Pulse",
              app.Contains("Unhandled exception; Pulse is closing"),
              "AppDomain.UnhandledException cannot be recovered from");

        Check("the reason for staying alive is recorded",
              app.Contains("the overlay and the tray icon both vanish"),
              "so this is not mistaken for swallowing errors carelessly");
    }

    // ── but an endless stream of them must ──────────────────────────────────────────
    static void TheStormGuard()
    {
        Console.WriteLine();
        Console.WriteLine("An unending stream of failures must stop rather than spin:");

        var app = Source("App.xaml.cs");

        Check("there is a limit and a window",
              app.Contains("UiFaultLimit") && app.Contains("UiFaultWindowMs"),
              "otherwise a fault on every layout pass loops forever");

        // The real method, on a real App instance, rather than a copy of its arithmetic.
        var type = _asm.GetType("Pulse.App");
        var method = type?.GetMethod("ShouldKeepRunningAfter",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Check("the decision is a method that can be exercised", method != null);
        if (method == null) return;

        object instance;
        try
        {
            instance = Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            Check("an App can be constructed for the test", false, ex.GetType().Name);
            return;
        }

        int limit  = (int)type.GetField("UiFaultLimit",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.GetRawConstantValue();

        var answers = new List<bool>();
        for (int i = 0; i < limit + 3; i++)
            answers.Add((bool)method.Invoke(instance, null));

        int survived = 0;
        foreach (var a in answers) { if (a) survived++; else break; }

        Check($"the first {limit} failures in a row are survived",
              survived == limit,
              $"survived {survived} of {answers.Count} before giving up");

        Check("and the next one is not",
              answers.Count > limit && !answers[limit],
              "a fault repeating this fast cannot be recovered from");

        Check("nor is anything after it",
              !answers[answers.Count - 1],
              "it does not start accepting them again while the storm continues");

        // The window is what makes occasional faults survivable forever. Faults spread out over
        // a long session must never accumulate into a shutdown, which is the same mistake the
        // frame capture backoff used to make by counting restarts and never clearing them.
        // The same instance, reset. WPF permits only one Application per AppDomain, so a second
        // one cannot be built for this.
        var queue = (Queue<long>)type.GetField("_uiFaults",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
        queue.Clear();

        long windowMs = (long)type.GetField("UiFaultWindowMs",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.GetRawConstantValue();

        bool allSurvived = true;
        for (int i = 0; i < 50; i++)
        {
            // Age every recorded fault past the window, the way real time would between two
            // unrelated failures minutes apart.
            var aged = new Queue<long>();
            foreach (var t in queue) aged.Enqueue(t - windowMs - 1);
            queue.Clear();
            foreach (var t in aged) queue.Enqueue(t);

            if (!(bool)method.Invoke(instance, null)) allSurvived = false;
        }

        Check("fifty faults spread across a long session are all survived",
              allSurvived,
              "only a burst inside the window counts, not a lifetime total");
    }

    // ── handlers with nowhere to report a failure ───────────────────────────────────
    static void HandlersThatCannotReturnAFailure()
    {
        Console.WriteLine();
        Console.WriteLine("An async void handler must catch its own failures:");

        // These are the handlers whose exceptions have no caller to reach. They go straight to
        // the dispatcher, which is why they are worth listing explicitly rather than trusting
        // that nothing in them throws today.
        foreach (var (file, handler) in new[]
                 {
                     ("Views/MainWindow.xaml.cs", "BtnCheckUpdates_Click"),
                     ("Views/MainWindow.xaml.cs", "BtnUpdateNow_Click"),
                     ("Views/MainWindow.xaml.cs", "BtnExportDiagnostics_Click"),
                     ("App.xaml.cs",              "RunUpdateCheck"),
                 })
        {
            Check($"{handler} catches its own", Guarded(Source(file), handler),
                  "async void has no caller to hand an exception to");
        }

        // Every async void in the product, so a new one cannot be added unguarded.
        int unguarded = 0;
        var names = new List<string>();

        foreach (var file in new[] { "App.xaml.cs", "Views/MainWindow.xaml.cs",
                                     "Views/OverlayWindow.xaml.cs", "Views/WhatsNewWindow.xaml.cs",
                                     "ViewModels/SettingsViewModel.cs" })
        {
            var text = Source(file);
            int at = 0;

            while ((at = text.IndexOf("async void", at, StringComparison.Ordinal)) >= 0)
            {
                int open = text.IndexOf('(', at);
                at += 10;
                if (open < 0) continue;

                // A declaration, not the phrase used in a comment: the name has to be a plain
                // identifier sitting on the same line as its parameter list. Without this the
                // sweep matched a comment reading "from an async void click handler" and
                // reported a method called "click handler".
                var between = text.Substring(at, open - at).Trim();
                if (between.Length == 0 || between.IndexOf('\n') >= 0) continue;

                bool identifier = true;
                foreach (var c in between)
                    if (!char.IsLetterOrDigit(c) && c != '_') identifier = false;
                if (!identifier) continue;

                if (!Guarded(text, between)) { unguarded++; names.Add($"{file}:{between}"); }
            }
        }

        Check("no async void handler anywhere is left unguarded", unguarded == 0,
              unguarded == 0 ? "checked every one in the product" : string.Join(", ", names));
    }

    // ── the panel must not describe a machine that has changed ──────────────────────
    static void ThePanelFollowsTheRealWorld()
    {
        Console.WriteLine();
        Console.WriteLine("The control panel must keep up with displays being plugged and unplugged:");

        var main = Source("Views/MainWindow.xaml.cs");

        // The panel is hidden and re-shown rather than rebuilt, so anything read once at Loaded
        // stays as it was for the rest of the session.
        Check("the display buttons are rebuilt when the panel is shown again",
              Guarded(main, "IsVisibleChanged") is var _ &&
              main.Contains("PopulateMonitorButtons();")
              && main.IndexOf("if (args.NewValue is not true) return;", StringComparison.Ordinal) > 0,
              "Loaded fires once, and a monitor can be unplugged while the panel is away");

        Check("and while it is open, when Windows says the displays changed",
              main.Contains("SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged"),
              "plugging a monitor in and going straight to Pulse is the likely case");

        Check("the rebuild is marshalled onto the interface thread",
              main.Contains("Dispatcher.BeginInvoke(PopulateMonitorButtons)"),
              "SystemEvents raises from its own window, and this touches bound children");

        Check("and the static event is released when the panel closes",
              main.Contains("SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged"),
              "SystemEvents outlives every window, so this leaks harder than an instance event");

        // The GPU list already had this; the displays did not. Worth asserting together so the
        // pair cannot drift apart again.
        Check("the GPU list is still refreshed the same way",
              main.Contains("GpuListChanged += OnGpuListChanged")
              && main.Contains("GpuListChanged -= OnGpuListChanged"),
              "both lists describe hardware that can change while Pulse runs");

        Console.WriteLine();
        Console.WriteLine("Holding a position slider still must not let the overlay move:");

        var vm = Source("ViewModels/SettingsViewModel.cs");

        Check("a held thumb is tracked separately from the save debounce",
              vm.Contains("IsDraggingPositionSlider"),
              "a timer cannot tell a still thumb from a released one");

        Check("and either one is enough to hold the overlay",
              vm.Contains("IsDraggingPositionSlider || _positionSaveTimer is { IsEnabled: true }"),
              "the timer still covers arrow keys and clicks on the track");

        Check("the panel reports both ends of the drag",
              main.Contains("PositionSlider_DragStarted") && main.Contains("PositionSlider_DragCompleted"));

        var xaml = Source("Views/MainWindow.xaml");
        int wired = 0, at = 0;
        while ((at = xaml.IndexOf("Thumb.DragStarted=\"PositionSlider_DragStarted\"", at,
                                  StringComparison.Ordinal)) >= 0) { wired++; at += 10; }

        Check("both position sliders are wired up, not just one", wired == 2,
              $"{wired} of 2, and the other axis would still slip");

        Check("the overlay still refuses to re-place itself while positioning",
              Source("Views/OverlayWindow.xaml.cs").Contains("if (IsUserPositioning) return;"),
              "which is what the flag above feeds");
    }

    // ── a timer belongs to the thread that built it ─────────────────────────────────
    static void TimersOnTheRightThread()
    {
        Console.WriteLine();
        Console.WriteLine("A debounce timer must belong to the thread that can run it:");

        var vm = Source("ViewModels/SettingsViewModel.cs");

        Check("both debounce timers are built the same way",
              vm.Contains("_saveTimer = NewDebounceTimer();")
              && vm.Contains("_positionSaveTimer = NewDebounceTimer();"),
              "they were two separate lazy constructions with the same hazard");

        Check("and the dispatcher is named rather than inferred",
              vm.Contains("Application.Current?.Dispatcher"),
              "a DispatcherTimer belongs to whoever constructs it");

        Check("with a fallback for no application at all",
              vm.Contains("Dispatcher.CurrentDispatcher"),
              "so this stays constructible outside a running Pulse, as here");

        Check("neither builds a bare DispatcherTimer any more",
              !vm.Contains("new System.Windows.Threading.DispatcherTimer\n"),
              "the parameterless constructor is the one that captures the wrong thread");

        // The same fault, already answered differently elsewhere. Worth pinning so the two do
        // not drift into disagreeing about the problem.
        Check("frame capture still guards its own timer",
              Source("Services/FpsService.cs").Contains("belongs to whichever thread created it"),
              "same hazard, answered by making the caller responsible instead");
    }

    // ── a converter with no answer must not invent one ──────────────────────────────
    static void SayingNothingRatherThanSayingFalse()
    {
        Console.WriteLine();
        Console.WriteLine("A converter given no value must decline rather than guess:");

        var type = _asm.GetType("Pulse.Converters.InverseBoolConverter");
        Check("the converter exists", type != null);
        if (type == null) return;

        var converter = Activator.CreateInstance(type);
        var convert = type.GetMethod("Convert");

        object Run(object value)
            => convert.Invoke(converter, new object[] { value, typeof(bool), null, null });

        Check("true inverts to false", Run(true) is bool f && !f);
        Check("false inverts to true", Run(false) is bool t && t);

        // The value WPF passes while a binding is still being set up.
        var unset = System.Windows.DependencyProperty.UnsetValue;

        Check("an unset value is answered with no opinion",
              ReferenceEquals(Run(unset), unset),
              "it used to answer false, so IsEnabled went false on the way to true");

        Check("and so is anything else that is not a boolean",
              ReferenceEquals(Run("yes"), unset) && ReferenceEquals(Run(1), unset),
              "false is not the inverse of a string");
    }

    // ── text from the network must not build unbounded UI ───────────────────────────
    static void BoundedRendering()
    {
        Console.WriteLine();
        Console.WriteLine("Release notes must not build one element per line without limit:");

        var window = _asm.GetType("Pulse.Views.WhatsNewWindow");
        var parse = window?.GetMethod("ParseNotes", BindingFlags.Public | BindingFlags.Static);

        Check("the parser can be exercised", parse != null);
        if (parse == null) return;

        var lines = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++) lines.Append("- line ").Append(i).Append('\n');

        var blocks = (System.Collections.ICollection)parse.Invoke(null,
            new object[] { lines.ToString(), "1.2.0" });

        Check("five thousand lines do not become five thousand elements",
              blocks.Count < 500,
              $"{blocks.Count} blocks from 5000 lines");

        // Truncating silently would be its own bug: the notes would simply stop, and nothing
        // would say the rest existed.
        bool saysSo = false;
        foreach (var b in blocks)
        {
            var text = b.GetType().GetProperty("Text")?.GetValue(b) as string ?? "";
            if (text.Contains("shortened")) saysSo = true;
        }

        Check("and the reader is told they were shortened", saysSo,
              "stopping mid-sentence with no explanation is worse than a long list");

        // A real release note must be nowhere near the limit, or this would be trimming
        // something people are meant to read.
        var realistic = new System.Text.StringBuilder();
        for (int i = 0; i < 60; i++) realistic.Append("- a genuine change\n");

        var normal = (System.Collections.ICollection)parse.Invoke(null,
            new object[] { realistic.ToString(), "1.2.0" });

        Check("an ordinary release note is untouched", normal.Count >= 60,
              $"{normal.Count} blocks, well inside the limit");
    }

    // ── things that look like bugs and are not ──────────────────────────────────────
    //
    // Both of these were raised in an audit and both are wrong. They are asserted here so the
    // next reading of the same code does not "fix" them back into being bugs.
    static void WhatIsDeliberatelyKept()
    {
        Console.WriteLine();
        Console.WriteLine("Two things that read like defects and must stay as they are:");

        var settings = Source("Models/AppSettings.cs");

        Check("tile ids belonging to no tile are kept, not dropped",
              settings.Contains("deliberately kept rather than dropped"),
              "dropping them loses a newer build's tiles after one older launch");

        // The claim was that a pending position is lost when the overlay is hidden. It cannot
        // be: hiding does not unload, so the window is still there to be asked.
        var app = Source("App.xaml.cs");
        Check("a hidden overlay is still reachable",
              app.Contains("_overlayWindow is { IsLoaded: true }"),
              "Hide does not unload, so the position can still be read and stored");

        var vm = Source("ViewModels/SettingsViewModel.cs");
        Check("and with no overlay at all there is nothing pending to lose",
              vm.Contains("var overlay = LiveOverlay;") && vm.Contains("if (overlay == null) return;"),
              "MoveAxis returns before scheduling a save, so nothing was ever queued");
    }

    // ── a catch that says nothing must have said why ────────────────────────────────
    //
    // Fifty of these existed and nobody could tell, without reading each one, which were
    // deliberate and which were hiding a fault. Several were hiding faults: the sensor host
    // watchdog could stop supervising, the host's diagnostic channel could close, and a reading
    // pump thread could die, all without a single line anywhere.
    //
    // Rather than banning the pattern, which would be wrong — cleanup genuinely has nothing to
    // say — this requires each one to be either an obvious release of a resource or accompanied
    // by a reason. The next empty catch has to justify itself in writing.
    static void EverySilentCatchIsAccountedFor()
    {
        Console.WriteLine();
        Console.WriteLine("Every catch that reports nothing must say why not:");

        var repo = RepoRoot();
        if (repo == null) { Check("the product source was found", false); return; }

        // Releasing something. These have nothing to report by their nature: the thing is being
        // torn down and the failure changes nothing that follows.
        var cleanup = new[]
        {
            "Dispose", "Kill", "Delete", "Close", "Release", "Flush", "Cancel",
            "Stop", "Abort", "Shutdown", "Exit",
        };

        var unexplained = new List<string>();
        int silent = 0, byPattern = 0, byComment = 0;

        foreach (var file in Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repo, file).Replace('\\', '/');
            // The product only. The tests live in the same repository and catch things on
            // purpose, which says nothing about how Pulse itself handles a failure.
            if (rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.Contains("/obj/")
                || rel.Contains("/bin/") || rel.StartsWith("tests/")) continue;

            var lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                int at = line.IndexOf("catch", StringComparison.Ordinal);
                if (at < 0) continue;

                // A one-line "try { ... } catch { }", or a "catch { }" on its own.
                var after = line[at..];
                bool inlineEmpty = System.Text.RegularExpressions.Regex.IsMatch(after, @"^catch[^{]*\{\s*\}");

                // A block form whose body holds nothing but a closing brace.
                bool blockEmpty = false;
                if (!inlineEmpty && System.Text.RegularExpressions.Regex.IsMatch(after, @"^catch[^{]*$")
                    && i + 2 < lines.Length
                    && lines[i + 1].Trim() == "{")
                {
                    blockEmpty = lines[i + 2].Trim() == "}";
                }

                if (!inlineEmpty && !blockEmpty) continue;
                silent++;

                // Releasing a resource, on the same line, is its own explanation.
                bool isCleanup = false;
                foreach (var verb in cleanup)
                    if (line.Contains(verb + "(", StringComparison.Ordinal)) isCleanup = true;

                if (isCleanup) { byPattern++; continue; }

                // Otherwise a reason has to be written next to it: trailing on the line, on the
                // line above, or in the two lines below for a block form.
                bool explained = line.Contains("//", StringComparison.Ordinal)
                    || (i > 0 && lines[i - 1].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    || (i + 2 < lines.Length && lines[i + 2].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    || (i + 3 < lines.Length && lines[i + 3].TrimStart().StartsWith("//", StringComparison.Ordinal));

                if (explained) byComment++;
                else unexplained.Add($"{rel}:{i + 1}");
            }
        }

        Check("there are still silent catches, and that is expected", silent > 0,
              $"{silent} found: {byPattern} releasing a resource, {byComment} with a stated reason");

        Check("none of them is unexplained", unexplained.Count == 0,
              unexplained.Count == 0
                  ? "each one is either cleanup or says why it stays quiet"
                  : string.Join(", ", unexplained));

        // The specific ones that were hiding real faults. Named, because "it has a comment now"
        // is not the same as "it reports the failure".
        var hardware = Source("Services/HardwareService.cs");

        Check("the sensor host watchdog reports its own failures",
              hardware.Contains("LogOnce(\"the sensor host watchdog\""),
              "if it throws, nothing is supervising and the readings never come back");

        Check("so does the host's diagnostic channel",
              hardware.Contains("LogOnce(\"the sensor host's diagnostic reader\""),
              "losing it made every later host problem unexplainable");

        Check("and a reading thread that ends early",
              hardware.Contains("the {name} thread"),
              "the symptom was sensors stopping with nothing saying why");

        Check("a fault in a subscriber is no longer swallowed",
              hardware.Contains("LogOnce(\"a sensor update handler\""),
              "handlers run inline on the interface thread, so their faults surfaced here");

        // These fire on a timer, so they must not be able to fill the log.
        Check("repeating failures are reported once per kind, not once per tick",
              hardware.Contains("if (!Reported.Add(")
              && hardware.Contains("reported once per kind of fault"),
              "the watchdog runs every two seconds; the pump runs per reading");

        Check("but a different fault in the same place is still reported",
              hardware.Contains("$\"{what}|{ex.GetType().FullName}\""),
              "keyed by both, so suppression cannot hide a second problem");

        Check("the diagnostics export names a file it could not read",
              Source("Services/LogService.cs").Contains("could not be read:"),
              "a silently missing section reads as nothing to report");

        Check("and log rotation stays silent on purpose",
              Source("Services/LogService.cs").Contains("would call Write, which would call this again"),
              "reporting it from inside Write recurses until the stack ends");
    }

    static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // The repository root is the folder holding Pulse.csproj, whatever it is called.
            if (File.Exists(Path.Combine(dir.FullName, "Pulse.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// Whether a method's body contains a catch. Read from the source because these are private
    /// event handlers with no other way in.
    static bool Guarded(string source, string method)
    {
        // The declaration, not the first mention. A method that is also called earlier in the
        // file used to be judged by the braces after the call, which belong to something else.
        int at = source.IndexOf("void " + method + "(", StringComparison.Ordinal);
        if (at < 0) at = source.IndexOf(method + "(", StringComparison.Ordinal);
        if (at < 0) return false;

        int open = source.IndexOf('{', at);
        if (open < 0) return false;

        // Balance braces to find the end of the method, so a catch belonging to the next one
        // cannot be mistaken for this one's.
        int depth = 0, i = open;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) break;
        }

        return source.Substring(open, Math.Min(i, source.Length - 1) - open).Contains("catch");
    }
}
