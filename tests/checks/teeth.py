# Proves the guards would actually have caught the bugs they claim to.
#
# A guard that passes against the fixed code proves nothing on its own. Each case below checks
# out the real historical version of the file, from the commit where the bug was present, and
# requires the matching guard to fail against it. A guard that passes here is not testing
# anything and should be rewritten.

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

TESTS = Path(__file__).resolve().parent
REPO = TESTS.parents[1]

# Everything the guards read. Whichever of these existed at a given commit gets extracted.
WATCHED = [
    "setup.iss",
    "Pulse.csproj",
    "App.xaml.cs",
    "Services/HardwareService.cs",
    "Services/SensorHost.cs",
    "Services/SensorReader.cs",
    "Services/DisplayAdapters.cs",
    "Resources/Styles.xaml",
    "Models/NetworkUnits.cs",
    "ViewModels/OverlayViewModel.cs",
    "Services/SensorSnapshot.cs",
    "Views/MainWindow.xaml",
    "Views/MainWindow.xaml.cs",
    "Services/StartupTask.cs",
    "Services/FpsService.cs",
    "Services/TraceSessionCleanup.cs",
    "Services/UpdateService.cs",
    "ViewModels/SettingsViewModel.cs",
    "Views/OverlayWindow.xaml",
    "Views/OverlayWindow.xaml.cs",
    "Services/LogService.cs",
]

CASES = [
    ("e8b4781", "no environment variables in Exec parameters",
     "the uninstall kill filter that matched no process, shipped in 1.1.0 to 1.2.0"),

    ("19d9c0c~1", "the .NET runtime version is pinned",
     "shipping whichever runtime patch the SDK happened to pick"),

    ("b2307d5", "our own entry point is still selected",
     "the sensor host loading the whole UI resource tree"),

    ("b2307d5", "frame capture is created before the sensor service",
     "a DispatcherTimer built on a thread with no message loop"),

    ("b2307d5", "readings only touch frame capture once it exists",
     "the same hazard, from the other side"),

    ("650615d", "the GPU selection is not bound two way",
     "a displayed fallback silently becoming a permanent pin"),

    ("650615d", "the poll interval measures the period, not the gap",
     "0.5s delivering a reading every 1.2s"),

]

# Guards protecting something that never shipped broken have no historical counterexample, so
# they are proven the other way round: reintroduce the fault into the current source and
# require the guard to notice. Same standard, different direction.
MUTATIONS = [
    # Show asleep the moment the card dips, and the tiles flicker the way a reporter filmed.
    ("Services/SensorReader.cs",
     "private const long SleepShownAfterMs = 10_000;",
     "private const long SleepShownAfterMs = 0;",
     "a short dip into sleep keeps the last reading",
     "GPU Usage and GPU Temp flickering between asleep and readings every few seconds"),

    # Let built-in graphics on screen be treated as asleep again, and the chip drawing the screen
    # reads "asleep" for a fraction of a second, hundreds of times a day.
    ("Services/SensorReader.cs",
     "            if (chosen != null && asleep.Contains(chosen) && !IsDiscrete(chosen))\n            {\n                chosen.Accept(_visitor);\n                asleep.Remove(chosen);\n            }\n",
     "",
     "built-in graphics on screen are read even while asleep",
     "an Intel UHD driving the screen labelled asleep 926 times in four hours"),

    # Log every adapter's changes again, and built-in graphics fill the log.
    ("Services/SensorReader.cs",
     "            if (IsDiscrete(gpu))\n            {\n",
     "            {\n",
     "sleep changes are logged only for a graphics card",
     "926 log lines of built-in graphics dipping, one morning, burying everything else"),

    # Refresh the hardware while describing it, and a diagnostics click wakes a sleeping card.
    ("Services/SensorReader.cs",
     "        void Walk(IHardware hw, int depth)\n        {\n",
     "        void Walk(IHardware hw, int depth)\n        {\n            hw.Update();\n",
     "the sensor report reads nothing from the hardware",
     "a diagnostics click waking a sleeping laptop card"),

    # Wait without a limit, and Save diagnostics hangs behind a host that is opening sensors.
    ("Services/HardwareService.cs",
     "waiter.Task.Wait(timeout)",
     "waiter.Task.Wait(-1)",
     "asking for the sensor report has a time limit",
     "Save diagnostics stuck on Saving... behind a host that is still opening sensors"),

    # Drop the section, and reports go back to needing a second round of questions.
    ("Services/LogService.cs",
     "            AppendSensors(report);\n",
     "",
     "the diagnostics file includes the sensor report",
     "every hardware report needing a second round of questions"),

    ("Services/SensorSnapshot.cs",
     "StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,",
     "",
     "every serialiser escapes non-ASCII",
     "an adapter name corrupted by a console codepage"),

    ("Services/SensorSnapshot.cs",
     "Culture              = CultureInfo.InvariantCulture,",
     "",
     "every serialiser is written in the invariant culture",
     "decimal commas on a German or French machine"),

    ("setup.iss",
     "AppId={{B8F3E2A4-9D1C-4F7E-A5B2-3C8D6E9F1A2B}",
     "AppId={{00000000-0000-0000-0000-000000000000}",
     "the application id is unchanged",
     "every existing installation orphaned"),

    ("setup.iss",
     "DisableDirPage=yes",
     "DisableDirPage=auto",
     "the folder page stays disabled",
     "a second copy installed into a different folder"),

    ("setup.iss",
     "CloseApplicationsFilter=Pulse.exe",
     "",
     "Restart Manager is limited to Pulse itself",
     "installs stopping on a file Restart Manager could not judge"),

    ("setup.iss",
     "CloseApplications=yes",
     "CloseApplications=yes\nAppMutex=PulseMonitor_SingleInstance",
     "no AppMutex",
     "the wizard replaced by a dead end telling the user to close Pulse"),

    ("setup.iss",
     '/Query /TN "PulseMonitor" /V /FO LIST',
     '/Query /TN "PulseMonitor" /XML',
     "a format the installer can actually read",
     "UTF-16 output that LoadStringFromFile cannot read"),

    ("setup.iss",
     "    CloseRunningPulse();",
     "",
     "uninstalling still closes a running Pulse",
     "locked files and a sensor driver that cannot be removed"),

    # Deliberately does not name a version. This entry used to say AppVersion=1.3.0, which
    # meant the teeth test stopped finding its target the moment anyone bumped the version,
    # and reported it as a failure of the guard rather than of itself. Prefixing the value
    # leaves it disagreeing with the project whatever the real version happens to be.
    ("setup.iss",
     "AppVersion=",
     "AppVersion=9.9.9-",
     "the project and installer agree on the version",
     "an installer whose version disagrees with the binary inside it"),

    ("Views/MainWindow.xaml",
     "SelectionChanged=\"GpuCombo_SelectionChanged\"",
     "",
     "a real choice still reaches settings",
     "a GPU picker that cannot record what the user picked"),

    ("Services/HardwareService.cs",
     "if (needed.HasFlag(SensorSubsystems.Gpu)) needed |= SensorSubsystems.Cpu;",
     "",
     "pulls in the CPU subsystem",
     "every GPU reading lost on an Intel-only machine"),

    # Leave them out of the installer, and the single file build starts with no native libraries.
    ("setup.iss",
     'Source: "publish\\*.dll"; DestDir: "{app}"; Flags: ignoreversion\n',
     "",
     "native libraries ship beside Pulse.exe",
     "a single file build missing its native libraries"),

    # Bundle them again, and they go back to %TEMP%, where something keeps deleting them.
    ("Pulse.csproj",
     "<IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>",
     "<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>",
     "native libraries ship beside Pulse.exe",
     "the overlay and shortcuts failing at start because %TEMP% had been cleaned"),

    ("Services/StartupTask.cs",
     "var outputTask = process.StandardOutput.ReadToEndAsync();",
     "string outputEarly = process.StandardOutput.ReadToEnd(); var outputTask = Task.FromResult(outputEarly);",
     "schtasks output is never read synchronously",
     "a hung Task Scheduler blocking forever with the timeout unreachable"),

    ("Views/MainWindow.xaml.cs",
     "await Task.Run(LogService.Export)",
     "LogService.Export()",
     "diagnostics are gathered off the UI thread",
     "the control panel frozen by the button pressed when something is already wrong"),

    ("Services/UpdateService.cs",
     "catch (Exception) when (linked.IsCancellationRequested)",
     "catch (OperationCanceledException)",
     "recognised by its token, not its exception type",
     "a cancelled download reported to the user as a failed one"),

    ("Services/UpdateService.cs",
     "stall.CancelAfter(StallTimeout);",
     "",
     "a download that stops receiving data is abandoned",
     "a half open socket freezing the panel at a percentage forever"),

    ("Services/StartupTask.cs",
     "if (!before.Exists) return true;",
     "",
     "removing a startup task that is not there counts as removed",
     "the startup switch flipping itself back on with no way to turn it off"),

    # Take away the check that a query actually answered and a failed one reads as absence:
    # the toggle moves to off, the task stays, and Pulse starts with Windows while denying it.
    ("Services/StartupTask.cs",
     "if (!before.Known)",
     "if (false)",
     "a startup task that could not be read is not reported as removed",
     "turning startup off reporting success while changing nothing"),

    # Take the discrete test away and every AMD APU reports a flat 100% GPU usage.
    ("Services/SensorReader.cs",
     "if (discrete && _vendorGpuLoad is { } vendor) data.GpuUsage = vendor;",
     "if (_vendorGpuLoad is { } vendor) data.GpuUsage = vendor;",
     "GPU usage takes the vendor's figure only from a discrete card",
     "a Vega 8 reading 100% while Task Manager reads 8%"),

    # Let the two classifiers drift apart again and a machine they disagree about lists an
    # adapter as a graphics card, then renames its temperature tile to say it is part of the
    # processor.
    ("Services/SensorReader.cs",
     "chosenIsDiscrete = IsDiscrete(chosen);\n                ApplyWindowsTemperature",
     "chosenIsDiscrete = !FindAdapter(chosen.Name)?.Integrated ?? true;\n                ApplyWindowsTemperature",
     "one answer decides discrete or integrated, and it is passed to everything",
     "the picker and the tile labels answering the same question differently"),

    # Put the addition back and every machine without a separate graphics card overstates:
    # the package reading already contains the graphics it is being added to.
    ("Services/SensorReader.cs",
     "? chosenIsDiscrete",
     "? true",
     "the processor and graphics figures are added only when they are two chips",
     "the graphics counted twice on every laptop with built-in graphics"),

    # Drop the sleeping-card case and the sum is null whenever a laptop card rests.
    ("Services/SensorReader.cs",
     ": data.GpuAsleep ? cpuW\n",
     "\n",
     "a sleeping graphics card counts as off in CPU+GPU Power",
     "CPU+GPU Power reading -- on every laptop on battery"),

    # Take the later attempts away and a driver that settles a second after the third try costs
    # the whole session: every tile reads "--" until somebody restarts Pulse.
    ("Services/SensorHost.cs",
     "reader.Retry();",
     "",
     "sensors that would not open are tried again later",
     "six seconds deciding whether Pulse works for the rest of the session"),

    # Take this away and a launch that threw before it had a process has nothing to react to:
    # the watchdog's own remedy returns immediately when there is no host to replace.
    ("Services/HardwareService.cs",
     "            RelaunchIfAbsent();",
     "",
     "a host that never started is started again by the watchdog",
     "a machine that could not start the host once never starting it again"),

    # Take it out of the list and a NaN battery reading reaches the formatter and the bar.
    ("Services/SensorSnapshot.cs",
     "        d.BatteryLevel = Clean(d.BatteryLevel);",
     "",
     "the battery reading is sanitised like every other one",
     "a tile reading NaN and a bar clamped to nothing"),

    # Take the notification away and switching to megabits converts the number and leaves the
    # old unit beside it, which at a steady zero never corrects itself.
    ("ViewModels/OverlayViewModel.cs",
     "        OnPropertyChanged(nameof(DisplayValue));\n        OnPropertyChanged(nameof(DisplayUnit));\n        OnPropertyChanged(nameof(CompactLine));\n        OnPropertyChanged(nameof(DisplayLabel));",
     "        OnPropertyChanged(nameof(DisplayValue));\n        OnPropertyChanged(nameof(CompactLine));\n        OnPropertyChanged(nameof(DisplayLabel));",
     "changing the network unit changes the unit on screen",
     "Mbps printed under a label that says MB/s"),

    # Take the cancel away and a corner chosen within 400ms of touching a slider is undone by
    # the write that was already scheduled, which sets the position back to Custom.
    ("ViewModels/SettingsViewModel.cs",
     "        CancelPendingPositionSave();\n\n        var s = SettingsService.Instance.Settings;\n        s.OverlayPosition  = position;",
     "        var s = SettingsService.Instance.Settings;\n        s.OverlayPosition  = position;",
     "choosing a preset cancels a position save that has not happened yet",
     "a preset silently undone a moment after it is clicked"),

    # Put it back to x64compatible and setup runs on ARM64, where the x64 kernel driver cannot
    # load: the install finishes and nothing can be read.
    ("setup.iss",
     "ArchitecturesAllowed=x64os",
     "ArchitecturesAllowed=x64compatible",
     "the installer refuses machines the driver cannot work on",
     "a finished install on ARM64 with no sensor readings at all"),

    # Unbind it and the overlay's one instruction to a user with empty tiles leads nowhere,
    # which is where it led for a long time.
    ("Views/MainWindow.xaml",
     "{Binding SensorFault}",
     "{Binding AppVersionLabel}",
     "and About actually shows it",
     "see About for details, where the details are not"),

    # Hide the card again and a single GPU machine has nothing on screen saying what is read.
    ("Views/MainWindow.xaml.cs",
     "GpuSourceSection.Visibility = System.Windows.Visibility.Visible;",
     "GpuSourceSection.Visibility = System.Windows.Visibility.Collapsed;",
     "the GPU source card is shown even with one graphics adapter",
     "a Ryzen laptop with no way to see which GPU is being read"),

    # Copy Task Manager's arithmetic and the tile prints a figure no core is running at.
    ("Services/SensorReader.cs",
     "IsCoreClock",
     "% Processor Performance",
     "the CPU clock is read from the cores, not modelled from a counter",
     "1.35 GHz on screen while every core sits at 0.85"),

    # Loosen the pattern and the cleanup stops the session a running capture is using.
    ("Services/TraceSessionCleanup.cs",
     '@"^Pulse_[0-9]+$"',
     '@"^Pulse"',
     "only the old naming is stopped, anchored and digits only",
     "stopping PulseMonitor, which is the session a live capture is using"),

    # Remove the cap and a misread listing spawns a process per line.
    ("Services/TraceSessionCleanup.cs",
     "if (found.Count >= MostToStop) break;",
     "",
     "and the count is capped",
     "hundreds of processes spawned from one parsing fault"),

    # Take the wrap away and a long tooltip draws as one line as wide as its text, which is
    # how the 1% Low description ended up lying across the desktop.
    ("Resources/Styles.xaml",
     'TextWrapping="Wrap"',
     '',
     "tooltips wrap instead of running off the screen",
     "a description drawn as a nineteen hundred pixel line outside the window"),

    # The obvious factor, and the wrong one.
    ("Models/NetworkUnits.cs",
     "1_048_576f * 8f / 1_000_000f",
     "8f",
     "megabits are converted with the decimal factor, not eight",
     "network speed reading five percent low against Task Manager"),

    # Convert the number but leave the label, or the other way round, and one of them lies.
    ("ViewModels/OverlayViewModel.cs",
     "NetworkUnits.Convert(_value.Value, bits: true)",
     "_value.Value",
     "the unit shown and the value shown are decided together",
     "a tile labelled Mbps showing a number in MB/s"),

    # Convert where the reading is taken and every consumer of it inherits the preference.
    ("Services/SensorReader.cs",
     "data.NetUpload = (data.NetUpload ?? 0) + s.Value.Value / 1_048_576f;",
     "data.NetUpload = (data.NetUpload ?? 0) + s.Value.Value / 1_048_576f * (SettingsService.Instance.Settings.NetworkUnitBits ? 8f : 1f);",
     "the measurement itself does not know about the preference",
     "the stored reading changing meaning with a display setting"),

    # Group by name instead of address and the filters bound to an adapter come back, each
    # reporting the same bytes over the same wire.
    ("Services/SensorReader.cs",
     "nic.GetPhysicalAddress().ToString()",
     "nic.Name",
     "network adapters are grouped by physical address",
     "one Wi-Fi card counted six times"),

    # Keep whichever entry Windows happens to enumerate first and the tile is named after a
    # filter product rather than the adapter.
    ("Services/SensorReader.cs",
     "|| nic.Name.Length < chosen.Length",
     "",
     "and the adapter is preferred over the filters bound to it",
     "the reading taken from whichever binding was enumerated first"),

    # Take the source away and the graphics temperature comes from nowhere at all.
    ("Services/SensorReader.cs",
     "data.GpuTemp ??= DisplayAdapters.Temperature(adapter.Luid);",
     "",
     "the graphics temperature is asked of Windows, per adapter",
     "graphics with no sensor of their own reading nothing on any machine"),

    # Put the processor back where Windows has no answer, which is what showed a temperature
    # on the Intel laptop that Task Manager does not show at all.
    ("Services/SensorReader.cs",
     "data.GpuTemp ??= DisplayAdapters.Temperature(adapter.Luid);",
     "data.GpuTemp ??= DisplayAdapters.Temperature(adapter.Luid);\n        data.GpuTemp ??= data.CpuTemp;",
     "no processor temperature is put on the graphics tile",
     "a processor temperature shown as the GPU's where Task Manager shows none"),

    # Treat "no temperature" as a temperature and every silent adapter reads zero degrees.
    ("Services/DisplayAdapters.cs",
     "if (perf.Temperature == 0) return null;",
     "",
     "an adapter that reports no temperature is left without one",
     "an adapter with no sensor reading 0 degrees"),

    # Assume the scale and a driver reporting whole degrees shows 47 as 4.7.
    ("Services/DisplayAdapters.cs",
     "bool tenths = max > 0 ? max >= 200 : perf.Temperature >= 200;",
     "bool tenths = true;",
     "the temperature scale is taken from what Windows states",
     "a driver reporting whole degrees showing 47 as 4.7"),

    # Believe anything the kernel puts in the field.
    ("Services/DisplayAdapters.cs",
     "return celsius is > 0f and <= 150f ? celsius : null;",
     "return celsius;",
     "and an implausible temperature is discarded rather than shown",
     "a nonsense value from a driver drawn as a temperature"),

    # Guess integrated from the adapter's name or memory instead of asking Windows.
    ("Services/DisplayAdapters.cs",
     "HybridIntegrated = 1u << 5",
     "HybridIntegratedUnused = 1u << 5",
     "integrated is Windows' answer, not a guess from the adapter's name or memory",
     "deciding integrated from the adapter's name or memory size"),

    # Leak the display kernel handle on every poll.
    ("Services/DisplayAdapters.cs",
     "finally { Close(handle); }",
     "finally { }",
     "every display kernel handle opened is handed back",
     "one leaked kernel handle per poll for as long as Pulse runs"),

    # Take the name away and the session becomes "PresentMon", shared with every other tool.
    ("Services/FpsService.cs",
     "--session_name {TraceSessionName} --stop_existing_session",
     "--stop_existing_session",
     "frame capture uses a trace session of its own",
     "silently killing another tool's frame capture"),

    # Put the process id back and every launch abandons a session that is never cleaned up.
    ("Services/FpsService.cs",
     'internal const string TraceSessionName = "PulseMonitor";',
     'internal static readonly string TraceSessionName = $"Pulse_{Environment.ProcessId}";',
     "and that name is constant, so a leaked session is cleaned up next launch",
     "leaking one ETW session per launch until frame capture dies machine wide"),

    # Remove the warning and a starved capture goes back to being completely silent.
    ("Services/FpsService.cs",
     "        ReportSilentCapture();",
     "",
     "a capture that hears nothing eventually says so",
     "a dead capture producing no log line at all"),

    ("Services/FpsService.cs",
     "!double.IsFinite(ms) || ms <= 0 || ms > 10_000",
     "ms <= 0",
     "a frame time must be finite before it is believed",
     "NaN reaching the tile as a frame rate"),
]


def extract(commit, into):
    """Writes the historical version of each watched file into a scratch directory."""
    written = 0
    for rel in WATCHED:
        result = subprocess.run(["git", "show", f"{commit}:{rel}"],
                                cwd=REPO, capture_output=True)
        if result.returncode != 0:
            continue   # file did not exist at that commit
        target = into / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(result.stdout)
        written += 1
    return written


def run_guards(root):
    """Returns {check name: passed}."""
    result = subprocess.run([sys.executable, str(TESTS / "guards.py"), str(root)],
                            capture_output=True, text=True)
    outcomes = {}
    for line in result.stdout.splitlines():
        m = re.match(r"\s+(PASS|FAIL)\s+(.+?)(?:\s+->.*)?$", line)
        if m:
            outcomes[m.group(2).strip()] = (m.group(1) == "PASS")
    return outcomes


def main():
    print("Do the guards actually bite?\n")

    passed = failed = 0

    for commit, fragment, bug in CASES:
        scratch = Path(tempfile.mkdtemp(prefix="pulse-teeth-"))
        try:
            count = extract(commit, scratch)
            if count == 0:
                print(f"  FAIL  {commit}: nothing could be extracted")
                failed += 1
                continue

            outcomes = run_guards(scratch)

            matching = [(name, ok) for name, ok in outcomes.items() if fragment in name]
            if not matching:
                print(f"  FAIL  no guard matching '{fragment}' ran against {commit}")
                failed += 1
                continue

            name, ok = matching[0]
            if ok:
                print(f"  FAIL  {name}")
                print(f"        passed against {commit}, so it is not testing anything")
                failed += 1
            else:
                print(f"  PASS  {name}")
                print(f"        correctly fails against {commit}: {bug}")
                passed += 1
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    print("\nReintroducing each fault into the current source:\n")

    for rel, find, replace, fragment, bug in MUTATIONS:
        scratch = Path(tempfile.mkdtemp(prefix="pulse-teeth-"))
        try:
            # Start from the working tree, then damage exactly one line of it.
            for watched in WATCHED:
                source = REPO / watched
                if not source.exists():
                    continue
                target = scratch / watched
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)

            damaged = scratch / rel
            text = damaged.read_text(encoding="utf-8-sig")
            if find not in text:
                print(f"  FAIL  {fragment}: could not find the line to damage in {rel}")
                failed += 1
                continue
            # A line that appears more than once is damaged at each place in turn, and the guard
            # must fail every time. Damaging only the first passed silently in 1.3.3, when new
            # code repeated a line before the one the guard watches.
            places = [i for i in range(len(text)) if text.startswith(find, i)]
            missed = []
            ran = None
            for at in places:
                damaged.write_text(text[:at] + replace + text[at + len(find):], encoding="utf-8-sig")
                outcomes = run_guards(scratch)
                matching = [(name, ok) for name, ok in outcomes.items() if fragment in name]
                if not matching:
                    ran = None
                    break
                ran = matching[0][0]
                if matching[0][1]:
                    line = text.count(chr(10), 0, at) + 1
                    missed.append(line)

            if ran is None:
                print(f"  FAIL  no guard matching '{fragment}' ran")
                failed += 1
            elif missed:
                print(f"  FAIL  {ran}")
                print(f"        passed with the fault put back at line {', '.join(map(str, missed))} of {rel}, so it is not testing that")
                failed += 1
            else:
                print(f"  PASS  {ran}")
                print(f"        catches it: {bug}" + (f" (at all {len(places)} places)" if len(places) > 1 else ""))
                passed += 1
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    print(f"\n{passed} guards have teeth, {failed} do not")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
