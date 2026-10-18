# Guards against re-breaking things that have already been fixed once.
#
# Every check here exists because the corresponding bug actually shipped. They are assertions
# about the source rather than about behaviour, and that is deliberate: this whole category of
# fault is a silent one. Nothing throws, nothing logs, the installer still reports success and
# the app still starts. The only way these get caught is by someone noticing months later, or
# by a check like this failing the moment the line changes.
#
# Run with no arguments to check the working tree:
#     python guards.py
#
# Every guard also names the commit whose change it would have caught, and those are verified
# by teeth.py, which runs the guards against the historical files and requires them to fail.

import re
import sys
from pathlib import Path

# tests/checks/guards.py, so the repository is two folders up.
REPO = Path(__file__).resolve().parents[2]

results = []


def check(name, ok, detail=""):
    results.append((name, ok))
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  -> {detail}" if detail else ""))


class Sources:
    """Reads the files under test, so the same guards can run against historical copies."""

    def __init__(self, root):
        self.root = Path(root)

    def text(self, name):
        p = self.root / name
        if not p.exists():
            # Historical checkouts may predate a file. Absent counts as "cannot satisfy".
            return None
        return p.read_text(encoding="utf-8-sig", errors="replace")


# ── the installer ───────────────────────────────────────────────────────────────────

def code_section(iss):
    """Just the Pascal, so [Setup] directives are not mistaken for code."""
    i = iss.find("[Code]")
    return iss[i:] if i >= 0 else ""


def strip_comments(pascal):
    """Removes Pascal comments while leaving string literals alone.

    Worth doing properly rather than with a regex. The comments in setup.iss quote the exact
    text these guards look for, because they explain the bugs the guards exist to prevent, so
    a naive scan reports the explanation as the offence.
    """
    out = []
    i, n = 0, len(pascal)
    in_string = False

    while i < n:
        c = pascal[i]

        if in_string:
            out.append(c)
            if c == "'":
                in_string = False
            i += 1
        elif c == "'":
            in_string = True
            out.append(c)
            i += 1
        elif c == "{":
            depth = 1
            i += 1
            while i < n and depth:
                if pascal[i] == "{":
                    depth += 1
                elif pascal[i] == "}":
                    depth -= 1
                i += 1
            out.append(" ")
        elif c == "/" and i + 1 < n and pascal[i + 1] == "/":
            while i < n and pascal[i] != "\n":
                i += 1
        else:
            out.append(c)
            i += 1

    return "".join(out)


def quoted_strings(pascal):
    """Every single quoted literal, comments already removed."""
    return re.findall(r"'([^']*)'", pascal)


def installer_guards(src):
    print("\nThe installer must be able to close Pulse and remove itself:")

    iss = src.text("setup.iss")
    if iss is None:
        check("setup.iss is present", False, "missing")
        return

    # ── the one that just cost us four releases ─────────────────────────────────────
    #
    # Inno's Exec calls CreateProcess with no shell, and ExpandConstant is applied to the
    # program path only, never the parameters. A literal %USERNAME% therefore reaches
    # taskkill unexpanded and matches no process at all, so the kill silently does nothing.
    # Broken by e8b4781, shipped in 1.1.0 through 1.2.0, fixed in f03ed80.
    pascal = strip_comments(code_section(iss))
    offenders = [s for s in quoted_strings(pascal) if re.search(r"%[A-Za-z_][A-Za-z_0-9]*%", s)]
    check("no environment variables in Exec parameters, which never expand there",
          not offenders,
          "found: " + " | ".join(offenders) if offenders else "")

    # The kill is worthless if nothing calls it.
    uninstall_step = pascal[pascal.find("CurUninstallStepChanged"):] if "CurUninstallStepChanged" in pascal else ""
    check("uninstalling still closes a running Pulse",
          "CloseRunningPulse" in uninstall_step,
          "CurUninstallStepChanged must call CloseRunningPulse")

    # Changing this orphans every existing installation: the upgrade path stops recognising
    # what is already there and installs a second copy alongside it.
    check("the application id is unchanged",
          "{{B8F3E2A4-9D1C-4F7E-A5B2-3C8D6E9F1A2B}" in iss,
          "AppId must never change for an existing product")

    # Reinstalling into a different folder left a working exe orphaned on disk, which is how
    # a user ended up with what looked like three copies of Pulse.
    check("the folder page stays disabled and the previous folder is reused",
          "DisableDirPage=yes" in iss and "UsePreviousAppDir=yes" in iss)

    # Restart Manager inspects every file it replaces by default, could not get a clean answer
    # about the bundled PresentMon, and stopped the install with a misleading message.
    check("Restart Manager is limited to Pulse itself",
          "CloseApplicationsFilter=Pulse.exe" in iss)

    # Checked before the wizard starts, and can only refuse to continue, so it replaced the
    # page that closes Pulse for the user with a dead end telling them to do it themselves.
    check("no AppMutex, which turns a handled case into a dead end",
          not re.search(r"^\s*AppMutex\s*=", iss, re.MULTILINE))

    # schtasks /XML emits UTF-16 that does not survive LoadStringFromFile. The list format
    # comes back in the console encoding and can actually be read. Same class of fault as
    # d054570, which made Pulse think the startup task did not exist.
    if "schtasks" in pascal:
        check("the task query uses a format the installer can actually read",
              "/FO LIST" in pascal and "/XML" not in pascal,
              "/V /FO LIST, never /XML")


    # x64os, not x64compatible. They differ on exactly one machine: ARM64 Windows, which counts
    # as x64-compatible because it emulates x64 programs. Pulse would indeed run there and the
    # PawnIO kernel driver would not, because a kernel emulates nothing. Setup would finish, the
    # driver would fail, and every tile would read "--" with no explanation.
    check("the installer refuses machines the driver cannot work on",
          "ArchitecturesAllowed=x64os" in iss,
          "x64compatible lets it install on ARM64, where the kernel driver cannot load")

    # The Run section reports only a failure to start a program and discards the exit code, so a
    # driver installer that ran and refused looked exactly like one that worked.
    # And the driver is judged on whether it is registered afterwards, not on what its
    # installer returned. PawnIO's setup reports success in its own words and still exits
    # non-zero, so reading the code told a machine with a working driver that it had failed.
    # Inventing a failure is worse than missing one: the reason for looking was to stop setup
    # being quiet about a real problem.
    check("a driver that is actually there is never reported as failed",
          "function DriverIsInstalled(): Boolean;" in pascal
          and "DriverFailed := not DriverIsInstalled();" in pascal,
          "the outcome, not the installer's opinion of the outcome")

    check("the driver and the startup task are run where their exit codes can be seen",
          "function InstallStep(" in pascal
          and "procedure CurStepChanged(" in pascal
          and "DriverFailed" in pascal,
          "a silent driver failure is the worst thing this installer can be quiet about")

    check("and a driver that wants a restart says so",
          "function NeedRestart(): Boolean;" in pascal and "RebootWanted" in pascal,
          "3010 means installed, but not loaded until the machine restarts")

    # Searching the listing for the folder name says yes to more than it means: an install at
    # ...\Pulse matches a task running from ...\Pulse-old, so uninstalling one copy could
    # delete the startup task belonging to another.
    check("the uninstaller matches the whole program, not the folder name",
          "{app}\\Pulse.exe" in pascal,
          "a folder name appears inside other folder names")


# ── the build ───────────────────────────────────────────────────────────────────────

def project_guards(src):
    print("\nThe build must keep shipping what we think it ships:")

    csproj = src.text("Pulse.csproj")
    if csproj is None:
        check("Pulse.csproj is present", False, "missing")
        return

    # Pulse ships self contained, so the bundled runtime is the runtime users get and Windows
    # Update will never patch it. Left to the SDK this silently went two security patches
    # behind. Pinned by 19d9c0c.
    pin = re.search(r"<RuntimeFrameworkVersion>([^<]+)</RuntimeFrameworkVersion>", csproj)
    check("the .NET runtime version is pinned, not left to the SDK",
          pin is not None, pin.group(1) if pin else "absent")

    # Without this the sensor host, which is a second copy of the same exe with no UI, builds
    # the Application and loads every merged resource dictionary before reaching our code.
    check("our own entry point is still selected",
          "<StartupObject>Pulse.Program</StartupObject>" in csproj)

    # Not bundled, so never unpacked to %TEMP%, where four of the five kept disappearing and the
    # overlay or shortcuts failed to start eight times in four weeks to 16 Oct 2026. Which means
    # the installer has to carry them, or the single file build starts with none at all.
    iss_files = src.text("setup.iss") or ""
    check("native libraries ship beside Pulse.exe, installed by setup, not unpacked to %TEMP%",
          "<IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>" in csproj
          and 'Source: "publish\\*.dll"; DestDir: "{app}"' in iss_files,
          "the overlay and shortcuts failing at start because %TEMP% had been cleaned")

    # A mismatch ships an installer whose declared version disagrees with the binary inside it,
    # which breaks upgrade detection and makes any bug report ambiguous.
    iss = src.text("setup.iss")
    if iss:
        proj = re.search(r"<Version>([^<]+)</Version>", csproj)
        setup = re.search(r"^AppVersion=(.+)$", iss, re.MULTILINE)
        if proj and setup:
            check("the project and installer agree on the version",
                  proj.group(1).strip() == setup.group(1).strip(),
                  f"project {proj.group(1).strip()}, installer {setup.group(1).strip()}")


# ── threading and binding hazards ───────────────────────────────────────────────────

def wiring_guards(src):
    print("\nWiring that is easy to undo by accident:")

    app = src.text("App.xaml.cs")
    if app is not None:
        # FpsService's constructor builds a DispatcherTimer, which belongs to whichever thread
        # creates it. Readings arrive on a pipe thread with no message loop, so if a reading
        # were the first thing to touch that class the foreground game would silently stop
        # being tracked. It has to be created here, on the UI thread, before the readings can.
        fps = app.find("_ = FpsService.Instance;")
        hw = app.find("_ = HardwareService.Instance;")
        check("frame capture is created before the sensor service, on the UI thread",
              fps >= 0 and hw >= 0 and fps < hw,
              "FpsService.Instance must come first")

    hardware = src.text("Services/HardwareService.cs")
    if hardware is not None:
        # Belt and braces for the same hazard, in case the order above is ever changed back.
        check("readings only touch frame capture once it exists",
              "FpsService.IsStarted" in hardware,
              "Publish must guard on IsStarted")

    xaml = src.text("Views/MainWindow.xaml")
    if xaml is not None:
        combo = re.search(r"<ComboBox[^>]*x:Name=\"GpuCombo\".*?/>", xaml, re.DOTALL)
        if combo is None:
            check("the GPU picker is present", False, "GpuCombo not found")
        else:
            body = combo.group(0)
            # A two way binding writes the displayed selection straight back. When the pinned
            # GPU is switched off the dropdown shows whichever adapter is actually being read,
            # and a two way binding would turn that into a permanent pin, so the card the user
            # chose would never be used again once it returned.
            check("the GPU selection is not bound two way",
                  "Mode=OneWay" in body and "Mode=TwoWay" not in body,
                  "SelectedGpuChoice must be OneWay")
            check("a real choice still reaches settings through the handler",
                  "SelectionChanged=" in body)

    startup = src.text("Services/StartupTask.cs")
    if startup is not None:
        # A read only returns once the child closes its pipe, so waiting on one before the
        # process is known to have finished means a hung schtasks blocks forever and the
        # timeout is never evaluated. That is what this did, and it could freeze the UI.
        check("schtasks output is never read synchronously",
              "StandardOutput.ReadToEnd()" not in startup,
              "a blocking read defeats the timeout")

        # schtasks exits non-zero when asked to delete a task that is not there, which was
        # reported as failure. UpdateStartWithWindows then wrote the opposite of what was
        # asked, so turning startup off while the task was already gone flipped it back on
        # and left the panel with no way to switch it off at all.
        check("removing a startup task that is not there counts as removed",
              "if (!before.Exists) return true;" in startup,
              "absence is the state the caller asked for")

        # And a query that failed is not absence. Reporting success from it moved the toggle
        # to off while the task stayed exactly where it was, so Pulse went on starting with
        # Windows while claiming it would not.
        check("a startup task that could not be read is not reported as removed",
              "if (!before.Known)" in startup and "return false;" in startup,
              "a failed query says nothing about whether the task is there")

        wait = startup.find("WaitForExit(")
        result = startup.find("outputTask.Result")
        check("the process is waited on before its output is collected",
              wait >= 0 and result >= 0 and wait < result,
              "otherwise the timeout below the read is unreachable")

    update = src.text("Services/UpdateService.cs")
    if update is not None:
        # Cancelling a read on an HTTP response stream does not reliably raise
        # OperationCanceledException; it can arrive as an IOException or an HttpRequestException
        # wrapping one. Matching on the type reported a cancelled download as a failed one.
        check("a cancelled download is recognised by its token, not its exception type",
              "catch (Exception) when (linked.IsCancellationRequested)" in update,
              "matching on the exception type misreports cancellation as failure")

        # Without a bound, a half open socket left the panel frozen at a percentage forever.
        check("a download that stops receiving data is abandoned",
              "StallTimeout" in update and "stall.CancelAfter(StallTimeout)" in update,
              "the watchdog must be pushed back by progress, not left to expire on time")

    main_window = src.text("Views/MainWindow.xaml.cs")
    if main_window is not None:
        # Gathering diagnostics shells out to schtasks, asks DXGI about the adapters and reads
        # every log file. Inline, that froze the control panel, and it froze the one button
        # someone presses when something is already wrong.
        check("diagnostics are gathered off the UI thread",
              "await Task.Run(LogService.Export)" in main_window,
              "Export must not be called inline from the click handler")

    fps = src.text("Services/FpsService.cs")
    if fps is not None:
        # The session name has to be two things at once, and getting either wrong has already
        # cost a day. Unnamed, it is called "PresentMon", which every other tool built on it
        # also uses, so --stop_existing_session reached CapFrameX, OCAT or Intel's own Graphics
        # Software. Named per process id, it was new every launch and nothing was ever stopped:
        # ETW sessions outlive the process that made them, this one is killed rather than asked
        # to stop, and six abandoned sessions made Windows drop ~136,000 events per capture,
        # which blanked frame capture for the whole machine including NVIDIA's overlay.
        check("frame capture uses a trace session of its own",
              'TraceSessionName = "PulseMonitor"' in fps
              and "--session_name {TraceSessionName}" in fps,
              "the default name is shared with other tools")

        check("and that name is constant, so a leaked session is cleaned up next launch",
              "Pulse_{Environment.ProcessId}" not in fps
              and "const string TraceSessionName" in fps,
              "a new name every launch leaks one session per run, forever")

        # The call, not the definition: a method nobody invokes is not a guard against anything.
        check("a capture that hears nothing eventually says so",
              "ReportSilentCapture();" in fps,
              "PresentMon can run happily and deliver nothing, which logged nothing at all")

        # NaN parses, and every comparison against NaN is false, so a bare "ms <= 0" let it in.
        check("a frame time must be finite before it is believed",
              "!double.IsFinite(ms)" in fps,
              "NaN passed the old guard and poisoned the average")

        check("frame capture never permanently gives up",
              "MaxRestarts" not in fps,
              "it used to stop trying after five interruptions")

    host = src.text("Services/SensorHost.cs")
    if host is not None:
        # Sleeping the whole interval after the work made the real period the interval plus
        # however long a poll took, so 0.5s was delivering a reading every 1.2s.
        check("the poll interval measures the period, not the gap between polls",
              "pollStarted" in host and "remaining" in host,
              "elapsed time must be subtracted from the wait")

    reader = src.text("Services/SensorReader.cs")
    if reader is not None:
        # LibreHardwareMonitor only builds its Intel GPU group when CPU monitoring is on,
        # because the integrated GPU's sensors hang off the CPU package. Dropping the CPU
        # subsystem therefore removes every GPU reading on an Intel-only machine.
        pass

    hardware_sub = src.text("Services/HardwareService.cs")
    if hardware_sub is not None:
        check("asking for GPU readings still pulls in the CPU subsystem",
              "if (needed.HasFlag(SensorSubsystems.Gpu)) needed |= SensorSubsystems.Cpu;" in hardware_sub,
              "Intel iGPU sensors hang off the CPU package")


# ── the wire format ─────────────────────────────────────────────────────────────────

def protocol_guards(src):
    print("\nThe sensor host wire format:")

    proto = src.text("Services/SensorSnapshot.cs")
    if proto is None:
        return

    # Counted against the number of serialiser configurations rather than merely looked for.
    # There are two, one for snapshots and one for commands, and checking only that the text
    # appears somewhere meant the snapshot serialiser could lose the setting entirely while
    # the guard stayed green because the command serialiser still had it. Comparing counts
    # also means a third serialiser added later has to carry them too.
    configs = len(re.findall(r"JsonSerializerSettings", proto))

    # A German or French locale writes decimals with a comma, which JSON does not accept, so
    # readings would arrive corrupted or unparseable on exactly the machines we cannot test on.
    cultures = len(re.findall(r"Culture\s*=\s*CultureInfo\.InvariantCulture", proto))
    check("every serialiser is written in the invariant culture",
          configs > 0 and cultures >= configs,
          f"{cultures} of {configs} configurations")

    escaped = len(re.findall(r"StringEscapeHandling\s*=\s*StringEscapeHandling\.EscapeNonAscii", proto))
    check("every serialiser escapes non-ASCII, so no codepage can corrupt an adapter name",
          configs > 0 and escaped >= configs,
          f"{escaped} of {configs} configurations")


def borrowed_reading_guards(src):
    print("\nReadings that could be invented, and are not:")

    reader = src.text("Services/SensorReader.cs")
    adapters = src.text("Services/DisplayAdapters.cs")
    if reader is None or adapters is None:
        return

    # Graphics built into a processor often have no sensor the library can read. Windows may
    # still hold a temperature for them, reported by the display driver, and that is what Task
    # Manager's GPU page shows. Asking the same source means Pulse agrees with Task Manager on
    # any machine instead of resembling it on the one it was written against.
    check("the graphics temperature is asked of Windows, per adapter",
          "data.GpuTemp ??= DisplayAdapters.Temperature(adapter.Luid);" in reader,
          "the source Task Manager's GPU page is drawn from")

    # The rule that this whole area keeps having to relearn: a missing reading is not a gap to
    # be filled from elsewhere in the machine. Filling it with the processor die was right on
    # one laptop and wrong on the other, where Task Manager shows nothing at all.
    borrowed = re.search(r"data\.GpuTemp\s*\?{0,2}=\s*data\.CpuTemp", reader)
    check("no processor temperature is put on the graphics tile",
          borrowed is None and "_cpuGraphicsTemp" not in reader,
          borrowed.group(0) if borrowed else "where Task Manager shows nothing, Pulse must show nothing")

    check("an adapter that reports no temperature is left without one",
          "if (perf.Temperature == 0) return null;" in adapters,
          "zero is how a driver says it has none")

    check("the temperature scale is taken from what Windows states",
          "bool tenths = max > 0 ? max >= 200 : perf.Temperature >= 200;" in adapters,
          "read as tenths, a driver reporting whole degrees would show 47 as 4.7")

    check("and an implausible temperature is discarded rather than shown",
          "return celsius is > 0f and <= 150f ? celsius : null;" in adapters)

    # Integrated is Windows' own answer, not a guess from the name or the memory size. An APU
    # can carve out two gigabytes of dedicated memory and still be on the CPU die.
    check("integrated is Windows' answer, not a guess from the adapter's name or memory",
          "HybridIntegrated = 1u << 5" in adapters,
          "every heuristic tried here before has had to be removed")

    check("a question Windows will not answer leaves the reading absent",
          adapters.count("return null;") >= 2 and adapters.count("return false;") >= 3,
          "a failure must never turn into a number")

    opened = adapters.count("Open(luid, out uint handle)")
    closed = adapters.count("finally { Close(handle); }")
    check("every display kernel handle opened is handed back",
          "D3DKMTCloseAdapter(ref close)" in adapters and opened > 0 and closed == opened,
          f"{closed} of {opened} opens are paired with a close")


def counted_once_guards(src):
    print("\nThings that must be counted once, not once per way of looking at them:")

    reader = src.text("Services/SensorReader.cs")
    if reader is None:
        return

    # Windows exposes every NDIS filter bound to an adapter as an adapter of its own, all
    # reporting the same bytes over the same wire. On the machine this was found on, one Wi-Fi
    # card appeared nine times and six survived the name filter, so the tiles read six times
    # the real traffic: Task Manager said 45.9 Mbps while Pulse said the equivalent of 270.
    check("network adapters are grouped by physical address",
          "nic.GetPhysicalAddress().ToString()" in reader
          and "chosenByAddress[address] = nic.Name;" in reader,
          "a binding carries the address of the adapter it sits on, so the address is the link")

    check("and the adapter is preferred over the filters bound to it",
          "|| nic.Name.Length < chosen.Length" in reader,
          "a binding is the adapter's name extended, so it is always longer")

    check("an adapter that reports no address is not grouped with others",
          "withoutAddress.Add(nic.Name);" in reader,
          "unknown is not an identity")


def presentation_guards(src):
    print("\nWhat the panel and the overlay show:")

    styles = src.text("Resources/Styles.xaml")
    if styles is not None:
        # WPF tooltips have no maximum width and do not wrap, so a sentence draws as one line
        # as wide as the text. The 1% Low description reached about nineteen hundred pixels and
        # left the window entirely. The style is global on purpose: a tooltip written next year
        # should not be able to reintroduce this.
        check("tooltips wrap instead of running off the screen",
              'TargetType="ToolTip"' in styles
              and 'Property="MaxWidth"' in styles
              and 'TextWrapping="Wrap"' in styles,
              "a description is a sentence, not a one word hint")

    vm = src.text("ViewModels/OverlayViewModel.cs")
    reader = src.text("Services/SensorReader.cs")
    if vm is None or reader is None:
        return

    # Megabits are decimal, a million bits, while the reading is bytes over 1,048,576. Eight is
    # the obvious factor and the wrong one, by about five percent against Task Manager.
    units = src.text("Models/NetworkUnits.cs")
    if units is not None:
        check("megabits are converted with the decimal factor, not eight",
              "1_048_576f * 8f / 1_000_000f" in units,
              "eight would read five percent low against the thing people compare with")

    check("the unit shown and the value shown are decided together",
          "NetworkUnits.Convert(_value.Value, bits: true)" in vm
          and "NetworkUnits.Label(SettingsService.Instance.Settings.NetworkUnitBits)" in vm,
          "a label in one unit over a number in the other is worse than either")

    # Decided in both directions now, so it is guarded rather than left to be rediscovered.
    # Measured together on one run: D3D 3D 51.4, Task Manager 50.8, vendor load 88.4. Both are
    # honest; the vendor one is what Afterburner and the NVIDIA overlay show, and that is what
    # people compare Pulse against in a game.
    check("GPU usage takes the vendor's figure only from a discrete card",
          "if (discrete && _vendorGpuLoad is { } vendor) data.GpuUsage = vendor;" in reader,
          "a Vega 8 reports its vendor load as a flat 100 while the machine is idle")

    # And discrete means one thing. The picker used four tests and this used the hybrid flag
    # alone, so a machine the two disagreed about listed an adapter as a graphics card and
    # then renamed its temperature tile to say it was part of the processor.
    check("one answer decides discrete or integrated, and it is passed to everything",
          "chosenIsDiscrete = IsDiscrete(chosen);" in reader
          # Every assignment, not just one: since 1.3.3 a sleeping card sets it in a branch of
          # its own, and a second classifier there would pass a check for the first.
          and len(re.findall(r"(?<!bool )chosenIsDiscrete = ", reader))
              == reader.count("chosenIsDiscrete = IsDiscrete(chosen);")
          and "ApplyWindowsTemperature(chosen.Name, chosenIsDiscrete, data);" in reader
          and "private void ApplyWindowsTemperature(string adapterName, bool discrete, SensorData data)" in reader,
          "two classifiers disagreeing is how a card gets labelled as part of the processor")

    check("and integrated graphics keep the engine counter",
          "data.GpuUsage   = d3dEngineLoad ?? coreLoad;" in reader,
          "8.02 there against Task Manager's 8%, where the vendor figure said 100")

    view = src.text("Views/MainWindow.xaml.cs")
    if view is not None:
        check("the GPU source card is shown even with one graphics adapter",
              "GpuSourceSection.Visibility = System.Windows.Visibility.Visible;" in view,
              "it names what is being read, which a single GPU machine still needs")

    # Measured on the Ryzen at idle: cores 0.83 to 0.88, fastest core 0.94, Task Manager 1.35.
    # Copying Task Manager's arithmetic would print a figure no core is running at.
    check("the CPU clock is read from the cores, not modelled from a counter",
          "% Processor Performance" not in reader and "IsCoreClock" in reader,
          "Task Manager multiplies a base frequency by a counter; Pulse reads the cores")

    check("the measurement itself does not know about the preference",
          "NetworkUnitBits" not in reader,
          "the reading is stored in one unit and converted only where it is printed")


def leftover_session_guards(src):
    print("\nClearing what earlier versions left behind:")

    cleanup = src.text("Services/TraceSessionCleanup.cs")
    if cleanup is None:
        return

    # The pattern is the dangerous part. Stopping somebody else's trace session is the same
    # harm this exists to undo, so it is anchored at both ends and digits only: it cannot reach
    # PulseMonitor, which is what a running capture is using, and it cannot reach a name that
    # merely starts the same way.
    check("only the old naming is stopped, anchored and digits only",
          '@"^Pulse_[0-9]+$"' in cleanup,
          "an unanchored pattern would stop PulseMonitor and break a live capture")

    check("and the count is capped",
          "MostToStop" in cleanup and "found.Count >= MostToStop" in cleanup,
          "so a misread listing cannot spawn a process per line")

    check("it says nothing when there is nothing to clear",
          "if (leaked.Count == 0) return;" in cleanup,
          "every machine that never ran an affected build reaches this")

    check("it runs once per launch",
          "Interlocked.Exchange(ref _alreadyRun, 1) != 0" in cleanup)

    # Judged on whether a listing came back, not on what logman returned. It exits
    # 0x80071068, ERROR_WMI_GUID_NOT_FOUND, when it meets a session it cannot resolve while
    # enumerating, and prints everything it could read anyway. Reading the code threw a good
    # listing away and left the leftovers in place, on a real machine, with nothing on stderr.
    check("a listing that arrived is used, whatever logman exited with",
          "if (!listing.Ran || listing.Output.Trim().Length == 0)" in cleanup
          and "ERROR_WMI_GUID_NOT_FOUND" in cleanup,
          "a partial listing can only leave a session for next time, never stop the wrong one")

    # And a stop is judged on its exit code, because it produces no output to judge instead.
    check("but a stop is counted only when it actually succeeded",
          "if (stop.Ran && stop.ExitCode == 0) stopped++;" in cleanup,
          "otherwise a stop that failed would be reported as one that worked")

    check("a hung logman is killed rather than waited on",
          "process.Kill(entireProcessTree: true)" in cleanup
          and "WaitForExit(TimeoutMs)" in cleanup,
          "the hazard that once froze the diagnostics export")

    app = src.text("App.xaml.cs")
    if app is not None:
        check("and it is housekeeping, not on the startup path",
              "TraceSessionCleanup.Run();" in app,
              "nothing on screen waits for it")



def recovery_guards(src):
    """What Pulse does when something it depends on is not there.

    Every check here is a path that used to end in "and then nothing ever tried again". They
    are grouped because they share one shape: a failure was handled once, at the moment it
    happened, and no route back existed afterwards.
    """
    print("\nGetting back up after something fails:")

    reader = src.text("Services/SensorReader.cs")
    host   = src.text("Services/SensorHost.cs")
    hw     = src.text("Services/HardwareService.cs")

    if reader is not None and host is not None:
        # Open tries three times across about six seconds and stops. That is the right patience
        # for a driver that is nearly ready and none at all for one that arrives a moment later,
        # and the case it exists for is the reboot straight after installation. Six seconds used
        # to decide the whole session.
        check("sensors that would not open are tried again later",
              "public void Retry()" in reader
              and "reader.Retry();" in host
              and "!reader.IsReady" in host,
              "three attempts at startup used to be the only attempts there were")

        # A stream of incomplete readings that still called themselves ready. Pulse saw a
        # healthy child answering promptly and had no reason to act, while every tile was blank.
        check("readings that fail every time say so rather than looking healthy",
              "MaxConsecutiveReadFailures" in reader
              and "_consecutiveReadFailures >= MaxConsecutiveReadFailures" in reader,
              "one failed poll is ordinary; every poll failing is not")

    if hw is not None:
        # Every other route back from a dead host hangs off the child's Exited event, and a
        # launch that threw before it had a process never raises one.
        check("a host that never started is started again by the watchdog",
              "private void RelaunchIfAbsent()" in hw and "RelaunchIfAbsent();" in hw,
              "ReplaceHost returns immediately when there is nothing to replace")

        check("and not twice at once while a restart is already waiting",
              "_relaunching" in hw,
              "the exit handler clears the host and then waits out its backoff")

        # Those deadlines were written for the default two second interval and applied whatever
        # the interval was. At five seconds the graphics tiles blanked and refilled on almost
        # every cycle; at sixty the host was killed and replaced before it ever reported.
        # Since 1.3.2 the silence deadline also passes through Patience, which stretches it
        # after silent replacements, so it is written as an allowance rather than inline.
        check("the watchdog deadlines follow the polling rate",
              "private TimeSpan Deadline(TimeSpan baseline)" in hw
              and "silent > Deadline(GpuStaleAfter)" in hw
              and ("silent > Deadline(SilentAfter)" in hw
                   or ("Patience(_hostOpened ? Deadline(SilentAfter)" in hw and "silent > allowance" in hw)),
              "a fixed five seconds is already late when readings arrive every five")

        # An empty list used to be discarded either way, so unplugging an external card and
        # rescanning left it listed, selectable, and connected to nothing.
        check("an empty graphics list is believed when the host actually looked",
              "snapshot.GpusKnown && !SameGpus(snapshot.Gpus)" in hw,
              "nobody asked, and there are none, are different answers")


def honesty_guards(src):
    """Places where the interface said one thing and the code did another."""
    print("\nSaying only what is true:")

    vm    = src.text("ViewModels/OverlayViewModel.cs")
    svm   = src.text("ViewModels/SettingsViewModel.cs")
    view  = src.text("Views/MainWindow.xaml")
    ovxml = src.text("Views/OverlayWindow.xaml")
    win   = src.text("Views/MainWindow.xaml.cs")
    app   = src.text("App.xaml.cs")
    snap  = src.text("Services/SensorSnapshot.cs")

    if vm is not None:
        # The normal overlay binds the unit as its own element, so switching between MB/s and
        # Mbps converted the number and left the old unit beside it until the next reading
        # happened to differ. At a steady zero there is no next differing reading.
        check("changing the network unit changes the unit on screen",
              # In the method the unit switch calls, not anywhere: the sleeping card's tiles
              # raise it too, and a count would be satisfied by those.
              "OnPropertyChanged(nameof(DisplayUnit));" in
                  (re.search(r"public void RefreshDisplayFormatting\(\)\s*\{(.*?)\n    \}", vm, re.S) or [None, ""])[1],
              "a converted number under the unit it was converted from")

        # The setting says it leaves just the readings. It faded the panel and left every tile
        # with an opaque card and a hard outline, so what remained was a grid of boxes.
        check("the background setting reaches the tiles, not only the panel behind them",
              "TileFillBrush" in vm and "TileBorderBrush" in vm and "TileBarTrackBrush" in vm,
              "turn it all the way down is a promise the panel makes")

    if ovxml is not None:
        check("and the overlay uses those brushes",
              "DataContext.TileFillBrush" in ovxml and "DataContext.TileBarTrackBrush" in ovxml)

        # Found by looking at it rather than by reading it: at zero the panel and the tile
        # cards were gone and two violet accent lines were still drawn across the readings.
        # Decoration, not a reading, so it goes with the rest.
        check("the accent lines fade with the background too",
              ovxml.count('Opacity="{Binding NormalPanelOpacity}"') >= 3,
              "the panel, and the two dividers drawn over the readings")

    if snap is not None:
        # Every other reading is cleaned. Battery was added to the snapshot and not to the list,
        # so a NaN would have reached the formatter and the bar.
        check("the battery reading is sanitised like every other one",
              "d.BatteryLevel = Clean(d.BatteryLevel);" in snap,
              "NaN formats as text and clamps to itself")

    if svm is not None:
        # A slider write waiting on its debounce belongs to the position being replaced, and
        # committing one sets the position back to Custom.
        check("choosing a preset cancels a position save that has not happened yet",
              "private void CancelPendingPositionSave()" in svm
              and svm.count("CancelPendingPositionSave();") >= 2,
              "the later, explicit action is the one a person can predict")

        # The panel kept a second opinion about where the overlay was and never revisited it.
        check("the panel follows the overlay after it is dragged",
              "_overlayPosition = s.OverlayPosition;" in svm
              and "_selectedMonitorIndex = s.SelectedMonitorIndex;" in svm,
              "corner buttons highlighting a preset that was replaced by Custom")

        # The overlay status line has sent people to About for a long time, and the reason
        # reached the log, the diagnostics export, and no screen at all.
        check("the sensor fault the overlay points at is somewhere to be read",
              "public string? SensorFault" in svm and "public bool HasSensorFault" in svm)

        # Sanitise goes out of its way to keep ids it does not recognise; this used to rebuild
        # both lists from the catalogue and save, throwing them away without anyone touching
        # anything.
        check("tiles from a newer build survive being opened in an older one",
              "var keptActive = settings.ActiveTileIds.Where(id => !known.Contains(id))" in svm,
              "the care taken in AppSettings was undone in the view model")

    reader = src.text("Services/SensorReader.cs")
    if reader is not None and vm is not None:
        # Adding the processor and graphics figures is right for two chips and wrong for one.
        #
        # Where the graphics are part of the processor, the package reading already contains
        # them. Intel says so in its own domain model, and these are the exact registers the
        # sensor library reads for these two tiles: the processor figure is
        # MSR_PKG_ENERGY_STATUS and the graphics figure is MSR_PP1_ENERGY_STATUS, with PP1 a
        # subdomain of PKG. On an AMD APU the graphics figure has been reported to return the
        # whole chip, which would roughly double it rather than merely inflate it.
        check("the processor and graphics figures are added only when they are two chips",
              "? chosenIsDiscrete" in reader
              and "cpuW + gpuW" in reader
              and ": cpuW" in reader,
              "on one chip the package reading already contains the graphics")

        check("and the reason is written down where the sum is made",
              "MSR_PKG_ENERGY_STATUS" in reader and "MSR_PP1_ENERGY_STATUS" in reader,
              "so nobody puts the addition back without knowing which registers these are")

        # 1.3.3 stopped reading a sleeping laptop card, so it has no power figure. Without this
        # the sum was null and CPU+GPU Power read "--" for as long as the laptop was on battery.
        check("a sleeping graphics card counts as off in CPU+GPU Power",
              "data.GpuAsleep ? cpuW" in reader,
              "CPU+GPU Power reading -- on every laptop on battery")

        # Deliberately not renamed the way GPU Die Temp and Shared VRAM are. The name stays
        # true on one chip, since the package reading is the processor and graphics together,
        # and "Package Power" is the precise term that means nothing to most people. What the
        # reader does need is the reason it matches CPU Power there, which the tooltip gives.
        catalogue = src.text("Models/SensorTileDefinition.cs")
        if catalogue is not None:
            check("the combined power tile explains why it can match CPU Power",
                  "they are one chip drawing one amount, so this matches CPU Power" in catalogue,
                  "otherwise two identical readings look like a fault")

    if view is not None:
        check("and About actually shows it",
              "{Binding SensorFault}" in view and "HasSensorFault" in view,
              "see About for details led nowhere")

    if win is not None and app is not None:
        # The close button quit, Alt+F4 closed the window and left Pulse running with no panel,
        # because the overlay is a window too and WPF never reached its last one.
        check("every way of closing the panel means the same thing",
              "public void RequestExit()" in app
              and "app.RequestExit()" in win
              and "Dispatcher.BeginInvoke(new Action(app.RequestExit))" in win,
              "three controls that mean quit doing three different things")



def diagnosis_guards(src):
    """What a log has to contain to be worth asking somebody for."""
    print("\nLogs that can actually be diagnosed from:")

    log = src.text("Services/LogService.cs")
    if log is None:
        return

    # A user reported Pulse sometimes not starting. His log said, nine times across two
    # versions, only that converting some value in the markup had failed: not which value and
    # not why. The inner exception is the entire useful content of a XamlParseException, and
    # it was being discarded to keep the line short. The stack trace stays out, because that
    # can carry paths from the build machine and this file is meant to be pasteable publicly.
    check("an exception is logged with what it was thrown over",
          "inner.GetType().Name" in log and "error.InnerException" in log,
          "XamlParseException on its own names nothing at all")

    check("and the chain is bounded",
          "depth < 4" in log,
          "a deeply wrapped exception should not write a paragraph into the log")

    # The same user's crash, from the other end. Icon="pack://..." in markup is decoded by
    # ImageSourceConverter while the XAML is parsed, and a failure there is a
    # XamlParseException that takes the window, and the window is built during startup.
    # An icon is cosmetic; losing the application over one is not.
    app = src.text("App.xaml.cs")
    for name in ("Views/MainWindow.xaml", "Views/OverlayWindow.xaml"):
        markup = src.text(name)
        if markup is None:
            continue
        check(f"{name.split('/')[-1]} does not decode its icon while parsing",
              'Icon="pack://' not in markup,
              "a failed decode there is fatal and lands during startup")

    if app is not None:
        check("the icon is applied where failing costs an icon rather than the application",
              "public static void ApplyIcon(Window window)" in app
              and "The window icon could not be loaded" in app)

    # The export pasted all six log files in whole, which is up to six megabytes. The one
    # report that arrived that way carried thirteen days of routine lines with nine crashes
    # buried in them, found by scrolling.
    check("the diagnostics export is bounded rather than every log file in full",
          "ExportTailBytes" in log and "private static void AppendRecent(" in log,
          "six megabytes is an awkward thing to ask somebody to send and a worse thing to read")

    check("and what it leaves out is counted instead of dropped",
          "private static void AppendProblemSummary(" in log,
          "nine crashes as a count is the finding; nine crashes in a megabyte is a needle")

    # "stopped after 112s" and "stopped after 2971s" are one event twice, not two curiosities.
    check("repeats are grouped with the numbers that differ every time normalised",
          "DigitRuns.Replace(rest" in log,
          "durations, process ids and exit codes never change what a line means")

    hw = src.text("Services/HardwareService.cs")
    if hw is not None:
        # Unprefixed output on that stream is the runtime's, and it only writes there while
        # the process is dying. Fourteen access violations were recorded at INFO.
        check("a sensor host dying is logged as an error, not as information",
              'default: LogService.Error("SensorHost", text); break;' in hw,
              "a crash that reads like an ordinary line is a crash nobody sees")

    iss = src.text("setup.iss")
    if iss is not None:
        check("uninstalling takes the logs with it",
              r"{userappdata}\Refora\Pulse\logs" in iss,
              "otherwise a reinstall inherits sessions from versions that are gone")

        check("but leaves the settings, which are the user's work",
              "settings.json is deliberately left alone" in iss)


def hardening_guards(src):
    """Things that are only a problem on the day somebody goes looking for one.

    None of these has ever bitten a user. They are the places where Pulse trusts something it
    does not have to trust, or holds on to something it has finished with, and all of them are
    cheaper to close than to argue about.
    """
    print("\nNot trusting what we do not have to:")

    startup = src.text("Services/StartupTask.cs")
    cleanup = src.text("Services/TraceSessionCleanup.cs")
    window  = src.text("Views/MainWindow.xaml.cs")

    # A bare program name is resolved through the search path, and the search path is not ours.
    # Pulse runs elevated, so anything it starts starts elevated with it.
    for label, text in (("schtasks", startup), ("logman", cleanup), ("explorer", window)):
        if text is None:
            continue
        check(f"{label} is launched by full path, not by name",
              f'FileName               = "{label}.exe"' not in text
              and f'FileName        = "{label}.exe"' not in text,
              "an elevated process should not let the search path choose what it runs")

    if startup is not None:
        # This file is handed to an elevated schtasks and decides what runs at every logon.
        check("the task definition is written to a name that cannot already exist",
              "FileMode.CreateNew" in startup and "FileShare.None" in startup,
              "CreateNew refuses to follow a file or link already sitting there")

    update = src.text("Services/UpdateService.cs")
    if update is not None:
        # The asset name comes from the release on GitHub, which makes it remote text, and
        # Path.Combine returns an absolute second part rather than joining it.
        check("the installer's name from the release is reduced to a file name",
              "private static string SafeInstallerName(UpdateInfo info)" in update
              and "Path.GetFileName(info.InstallerName)" in update,
              "a name with a directory in it would have chosen its own download location")

        check("and the download is proved to land inside the folder we locked down",
              "Refusing to download to a path outside the update folder" in update,
              "belt as well as braces, since this is where an installer is about to be run from")

        check("a download folder whose owner could not be set says so",
              "its owner could not be set to Administrators" in update,
              "a security step that quietly did not happen is found only when it was needed")

    print("\nLetting go of things we have finished with:")

    hw       = src.text("Services/HardwareService.cs")
    counters = src.text("Services/GpuMemoryCounters.cs")

    if hw is not None:
        # This runs on every replacement, and a machine whose graphics driver faults will
        # replace the host many times across a session left running for days.
        check("a replaced sensor host is disposed, not merely dropped",
              "try { host.Dispose(); } catch { }" in hw,
              "a process handle and three pipe handles per replacement, never returned")

        check("and so is one that failed on the way up",
              "try { _host?.Dispose(); } catch { }" in hw,
              "Process.Start can succeed and the wiring after it fail")

    if counters is not None:
        # A laptop that powers its graphics down between games drops one of these every time.
        check("a graphics counter that stopped working is disposed before it is forgotten",
              "try { dead.Dispose(); } catch { }" in counters,
              "each one holds a handle, and a card that comes and goes takes one with it")

    print("\nWindows that fit the screen they open on:")

    settings_vm = src.text("ViewModels/SettingsViewModel.cs")
    whatsnew    = src.text("Views/WhatsNewWindow.xaml.cs")

    if settings_vm is not None:
        # The overlay is resized by dragging its own corner and by nothing else. Its position
        # is clamped to the screen and its size is not, so it can take that corner off screen.
        check("the reset puts the overlay's size back as well as its opacity",
              "OverlayViewModel.Instance.OverlayScale = defaults.OverlayScale;" in settings_vm,
              "otherwise an overlay scaled past the screen edge has no control left on screen")

    if whatsnew is not None:
        # 620 units tall is 1240 physical pixels at 200% scaling, and it cannot be resized or
        # dragged by a title bar it does not have.
        check("the update dialog fits the screen it opens on",
              "private void FitToWorkArea()" in whatsnew,
              "its Download and Install button was below the edge of a small display")

    if window is not None:
        # 420 is a sensible floor for a usable panel and not a fact about anybody's display.
        check("and the panel's minimum size gives way to a smaller screen",
              "MinWidth  = Math.Min(MinWidth,  roomWidth);" in window,
              "a cramped panel that scrolls beats one with its buttons off screen")


def stability_132_guards(src):
    """The 1.3.2 fixes that are about wiring rather than behaviour a test can drive."""
    print("\n1.3.2 stability")

    hw   = src.text("Services/HardwareService.cs") or ""
    fps  = src.text("Services/FpsService.cs") or ""
    app  = src.text("App.xaml.cs") or ""
    vm   = src.text("ViewModels/SettingsViewModel.cs") or ""
    hot  = src.text("Services/HotkeyService.cs") or ""
    iss  = src.text("setup.iss") or ""
    code = strip_comments(code_section(iss))

    # Sleep is not silence. Every silence decision in the watchdogs is on the awake clock.
    hw_code = "\n".join(l for l in hw.splitlines() if not l.strip().startswith("//"))
    check("the sensor watchdog never measures silence with a clock that counts sleep",
          "Environment.TickCount64" not in hw_code and "AwakeClock.Milliseconds" in hw_code,
          "51 of 52 'host silent' warnings in one log were the machine waking")
    check("so does the frame capture watchdog",
          "AwakeClock.Milliseconds - _lastFrameArrival" in fps and "AwakeClock.Milliseconds - (last == 0" in fps)

    # Modern Standby suspends Pulse while the machine counts as awake, so the awake clock alone
    # cannot see it. Both watchdogs notice their own missed ticks instead.
    check("the sensor watchdog notices when Pulse itself was suspended",
          "_paused.Check()" in hw and "new(TimeSpan.FromSeconds(15))" in hw,
          "a suspended copy had its host replaced for 76s of 'silence' without this")
    check("so does the frame capture watchdog",
          "_paused.Check() > TimeSpan.Zero" in fps and "ForgetAcrossPause()" in fps)
    pd = src.text("Services/PauseDetector.cs") or ""
    check("the pause detector uses a clock that counts sleep, by name",
          "GetTickCount64()" in pd and "Environment.TickCount64" not in pd.split("</remarks>")[-1])

    # The tiles follow the driver while Pulse runs, and a driver that appears reaches the host.
    vm2 = src.text("ViewModels/SettingsViewModel.cs") or ""
    check("the driver is re-read while Pulse runs, not only when the panel opens",
          "_driverWatch.Tick += (_, _) => SensorDriver.Instance.Refresh();" in vm2,
          "removing it mid-use left the overlay unaware")
    check("a driver that becomes available restarts the sensor host, from any source",
          "driver == DriverState.Running && _driverSeen != DriverState.Running" in vm2
          and vm2.count("RestartHost(") == 1)

    # A sleeping laptop card is asked about before it is touched, because touching it wakes it.
    # Measured 12 Oct 2026: released 1.3.2 kept an RTX 3050 awake 98% on battery (24.3 W), the
    # fix 4% (15.1 W), Pulse closed 1% (12.4 W).
    reader = src.text("Services/SensorReader.cs") or ""
    # The reading loop, not the report's walk over the same list, which comes earlier in the file.
    i = reader.find("foreach (var hw in _computer.Hardware)", reader.find("public SensorSnapshot Read()"))
    loop = reader[i:i + 3000]
    check("a sleeping graphics card is not read, so Pulse does not keep it awake",
          "IsAsleep(hw)" in loop and "IsGpu(hw.HardwareType)" in loop
          and 0 <= loop.find("IsAsleep(hw)") < loop.find("hw.Accept(_visitor);"),
          "released 1.3.2 doubled a laptop's draw on battery by keeping its card awake")

    # Built-in graphics dip into the sleep state many times a minute while the screen holds a
    # still picture. Measured 16 Oct 2026: an Intel UHD reported asleep 926 times in under four
    # hours. The ones on screen are read anyway; the ones nobody is looking at stay left alone.
    check("built-in graphics on screen are read even while asleep, never shown as asleep",
          "if (chosen != null && asleep.Contains(chosen) && !IsDiscrete(chosen))" in reader
          and "asleep.Remove(chosen);" in reader
          and reader.find("asleep.Remove(chosen);") < reader.find("if (chosen != null && asleep.Contains(chosen))\n"),
          "an Intel UHD driving the screen labelled asleep 926 times in four hours")
    # A card that dips into sleep for a second must not flicker the tiles. Reported 18 Oct 2026
    # with a video: GPU Usage and GPU Temp switching between "asleep" and readings every few
    # seconds on an RTX 3050. Replayed against 1.3.3, 28 of 103 readings said asleep.
    grace = re.search(r"SleepShownAfterMs = ([\d_]+);", reader)
    check("a short dip into sleep keeps the last reading instead of flickering to asleep",
          grace is not None and int(grace.group(1).replace("_", "")) >= 5000
          and "asleepMs < SleepShownAfterMs && _held is { } held" in reader,
          "GPU Usage and GPU Temp flickering between asleep and readings every few seconds")
    i = reader.find("private bool IsAsleep(IHardware gpu)")
    check("sleep changes are logged only for a graphics card",
          "if (IsDiscrete(gpu))" in reader[i:i + 1500],
          "926 log lines of built-in graphics dipping, one morning, burying everything else")

    # Diagnostics: the sensor report. It must not wake what the loop above leaves asleep, must
    # describe a finished reading, and must never hang the Save diagnostics button.
    i = reader.find("public string Describe()")
    describe = reader[i:reader.find("\n    }\n", i)]
    check("the sensor report reads nothing from the hardware",
          i >= 0 and ".Update(" not in describe and "Accept(" not in describe and "Read()" not in describe,
          "a diagnostics click waking a sleeping laptop card")
    host = src.text("Services/SensorHost.cs") or ""
    check("the sensor report is built right after a reading, on the polling thread",
          0 <= host.find("var snapshot = reader.Read();") < host.find("reader.Describe()"),
          "a report of a tree the library was halfway through updating")
    service = src.text("Services/HardwareService.cs") or ""
    check("asking for the sensor report has a time limit",
          "waiter.Task.Wait(timeout)" in service,
          "Save diagnostics stuck on Saving... behind a host that is still opening sensors")
    check("the diagnostics file includes the sensor report",
          "AppendSensors(report);" in (src.text("Services/LogService.cs") or ""),
          "every hardware report needing a second round of questions")
    check("and a sleeping card's tiles say so rather than show a dash",
          '_isAsleep ? "asleep" : "--"' in (src.text("ViewModels/OverlayViewModel.cs") or ""))

    # The check and the kill happen together, or a new host is killed on an old figure.
    i = hw.find("private void CheckHost()")
    body = hw[i:i + 3000]
    check("the watchdog decides and replaces under one lock",
          "lock (_hostLock)" in body and body.find("lock (_hostLock)") < body.find("ReplaceHost("),
          "a host was killed 26 ms after it started")
    check("our own replacements are not counted as crashes",
          "if (!deliberate) _restarts++;" in hw and "if (!deliberate && _restarts >= 4)" in hw,
          "a slow machine was told a graphics driver was faulting")
    check("the host says when it has finished opening",
          "OpenedBanner" in (src.text("Services/SensorHost.cs") or "") and "SensorHost.OpenedBanner" in hw)

    # Update checks keep happening, and a failed one takes nothing back.
    check("update checks are scheduled, not a single call at launch",
          "CheckForUpdatesOnStartup" not in app and "UpdateService.NextCheckIn(" in app)
    check("a network coming back prompts a check", "NetworkAvailabilityChanged += OnNetworkAvailabilityChanged" in app)
    check("static events are released on exit",
          "PowerModeChanged -= OnPowerModeChanged" in app and "NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged" in app)
    i = vm.find("public async Task<bool?> CheckForUpdatesAsync")
    upd = vm[i:i + 2600]
    check("a failed update check does not hide an update already found",
          "else if (success)" in upd and upd.find("IsUpdateAvailable = false") < upd.find("else\n"),
          "one failed retry would otherwise take the banner away")

    # Shortcut diagnostics read from another thread.
    check("shortcut failures are swapped whole, never edited in place",
          "_failures[" not in hot and "_failures.Clear" not in hot and "_failures = failures;" in hot)

    # The overlay retry.
    check("a window that failed to show is released, not just dropped",
          "broken?.Release();" in app)

    # The installer.
    check("the driver check does not trust the service key on its own",
          "Uninstall\\PawnIO" in code and "Services\\PawnIO\\Enum" in code,
          "PawnIO's uninstaller leaves the service key behind until a restart")
    check("the driver's exit code has its own variable", "IntToStr(DriverCode)" in code)
    check("no tile has ever shown fan readings, so setup does not promise them",
          "fan" not in " ".join(quoted_strings(code)).lower())
    check("the uninstaller runs schtasks by its full path",
          '{sys}\\schtasks.exe' in iss and not re.search(r'Filename:\s*"schtasks\.exe"', iss))


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else REPO
    print(f"Regression guards\nsource: {root}")

    src = Sources(root)
    installer_guards(src)
    project_guards(src)
    wiring_guards(src)
    protocol_guards(src)
    borrowed_reading_guards(src)
    counted_once_guards(src)
    leftover_session_guards(src)
    presentation_guards(src)
    recovery_guards(src)
    honesty_guards(src)
    hardening_guards(src)
    diagnosis_guards(src)
    stability_132_guards(src)

    passed = sum(1 for _, ok in results if ok)
    failed = len(results) - passed
    print(f"\n{passed} passed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
