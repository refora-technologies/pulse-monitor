// Tests the frame capture pipeline: what it measures, what it refuses to measure, and how it
// behaves when PresentMon stops.
//
// Every check here exists because of something the code actually did. It killed other tools'
// captures, reported the Windows desktop as a frame rate, accepted NaN as a frame time, gave
// up permanently after five interruptions, and never retried a launch that failed outright.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

        Console.WriteLine("Frame capture\n");

        Interoperability();
        FrameTimes();
        WhatWeMeasure();
        Recovery();
        Naming();
        Windows();
        OldSessionsAreCleanedUp();
        Publishing();
        BothOnePercentLows();
        DisplayedFrames();
        SessionsDoNotAccumulate();

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

    // ── not breaking other people's tools ───────────────────────────────────────────
    static void Interoperability()
    {
        Console.WriteLine("Starting capture must not stop anyone else's:");

        var fps = Source("Services/FpsService.cs");

        Check("our trace session has a name of its own",
              fps.Contains("--session_name {TraceSessionName}")
              && fps.Contains("TraceSessionName = \"PulseMonitor\""),
              "unnamed, it is called PresentMon, which every other tool built on it also uses");

        Check("and only then is an existing session stopped",
              fps.Contains("--stop_existing_session"),
              "safe now that it can only ever match a session of ours");

        // The bug this pairing exists to prevent, and it has been got wrong in both directions.
        //
        // Named per process id, the name was new every launch, so --stop_existing_session never
        // matched anything. ETW sessions outlive the process that created them and this one is
        // killed rather than asked to stop, so every run abandoned a session that stayed
        // subscribed to the graphics providers with nobody reading it. Six accumulated here and
        // Windows began dropping about 136,000 events per capture, which blanked frame capture
        // for the whole machine including NVIDIA's overlay.
        Check("the name does not contain the process id",
              !fps.Contains("Pulse_{Environment.ProcessId}"),
              "a new name every launch means a leaked session is never cleaned up");

        Check("the name is a constant, so the next launch can clean up after this one",
              fps.Contains("const string TraceSessionName"),
              "at most one abandoned session can exist at a time");

        Check("and it is not the default name other tools use",
              !fps.Contains("TraceSessionName = \"PresentMon\""),
              "stopping that would reach CapFrameX, OCAT or Intel Graphics Software");

        Check("why both halves matter is written down",
              fps.Contains("accumulated") && fps.Contains("136,000"),
              "so neither half is undone without knowing what it cost");

        // PresentMon's own help is the authority on what the flag does.
        var presentMon = FindPresentMon();
        if (presentMon == null)
        {
            Console.WriteLine("    (skipped: bundled PresentMon not found, so its help cannot be read)");
            return;
        }

        var help = Run(presentMon, "--help");
        Check("PresentMon supports naming a session", help.Contains("--session_name"),
              "otherwise the isolation above is not real");
        Check("and stopping one only affects the same name",
              help.Contains("same name"),
              "which is why an unnamed session hit other tools");
    }

    static string? FindPresentMon()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName,
                "Resources", "PresentMon", "PresentMon-2.5.1-x64.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Everything Run has failed at, in order. Kept rather than discarded.
    /// </summary>
    /// <remarks>
    /// Run used to answer an empty string for every kind of failure, which is the same answer
    /// it gives for a command that succeeded and printed nothing. A section that counts lines
    /// then reports zero, and zero is read as a fact about the machine when it was really a
    /// fact about the command never having run. That cost a whole elevated run to work out,
    /// and the product had been right the entire time.
    /// </remarks>
    static readonly List<string> _runFaults = new();

    static string Run(string exe, string args)
    {
        var what = $"{Path.GetFileName(exe)} {args}";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(15_000))
            {
                _runFaults.Add($"{what}: still running after 15s");
                try { p.Kill(entireProcessTree: true); } catch { }
            }

            var text = outTask.Result + errTask.Result;

            if (p.HasExited && p.ExitCode != 0)
                _runFaults.Add($"{what}: exit code {p.ExitCode}: {Trimmed(text)}");

            return text;
        }
        catch (Exception ex)
        {
            _runFaults.Add($"{what}: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    static string Trimmed(string text)
    {
        text = text.Trim().Replace("\r", "").Replace("\n", " ");
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    // ── what counts as a frame time ─────────────────────────────────────────────────
    static void FrameTimes()
    {
        Console.WriteLine();
        Console.WriteLine("A frame time must be a real number before it is believed:");

        // The exact guard the parser uses, applied to the values that used to get through.
        static bool Accepted(string field)
        {
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                return false;
            return double.IsFinite(ms) && ms > 0 && ms <= 10_000;
        }

        Check("a normal frame time is accepted", Accepted("16.667"));
        Check("a very slow frame is still accepted", Accepted("250"), "a real stutter");

        // These all parsed successfully and passed a "ms <= 0" test, because every comparison
        // against NaN is false.
        foreach (var bad in new[] { "NaN", "Infinity", "-Infinity", "1e400", "0", "-5", "", "NA" })
            Check($"\"{bad}\" is rejected", !Accepted(bad));

        Check("an absurd frame time is rejected", !Accepted("60000"),
              "ten seconds is already beyond anything a game produces");

        var fps = Source("Services/FpsService.cs");
        Check("the parser really applies that guard",
              fps.Contains("!double.IsFinite(ms) || ms <= 0 || ms > 10_000"),
              "checked in the source, since the parser is private");
    }

    // ── which process ───────────────────────────────────────────────────────────────
    static void WhatWeMeasure()
    {
        Console.WriteLine();
        Console.WriteLine("Only a real application counts as something to measure:");

        var fps = Source("Services/FpsService.cs");

        Check("Pulse never measures itself",
              fps.Contains("pid == (uint)Environment.ProcessId"),
              "the overlay presents, so we were measuring ourselves");

        foreach (var shell in new[] { "explorer", "SearchHost", "StartMenuExperienceHost", "dwm" })
            Check($"{shell} is ignored", fps.Contains($"\"{shell}\""),
                  "this is why the desktop read 4 to 14 fps");

        Check("nothing focused clears the readings rather than freezing them",
              fps.Contains("if (!IsWorthMeasuring(pid))"),
              "otherwise the last game's numbers stay on screen");

        Check("no frame rate threshold is used to guess at the desktop",
              !fps.Contains("< 15") && !fps.Contains("<= 15"),
              "that would hide genuinely struggling games");
    }

    // ── when PresentMon stops ───────────────────────────────────────────────────────
    static void Recovery()
    {
        Console.WriteLine();
        Console.WriteLine("Capture must keep coming back:");

        var fps = Source("Services/FpsService.cs");

        Check("it no longer gives up after a fixed number of attempts",
              !fps.Contains("MaxRestarts"),
              "five unrelated interruptions used to disable FPS for the session");

        Check("the wait between attempts is capped",
              fps.Contains("Math.Min(attempt, 15)"),
              "so it backs off without ever stopping");

        Check("a capture that ran a while clears the count",
              fps.Contains("if (lived >= CaptureSettled) _restartCount = 0;"),
              "otherwise the backoff only grows");

        Check("a launch that fails is retried too",
              fps.Contains("ScheduleRetry();"),
              "Exited never fires for a process that never started");

        Check("only the capture that exited triggers its own restart",
              fps.Contains("ReferenceEquals(exited, _process)"),
              "the delayed restart used to tear down a newer capture");

        Check("a newer capture supersedes a pending restart",
              fps.Contains("generation != _captureGeneration"),
              "which is what stopped two running at once");

        Check("a capture that starts says so",
              fps.Contains("Frame capture started (pid"),
              "a successful start logged nothing, so a dead capture and a healthy one looked alike");

        Check("and one that hears nothing at all eventually says that too",
              fps.Contains("ReportSilentCapture") && fps.Contains("produced no data in"),
              "PresentMon can run happily and deliver not one row, which is what a starved session looks like");

        Check("the silence warning names the trace session and how to list them",
              fps.Contains("logman query -ets"),
              "the person reading the log needs to be able to find the abandoned sessions");

        Check("it is reported once, not on every tick",
              fps.Contains("_reportedSilence"),
              "an idle desktop is legitimately silent and must not fill the log");

        Check("rows are counted before the foreground filter",
              fps.Contains("Interlocked.Increment(ref _rowsSeen)"),
              "otherwise this would measure whether a game is presenting, not whether capture works");

        Check("PresentMon's own errors reach the log",
              fps.Contains("BeginErrorReadLine") && fps.Contains("PresentMon: "),
              "stderr was redirected nowhere, so refusals were invisible");
    }

    // ── saying which statistic this is ──────────────────────────────────────────────
    static void Naming()
    {
        Console.WriteLine();
        Console.WriteLine("The 1% low must say which 1% low it is:");

        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        object? low = null, fps = null;
        foreach (var d in all)
        {
            var id = (string)defs.GetProperty("Id")!.GetValue(d)!;
            if (id == "fps_1low") low = d;
            if (id == "fps") fps = d;
        }

        Check("the 1% low tile exists", low != null);
        if (low == null) return;

        var label = (string)defs.GetProperty("Label")!.GetValue(low)!;
        var desc  = defs.GetProperty("Description")?.GetValue(low) as string;

        Check("its name is not qualified with a convention", label == "1% Low",
              label + ", because there is only one of them now");
        Check("it explains itself", !string.IsNullOrWhiteSpace(desc),
              desc is { Length: > 0 } ? desc[..Math.Min(60, desc.Length)] + "..." : "no description");
        Check("and says over what window",
              desc != null && desc.Contains("2,000 frames"),
              "the window is what makes this number mean anything");

        Check("the plain FPS tile explains itself too",
              fps != null && defs.GetProperty("Description")?.GetValue(fps) is string { Length: > 0 });

        // The stored id must not change, or everyone's tile selection resets.
        Check("the tile's saved identifier is unchanged", label != "" && low != null,
              "fps_1low, so no settings migration is needed");
    }

    // ── the windows the numbers are measured over ───────────────────────────────────
    static void Windows()
    {
        Console.WriteLine();
        Console.WriteLine("The window must be the window it claims to be:");

        var fps = Source("Services/FpsService.cs");

        Check("frames are stamped with when they happened, not when we read them",
              fps.Contains("TimeInSeconds"),
              "output arrives in bursts, which stretched the window either way");

        Check("and arrival time is kept only for noticing frames have stopped",
              fps.Contains("_lastFrameArrival"),
              "the two clocks are not comparable");

        // The window is a frame count with a time cap, and both halves matter. Counting
        // frames is what lets a fast machine forget a stutter quickly; the cap is what stops a
        // slow one remembering it for longer than the sixty seconds people complained about.
        Check("the 1% low window is a number of frames",
              fps.Contains("LowMaxSamples = 2_000"),
              "so it scales with frame rate instead of holding a stutter for a fixed minute");

        Check("and is capped in time as well",
              fps.Contains("LowWindowMs   = 30_000"),
              "2,000 frames is 67 seconds at 30fps, which is worse than what it replaced");

        Check("both limits are applied when a frame arrives",
              fps.Contains("while (_lowSamples.Count > LowMaxSamples ||")
              && fps.Contains("sample.At - _lowSamples.Peek().At > LowWindowMs"),
              "whichever runs out first ends the window");

        // What the window actually is, at frame rates real machines produce. Every one of
        // these must be faster than the sixty seconds it replaced, and none slower.
        foreach (var (rate, expected) in new[] { (240, 8.3), (143, 14.0), (100, 20.0), (60, 30.0), (30, 30.0) })
        {
            double seconds = Math.Min(30.0, 2000.0 / rate);
            Check($"at {rate}fps the window is {seconds:F1}s",
                  Math.Abs(seconds - expected) < 0.1 && seconds <= 30.0 && seconds < 60.0,
                  $"was 60s for every frame rate");
        }

        // The rank the number is taken at. A smaller buffer would put it on too few frames.
        int boundary = 2000 - (Math.Clamp((int)Math.Ceiling(0.99 * 2000) - 1, 0, 1999));
        Check("a full buffer sets the number from the 21st worst frame",
              boundary == 21,
              $"{boundary}th worst; at 1,000 frames it would be the 11th and one hitch would swing it");

        // The tile says 2,000 frames, so the buffer had better be 2,000 frames.
        var tileText = Tile("fps_1low").desc ?? "";
        Check("and the number the tile quotes is the number the code uses",
              tileText.Contains("2,000 frames") && fps.Contains("LowMaxSamples = 2_000"),
              "a tooltip that is nearly true is worse than one that says nothing");

        Check("the 1% low waits for time as well as frames",
              fps.Contains("LowMinSpanMs") && fps.Contains("span >= LowMinSpanMs"),
              "200 frames is 7 seconds at 30fps and under 1 at 240");

        // The span test on its own had a ceiling nobody could see. Two thousand frames spanning
        // three seconds is about 667 frames a second, and above that the retained history is
        // never three seconds long, so the tile read "--" for as long as the game ran however
        // steady it was. A full buffer is the evidence the span was standing in for.
        Check("but a full buffer counts as enough on its own",
              fps.Contains("_lowSamples.Count >= LowMaxSamples"),
              "above about 667 fps the retained window can never be three seconds long");

        Check("the boundary is taken at exactly the 99th percentile",
              fps.Contains("(int)Math.Ceiling(0.99 * count) - 1"),
              "nearest rank, so the index does not drift with the window size");

        // The index, worked through at sizes where an off-by-one would show.
        foreach (var count in new[] { 200, 250, 299, 999, 1000, 6676 })
        {
            int idx = Math.Clamp((int)Math.Ceiling(0.99 * count) - 1, 0, count - 1);
            double proportion = (count - idx) * 100.0 / count;
            Check($"{count} frames puts the boundary at index {idx}",
                  idx >= 0 && idx < count && proportion >= 1.0 && proportion < 1.6,
                  $"{proportion:F2}% of frames at or beyond it");
        }

        Check("the sort no longer allocates every time",
              fps.Contains("ArrayPool<double>.Shared.Rent"),
              "60 seconds of frames lands on the large object heap");

        Check("and what it rents is returned",
              fps.Contains("ArrayPool<double>.Shared.Return"),
              "in a finally, so a fault does not drain the pool");
    }

    // ── how often the numbers reach the screen ──────────────────────────────────────
    static void Publishing()
    {
        Console.WriteLine();
        Console.WriteLine("Frame rate must not be paced by the sensor polling rate:");

        var fps = Source("Services/FpsService.cs");
        var overlay = Source("ViewModels/OverlayViewModel.cs");

        var updated = _asm.GetType("Pulse.Services.FpsService")!.GetEvent("Updated");
        Check("frame capture announces its own updates", updated != null,
              "otherwise nothing can listen without the sensors");

        Check("on a timer of its own", fps.Contains("FromMilliseconds(250)"),
              "five second polling used to mean five second frame rates");

        Check("the overlay listens to it", overlay.Contains("FpsService.Instance.Updated"),
              "and updates only the frame rate tiles");

        Check("a sensor reading no longer overwrites the frame rate",
              overlay.Contains("tile.Definition.Id is \"fps\" or \"fps_1low\""),
              "that would keep replacing a fresh value with an older one");

        // The per frame call is gone; the timer does it instead.
        Check("nothing is recalculated per frame any more",
              fps.Contains("Nothing is recalculated here any more"),
              "averaging the whole window on every present is quadratic work");

        Check("a chain must lead consistently before it takes over",
              fps.Contains("ChainSwitchMargin") && fps.Contains("ChainSwitchLead"),
              "one extra frame used to wipe the whole 1% low history");
    }

    // ── the 1% low ──────────────────────────────────────────────────────────────────
    //
    // One metric, the percentile, which is what RTSS, MSI Afterburner and CapFrameX compute and
    // what people mean when they compare 1% lows. Pulse briefly shipped a second tile using
    // NVIDIA's average-of-the-worst convention; both were correct and having both was not.
    //
    // The important checks here are not about the formula in the abstract. They run it over a
    // real minute of frames captured from a game, whose answer was verified on the day against
    // two other overlays.

    /// The 99th percentile frame time by nearest rank, exactly as RecomputeOnePercentLow does it.
    static double Boundary(double[] ascending)
    {
        int count = ascending.Length;
        return ascending[Math.Clamp((int)Math.Ceiling(0.99 * count) - 1, 0, count - 1)];
    }

    static double Fps(double ms) => 1000.0 / ms;

    /// Real frame times captured from PUBG, kept next to this test.
    static double[] RealFrames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "real-frames.txt");
            if (File.Exists(candidate))
                return File.ReadAllLines(candidate)
                           .Where(l => l.Length > 0 && !l.StartsWith("#"))
                           .Select(l => double.Parse(l, CultureInfo.InvariantCulture))
                           .ToArray();
            dir = dir.Parent;
        }
        return Array.Empty<double>();
    }

    static void BothOnePercentLows()
    {
        Console.WriteLine();
        Console.WriteLine("The 1% low is the 99th percentile of frame times, and nothing else:");

        var src = Source("Services/FpsService.cs");

        Check("the percentile is taken by nearest rank",
              src.Contains("(int)Math.Ceiling(0.99 * count) - 1"),
              "the convention RTSS and CapFrameX use, so the numbers are comparable");

        Check("frame times are sorted, not frame rates",
              src.Contains("Array.Sort(times, 0, count)") && src.Contains("times[next++] = sample.Ms"),
              "computing a rate per frame and taking the lowest 1% of those is a different sum");

        Check("and converted to a rate exactly once",
              src.Contains("OnePercentLowFps = boundaryMs > 0"),
              "at the end, from the boundary frame time");

        Check("the averaging convention is gone",
              !src.Contains("OnePercentLowP1Fps") && !src.Contains("averageMs"),
              "one metric, so there is nothing to choose between");

        // ── the real capture ────────────────────────────────────────────────────────
        var real = RealFrames();
        Check("the captured minute of real frames is available", real.Length > 6000,
              $"{real.Length} frames");

        if (real.Length > 6000)
        {
            var sorted = (double[])real.Clone();
            Array.Sort(sorted);

            double mean = 1000.0 / real.Average();
            double low  = Fps(Boundary(sorted));

            // Checked on 2026-09-03 against Afterburner (110 fps) and the NVIDIA overlay
            // (107 fps) while Pulse displayed 109. If this drifts, the formula changed.
            Check("the mean frame rate matches what was measured that day",
                  Math.Abs(mean - 111.32) < 0.05, $"{mean:F2} fps, expected 111.32");

            Check("the 1% low matches what was measured that day",
                  Math.Abs(low - 90.32) < 0.05, $"{low:F2} fps, expected 90.32");

            // The reference implementation in Intel's own issue tracker truncates n * 0.99
            // instead of using nearest rank. The two disagree only when the frame count is an
            // exact multiple of a hundred, and then by one sample.
            int n = sorted.Length;
            double theirs = 1000.0 / sorted[Math.Min(n - 1, (int)(n * 0.99))];
            Check("and agrees with the published reference formula",
                  Math.Abs(low - theirs) < 0.01, $"ours {low:F2}, theirs {theirs:F2}");

            Check("the 1% low is below the mean, as a low must be",
                  low < mean, $"{low:F1} < {mean:F1}");
        }

        // ── the formula's behaviour ─────────────────────────────────────────────────
        var steady = Enumerable.Repeat(16.667, 1000).ToArray();
        Check("perfectly steady frames give back the frame rate itself",
              Math.Abs(Fps(Boundary(steady)) - 60.0) < 0.01,
              $"{Fps(Boundary(steady)):F1} fps from a flat 16.667ms");

        // A percentile is deliberately unmoved by a handful of catastrophic frames, which is
        // the property that makes it stable and the reason it is not a minimum.
        var oneStutter = Enumerable.Repeat(16.667, 999).Append(500.0).OrderBy(x => x).ToArray();
        Check("a single 500ms stutter does not move it",
              Math.Abs(Fps(Boundary(oneStutter)) - 60.0) < 0.01,
              "one frame in a thousand is well inside the 1%");

        // Enough of them, and it moves properly.
        var manyStutters = Enumerable.Repeat(16.667, 950).Concat(Enumerable.Repeat(50.0, 50))
                                     .OrderBy(x => x).ToArray();
        Check("fifty bad frames in a thousand do move it",
              Fps(Boundary(manyStutters)) < 25,
              $"{Fps(Boundary(manyStutters)):F1} fps once 5% of frames are slow");

        Check("it never exceeds the fastest frame in the window",
              Fps(Boundary(manyStutters)) <= 1000.0 / manyStutters.Min() + 0.01);

        // ── the tile ────────────────────────────────────────────────────────────────
        var lowTile = Tile("fps_1low");

        Check("there is exactly one 1% low tile",
              lowTile.id != null && Tile("fps_1low_p1").id == null,
              "the second one is gone");

        Check("its saved identifier is unchanged",
              lowTile.id == "fps_1low",
              "so nobody loses a tile they had already turned on");

        Check("it is simply called 1% Low", lowTile.label == "1% Low", lowTile.label);

        Check("it says over what window",
              lowTile.desc != null && lowTile.desc.Contains("2,000 frames"),
              "the window is what makes this number mean anything");

        Check("and what a low reading means",
              lowTile.desc != null && lowTile.desc.Contains("stutter", StringComparison.OrdinalIgnoreCase),
              "the number is useless to someone who does not know what it is warning about");

        Check("the 1% low tile alone keeps the minute of history",
              src.Contains("active.Contains(\"fps_1low\");"),
              "there is only one tile that needs it now");

        Check("and either frame rate tile starts the capture",
              src.Contains("active.Contains(\"fps\") || lowOn"),
              "PresentMon should not run for tiles nobody enabled");
    }

    // -- the frame rate is what the card produced -----------------------------------
    //
    // Pulse briefly offered a second tile for frames that reached the monitor. It was removed:
    // it only differs from the main frame rate when the frame rate exceeds the refresh rate,
    // and on the machine it was written against 6,640 of 6,640 frames were displayed, so the
    // two tiles read 109 and 107. Two tiles with "FPS" in the name, telling nobody anything.
    //
    // What must survive is the reason the main reading uses presents rather than display
    // changes, because preferring display changes gave Pulse an invisible ceiling at the
    // refresh rate, and that was a real bug.
    static void DisplayedFrames()
    {
        Console.WriteLine();
        Console.WriteLine("The frame rate is what the card produced, not what the monitor showed:");

        var fps = Source("Services/FpsService.cs");

        Check("presents are preferred over display changes",
              fps.Contains("_headerFrameTimeIndex = presents >= 0 ? presents : displayChange;"),
              "display changes are capped by the refresh rate");

        Check("and why is written down",
              fps.Contains("invisible ceiling at the refresh"),
              "so nobody swaps them back without knowing what it cost");

        Check("the second frame rate tile is gone",
              Tile("fps_displayed").id == null,
              "it only differed above the refresh rate, and confused everyone below it");

        Check("and nothing is left reading the column for it",
              !fps.Contains("_headerDisplayIndex") && !fps.Contains("DisplayedFps"),
              "dead plumbing for a tile nobody sees");

        Check("the frame sample carries only what is used",
              fps.Contains("private readonly record struct FrameSample(double Ms, long At);"),
              "no display time is kept any more");

        Check("the overlay routes only the two frame rate readings",
              Source("ViewModels/OverlayViewModel.cs")
                  .Contains("tile.Definition.Id is \"fps\" or \"fps_1low\""),
              "a tile with no source silently shows nothing");

        Check("ending the capture clears the live rate",
              Mentions(fps, "StopCapture", "ResetStream();")
              && Clears(fps, "ResetStream", "CurrentFps", "OnePercentLowFps"),
              "nothing is presenting once PresentMon is gone");

        // A restart used to clear three of the four column positions and none of the history.
        // PresentMon numbers its timestamps from the start of its own capture, so a quick
        // restart appended frames timed from near zero behind frames timed from the previous
        // run: the newest sample in the queue was then older than the oldest, and the windows
        // measured in time expired everything or nothing.
        Check("and a restart forgets the frames of the capture that ended",
              Mentions(fps, "ResetStream", "_bySwapChain.Clear()", "_lowSamples.Clear()",
                       "_dominantChain", "_headerTimeIndex", "_lastFrameArrival")
              && Mentions(fps, "OnCaptureExited", "ResetStream();"),
              "timestamps restart from zero, so old and new frames cannot share a queue");

        // Only the exit handler and the retry moved the generation, so turning the tile off and
        // on during a restart backoff left the pending work looking current. It then disposed
        // the capture that had just been started and launched another beside it.
        Check("starting or stopping stands down any restart already waiting",
              Mentions(fps, "StopCapture", "_captureGeneration++")
              && Mentions(fps, "StartCapture", "_captureGeneration++"),
              "two PresentMons, the second stopping the first's trace session");

        Check("and disposing ends capture permanently",
              Mentions(fps, "Dispose", "_disposed      = true;", "_captureWanted = false;"),
              "a retry counting down must not start a capture on the way out");

        Check("changing swap chain clears the 1% low",
              Clears(fps, "SwitchChain", "OnePercentLowFps"),
              "the history behind it belonged to the old chain");

        Check("and leaves the live rate to be recomputed",
              !Clears(fps, "SwitchChain", "CurrentFps"),
              "Recompute fills it from the new chain in the same pass");

        Check("frames stopping clears both readings",
              Clears(fps, "ExpireIfStale", "CurrentFps", "OnePercentLowFps"),
              "a frozen game must not keep a confident frame rate");

        Check("and so does alt-tabbing to something else",
              Clears(fps, "RefreshForegroundTarget", "CurrentFps", "OnePercentLowFps"),
              "everything collected belonged to the app just left");
    }


    /// One tile from the product's catalogue, or nulls when there is no such id.
    static (string? id, string label, string? desc) Tile(string wanted)
    {
        var defs = _asm.GetType("Pulse.Models.SensorTileDefinition")!;
        var all = (System.Collections.IEnumerable)defs.GetField("All",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        foreach (var d in all)
        {
            if ((string)defs.GetProperty("Id")!.GetValue(d)! != wanted) continue;
            return (wanted,
                    (string)defs.GetProperty("Label")!.GetValue(d)!,
                    defs.GetProperty("Description")?.GetValue(d) as string);
        }
        return (null, "", null);
    }

    /// The tiles a fresh install starts with.
    static System.Collections.Generic.List<string> DefaultTileIds()
    {
        var settings = Activator.CreateInstance(_asm.GetType("Pulse.Models.AppSettings")!)!;
        return (System.Collections.Generic.List<string>)
            settings.GetType().GetProperty("ActiveTileIds")!.GetValue(settings)!;
    }

    /// <summary>
    /// Whether a method's body sets each of the named properties to null.
    /// </summary>
    /// <remarks>
    /// Which readings each reset path owns differs on purpose, so they are asserted separately
    /// rather than by one blanket rule. SwitchChain is the interesting one: it does not clear
    /// the live rate, because Recompute refills it from the new chain in the same pass and
    /// blanking it would only make the tile flicker.
    /// </remarks>
    /// Whether a method's body contains each of these, as written. For the assignments that
    /// are not "= null": a Clear(), a call to another method, a flag being set.
    static bool Mentions(string source, string method, params string[] fragments)
    {
        var body = Body(source, method);
        if (body == null) return false;

        foreach (var fragment in fragments)
            if (!body.Contains(fragment, StringComparison.Ordinal)) return false;

        return true;
    }

    static string? Body(string source, string method)
    {
        int start = source.IndexOf($"void {method}", StringComparison.Ordinal);
        if (start < 0) return null;

        int end = source.IndexOf("\n    /// <summary>", start + 10, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(source.Length, start + 2500);
        return source[start..end];
    }

    static bool Clears(string source, string method, params string[] properties)
    {
        int start = source.IndexOf($"void {method}", StringComparison.Ordinal);
        if (start < 0) return false;

        // To the next method, roughly: far enough to cover the body, near enough not to borrow
        // another method's assignments.
        int end = source.IndexOf("\n    /// <summary>", start + 10, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(source.Length, start + 2500);
        var body = source[start..end];

        foreach (var property in properties)
        {
            int at = body.IndexOf(property, StringComparison.Ordinal);
            if (at < 0) return false;

            // The assignment itself, not merely a mention: these are padded for alignment.
            var rest = body[at..];
            int newline = rest.IndexOf('\n');
            if (newline < 0) newline = rest.Length;
            if (!rest[..newline].Contains("= null")) return false;
        }
        return true;
    }

    // -- clearing up after the versions that leaked ----------------------------------
    //
    // 1.2.0 and earlier named the session after the process id and never released it, so a
    // machine collected one per launch. Fifty-one were found here, Windows was dropping about
    // 136,000 events per capture, and frame rate measurement was dead for every tool including
    // NVIDIA's overlay.
    //
    // Current builds cannot leak, but they also cannot see what earlier ones left: the names
    // contain a process id nothing now knows. Without this, upgrading leaves the damage exactly
    // where it was on the machines that suffered it.
    //
    // The matching is the dangerous part. Stopping somebody else's trace session is the same
    // harm this exists to undo, so the pattern is tested against everything nearby that must
    // survive it, not only against what it should catch.
    static void OldSessionsAreCleanedUp()
    {
        Console.WriteLine();
        Console.WriteLine("Sessions leaked by earlier versions are cleared, and nothing else is:");

        var cleanup = _asm.GetType("Pulse.Services.TraceSessionCleanup");
        Check("the cleanup exists", cleanup != null);
        if (cleanup == null) return;

        var leaked = cleanup.GetMethod("Leaked", BindingFlags.NonPublic | BindingFlags.Static)
                  ?? cleanup.GetMethod("Leaked", BindingFlags.Public | BindingFlags.Static);
        Check("and can be asked what it would stop", leaked != null);
        if (leaked == null) return;

        List<string> Match(string listing) =>
            (List<string>)leaked.Invoke(null, new object[] { listing })!;

        // What must be caught.
        foreach (var name in new[] { "Pulse_1", "Pulse_12084", "Pulse_4294967295" })
            Check($"\"{name}\" is stopped", Match(name).Count == 1, "the old naming");

        // What must not be. Each of these is a real hazard rather than a hypothetical: the
        // first is our own current session, and the rest are the shapes a near miss takes.
        foreach (var name in new[]
                 {
                     "PulseMonitor",        // ours, right now
                     "Pulse_",              // the prefix with no id
                     "Pulse_abc",           // not an id
                     "Pulse_12a",           // nearly an id
                     "Pulse_12084x",        // an id with something after it
                     "pulse_12084",         // the wrong case
                     "MyPulse_12084",       // ours as a suffix of someone else's
                     "PulseMonitor_12084",  // a plausible future name of ours
                 })
        {
            Check($"\"{name}\" is left alone", Match(name).Count == 0);
        }

        // A real listing, headings and all. The table is translated on a localised Windows, so
        // nothing may depend on reading the headings.
        const string listing = """
            Data Collector Set                      Type                          Status
            -------------------------------------------------------------------------------
            Circular Kernel Context Logger          Trace                         Running
            DiagLog                                 Trace                         Running
            WPR_initiated_DiagTrackMiniLogger_WPR System Collector Trace          Running
            Pulse_12084                             Trace                         Running
            PulseMonitor                            Trace                         Running
            PresentMon                              Trace                         Running
            Pulse_30952                             Trace                         Running
            Pulse_12084                             Trace                         Running

            The command completed successfully.
            """;

        var fromListing = Match(listing);
        Check("a real listing yields only the leaked ones",
              fromListing.Count == 2 && fromListing.Contains("Pulse_12084") && fromListing.Contains("Pulse_30952"),
              string.Join(", ", fromListing));

        Check("our own session survives it",
              !fromListing.Contains("PulseMonitor"),
              "stopping this one would break the capture that is running");

        Check("and another tool's session survives it",
              !fromListing.Contains("PresentMon"),
              "that one belongs to somebody else");

        Check("a name repeated in the listing is stopped once",
              fromListing.Count(n => n == "Pulse_12084") == 1,
              "a second stop would log a failure for a session already gone");

        Check("an empty listing asks for nothing", Match("").Count == 0);

        // The limit exists so a parsing fault cannot turn into hundreds of processes.
        var many = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"Pulse_{i}    Trace    Running"));
        Check("a listing far past anything real is capped",
              Match(many).Count == 200,
              $"{Match(many).Count} of 500, so a misread cannot spawn a process per line");

        // Silence in the ordinary case: every machine that never ran an affected build gets
        // here, finds nothing, and must not write a line about it at every launch.
        var src = Source("Services/TraceSessionCleanup.cs");
        Check("nothing is logged when there is nothing to clear",
              src.Contains("if (leaked.Count == 0) return;"),
              "which is every machine that never ran an affected build");

        Check("it runs once per launch, not once per poll",
              src.Contains("Interlocked.Exchange(ref _alreadyRun, 1) != 0"));

        Check("a hung logman is killed rather than waited on",
              src.Contains("if (!process.WaitForExit(TimeoutMs))")
              && src.Contains("process.Kill(entireProcessTree: true)"),
              "the same hazard schtasks had, which froze the diagnostics export");

        Check("it is housekeeping, off the startup path",
              Source("App.xaml.cs") is { } app
              && app.Contains("TraceSessionCleanup.Run();")
              && app.IndexOf("TraceSessionCleanup.Run();", StringComparison.Ordinal)
                 > app.IndexOf("Task.Run(() =>", StringComparison.Ordinal),
              "nothing on screen should wait for it");
    }

    // -- abandoned trace sessions ---------------------------------------------------
    //
    // The behaviour, not the source. Kills a capture the way Pulse kills its own, twice, and
    // counts what Windows is left holding. Under the old per-process naming this leaves two
    // sessions and would leave one more for every launch forever; under a constant name it
    // leaves exactly one however many times it is run.
    //
    // Needs administrator rights, because creating an ETW session does. Skips honestly rather
    // than passing vacuously when it cannot.
    static void SessionsDoNotAccumulate()
    {
        Console.WriteLine();
        Console.WriteLine("Killing a capture must not leave trace sessions behind forever:");

        bool elevated;
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        var presentMon = FindPresentMon();

        if (!elevated || presentMon == null)
        {
            Console.WriteLine("    (skipped: creating a trace session needs administrator rights,");
            Console.WriteLine("     so this proves nothing unless the suite is run elevated)");
            return;
        }

        var made  = new List<string>();

        // Everything the run noticed but that no single check owns: a session that never
        // appeared, a logman that said nothing. Printed when a check below fails.
        //
        // This exists because the first version of these checks could only report "0 found",
        // which is the same answer for "PresentMon refused to create it", "it was created and
        // something else stopped it" and "logman returned nothing so we counted nothing in an
        // empty string". Three different conclusions, one indistinguishable number.
        var notes = new List<string>();

        try
        {
            // logman's listing, read again when the read itself failed rather than when the
            // answer is merely unwelcome.
            //
            // Asking twice is sound here because listing sessions changes nothing, so a second
            // attempt can only give a better reading of the same table. It is not a retry until
            // the expected answer appears: a listing that arrives intact and says zero is
            // believed the first time. The distinction is the whole point, because a failed
            // read used to look exactly like an empty table.
            string Listing()
            {
                for (int attempt = 1; ; attempt++)
                {
                    int before = _runFaults.Count;
                    var text = Run("logman.exe", "query -ets");
                    bool faulted = _runFaults.Count > before || text.Trim().Length == 0;

                    if (!faulted) return text;

                    var why = _runFaults.Count > before
                        ? _runFaults[^1]
                        : "logman printed nothing at all";

                    if (attempt == 3)
                    {
                        notes.Add($"could not read the session list after 3 tries: {why}");
                        return text;
                    }

                    notes.Add($"reading the session list failed ({why}), asked again");
                    System.Threading.Thread.Sleep(500);
                }
            }

            int Count(string prefix)
            {
                int n = 0;
                foreach (var line in Listing().Split('\n'))
                    if (line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n++;
                return n;
            }

            // Exactly what Pulse does: start a capture, then kill it. A killed process cleans
            // up nothing, so whatever session it created is left registered.
            void StartThenKill(string sessionName)
            {
                made.Add(sessionName);
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName  = presentMon,
                    Arguments = "--output_stdout --no_console_stats --v1_metrics "
                              + $"--session_name {sessionName} --stop_existing_session",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                });
                if (p == null)
                {
                    notes.Add($"{sessionName}: PresentMon did not start at all");
                    return;
                }

                // Read as it goes. Both pipes are redirected, and a pipe nobody empties stops
                // the writer once it fills, which would be us deciding what PresentMon gets to
                // do rather than watching what it does.
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();

                // Wait for the session to exist rather than assume a fixed delay is long
                // enough for it to have been created. Two and a half seconds usually was, and
                // on a busy machine occasionally was not: the kill then landed before there
                // was anything to leave behind, and the test failed reporting zero abandoned
                // sessions, which says nothing about Pulse and everything about the timing
                // assumption underneath it.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                bool appeared = false;

                while (DateTime.UtcNow < deadline)
                {
                    if (Count(sessionName) > 0) { appeared = true; break; }

                    // If it has already given up there is nothing left to wait for, and its
                    // exit code and complaint are worth more than another nineteen seconds.
                    if (p.HasExited) break;
                    System.Threading.Thread.Sleep(250);
                }

                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(3000); } catch { }

                if (!appeared)
                {
                    var complaint = errTask.Wait(2000) ? errTask.Result.Trim() : "(not readable)";
                    if (complaint.Length == 0)
                        complaint = outTask.Wait(2000) ? outTask.Result.Trim() : "(not readable)";
                    if (complaint.Length > 400) complaint = complaint[..400] + "...";

                    notes.Add($"{sessionName}: no session ever appeared. PresentMon exit code "
                            + $"{(p.HasExited ? p.ExitCode.ToString() : "still running")}, "
                            + $"said: {(complaint.Length == 0 ? "nothing" : complaint)}");
                }

                p.Dispose();
            }

            // The scheme that shipped in this build: one constant name.
            var ours = FpsSessionName;
            StartThenKill(ours);
            int afterOne = Count(ours);
            StartThenKill(ours);
            int afterTwo = Count(ours);

            Check("a killed capture does leave its session behind", afterOne >= 1,
                  $"{afterOne} named {ours}, which is why the name matters");

            Check("but running it again leaves one, not two", afterTwo == afterOne && afterTwo <= 1,
                  $"{afterTwo} after two launches; the second stopped the first");

            // The scheme this replaced, to show the difference is real rather than assumed.
            StartThenKill("PulseLeakProbe_1");
            StartThenKill("PulseLeakProbe_2");
            int leaked = Count("PulseLeakProbe_");

            Check("a name that changes every launch accumulates instead", leaked == 2,
                  $"{leaked} sessions from two launches, one per launch, forever");

            Check("which is the difference the fix makes",
                  leaked > afterTwo,
                  $"{leaked} abandoned under the old naming against {afterTwo} under the new");

            // ── and the cleanup, against those very sessions ─────────────────────────
            //
            // Everything above proves the leak is real. This proves the code that clears it
            // works on real sessions rather than on a string, which is the part that runs on
            // a user's machine after they upgrade.
            Console.WriteLine();
            Console.WriteLine("Clearing sessions an earlier version left, for real:");

            // Two under the old naming, and ours alongside them. Ours must survive.
            StartThenKill("Pulse_999001");
            StartThenKill("Pulse_999002");

            // The listing itself, so a failure here says what the machine actually looked like
            // rather than leaving the next person to guess. This test creates real sessions,
            // and "0 found" on its own cannot distinguish "never created" from "already gone".
            string before = Listing();
            int oursBefore = Count(FpsSessionName);
            int oldBefore  = Count("Pulse_999");

            // What the machine looked like, and anything odd noticed getting here. A failure
            // in this section is about sessions that exist or do not, and neither is visible
            // from a number on its own.
            string Evidence(string listing)
            {
                // The last few. A polling loop can record the same complaint many times over,
                // and a failure message nobody reads to the end explains nothing.
                var recent = notes.Count <= 8 ? notes : notes.GetRange(notes.Count - 8, 8);
                return (recent.Count == 0 ? "" : "\n        " + string.Join("\n        ", recent))
                     + "\n" + PulseLines(listing);
            }

            Check("two old style sessions are there to clear", oldBefore == 2,
                  oldBefore == 2 ? "2 found, under the naming 1.2.0 and earlier used"
                                 : $"{oldBefore} found.{Evidence(before)}");
            Check("and one of ours is running beside them", oursBefore >= 1,
                  oursBefore >= 1 ? $"{oursBefore} named {FpsSessionName}"
                                  : $"none.{Evidence(before)}");

            var cleanupType = _asm.GetType("Pulse.Services.TraceSessionCleanup")!;

            // Run() is deliberately once per process, so the latch is released to run it here.
            cleanupType.GetField("_alreadyRun", BindingFlags.NonPublic | BindingFlags.Static)!
                       .SetValue(null, 0);
            cleanupType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
                       .Invoke(null, null);

            string after = Listing();
            int oldAfter  = Count("Pulse_999");
            int oursAfter = Count(FpsSessionName);

            Check("the leaked sessions are gone", oldAfter == 0,
                  $"{oldBefore} before, {oldAfter} after");

            Check("and ours is untouched", oursAfter == oursBefore,
                  oursAfter == oursBefore
                      ? $"{oursAfter} named {FpsSessionName}, which a capture may be using right now"
                      : $"{oursBefore} before, {oursAfter} after.{Evidence(after)}");
        }
        finally
        {
            // Never leave any behind: that is the entire bug being tested.
            foreach (var name in made) Run("logman.exe", $"stop {name} -ets");
        }
    }

    /// Just the Pulse-ish lines of a logman listing, for a failure message worth reading.
    static string PulseLines(string listing)
    {
        var lines = listing.Split('\n')
                           .Select(l => l.TrimEnd())
                           .Where(l => l.TrimStart().StartsWith("Pulse", StringComparison.OrdinalIgnoreCase)
                                    || l.TrimStart().StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase))
                           .ToList();
        return lines.Count == 0 ? "        (none)" : "        " + string.Join("\n        ", lines);
    }

    /// Read from the product so the test cannot drift from the name actually used.
    static readonly string FpsSessionName = ReadSessionName();

    static string ReadSessionName()
    {
        var src = Source("Services/FpsService.cs");
        const string marker = "TraceSessionName = \"";
        int at = src.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return "PulseMonitor";
        at += marker.Length;
        int end = src.IndexOf('"', at);
        return end > at ? src[at..end] : "PulseMonitor";
    }
}
