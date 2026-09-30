using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Threading;

namespace Pulse.Services;

public class SensorData
{
    public float? CpuTemp { get; set; }
    public float? CpuPower { get; set; }
    public float? CpuClock { get; set; }
    public float? CpuUsage { get; set; }
    public float? GpuTemp { get; set; }
    public float? GpuPower { get; set; }
    public float? GpuClock { get; set; }
    public float? GpuVram { get; set; }
    public float? GpuUsage { get; set; }
    public float? RamUsed { get; set; }
    public float? SysPower { get; set; }
    public float? NetUpload { get; set; }
    public float? NetDownload { get; set; }
    public float? DiskActivity { get; set; }

    /// Charge left in the battery, as a percentage. Null on a machine with no battery, which
    /// is a desktop showing nothing rather than a desktop showing zero percent.
    public float? BatteryLevel { get; set; }
    public float? Fps { get; set; }
    public float? Fps1Low { get; set; }
    public float TotalRamGb { get; set; }
    public float TotalVramGb { get; set; }

    /// <summary>
    /// True when GpuVram is memory borrowed from system RAM rather than the adapter's own.
    /// </summary>
    /// <remarks>
    /// Graphics built into a processor have essentially no memory of their own, so what they
    /// use comes from the shared pool. Reporting that under a name meaning "the card's own
    /// memory" would be wrong in the way this project keeps having to remove, so the tile is
    /// renamed instead of the number being quietly redefined.
    /// </remarks>
    public bool VramIsShared { get; set; }

    /// <summary>
    /// True when GpuTemp belongs to graphics that are part of the processor, so the reading is
    /// the die's.
    /// </summary>
    /// <remarks>
    /// Set from Windows' own view of the adapter, not from its name or its memory. It is the
    /// same figure Task Manager shows on its GPU page, and the same figure Pulse shows as CPU
    /// Temp, which is why the tile is renamed: two identical readings with no explanation look
    /// like a bug, and the explanation is that there is one piece of silicon measured once.
    /// </remarks>
    public bool GpuTempIsDie { get; set; }

    public float? GetById(string id) => id switch
    {
        "cpu_temp"     => CpuTemp,
        "cpu_power"    => CpuPower,
        "cpu_clock"    => CpuClock,
        "cpu_usage"    => CpuUsage,
        "gpu_temp"     => GpuTemp,
        "gpu_power"    => GpuPower,
        "gpu_clock"    => GpuClock,
        "gpu_vram"     => GpuVram,
        "gpu_usage"    => GpuUsage,
        "ram_used"     => RamUsed,
        "sys_power"    => SysPower,
        "net_upload"   => NetUpload,
        "net_download" => NetDownload,
        "disk_activity"=> DiskActivity,
        "battery"      => BatteryLevel,
        "fps"          => Fps,
        "fps_1low"     => Fps1Low,
        _ => null
    };
}

/// A GPU Pulse can read from, as offered in the settings GPU picker.
public class GpuInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// True when the adapter is a real graphics card rather than graphics built into the CPU.
    public bool IsDiscrete { get; init; }
}

/// <summary>
/// Supplies the readings, by supervising the process that takes them.
///
/// Pulse used to read the hardware itself, on a timer, in this class. It no longer does, and
/// the reason is worth stating plainly: a fault inside a vendor driver cannot be caught. When a
/// user disabled their NVIDIA card while Pulse was running, reading GPU power through a handle
/// that card still owned raised an access violation deep inside NVML, and .NET ended the
/// process without running one line of managed code. The try/catch around the poll was not
/// bypassed by accident — it cannot run at all. Pulse simply vanished, and the only trace was
/// an entry in the Windows event log.
///
/// So the reading happens in a child process now, and this class watches it. If it dies, this
/// starts another; if it stops answering, this replaces it. The public surface is unchanged
/// from when the reading was done here, because from the outside nothing about it should look
/// different — except that hardware faults now cost a second of "--" instead of the whole app.
/// </summary>
public class HardwareService : IDisposable
{
    /// Lazy rather than `??=`: that is not atomic, and these are reached from the reader
    /// thread and the UI thread at the same time during startup. Losing the race builds two
    /// instances, each with its own event subscribers, so notifications reach an object
    /// nobody is listening to.
    private static readonly Lazy<HardwareService> LazyInstance =
        new(() => new HardwareService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static HardwareService Instance => LazyInstance.Value;

    public SensorData Current { get; private set; } = new();
    public event EventHandler<SensorData>? SensorsUpdated;
    public double PollingIntervalSeconds { get; private set; } = 2;

    /// <summary>
    /// How much memory this machine actually has, or zero when that is not yet known.
    ///
    /// Zero means unknown and must never be drawn as a capacity. These used to start at 16 and
    /// 6, which are not measurements of anything: they were a guess that looked like a reading.
    /// On any machine with only integrated graphics nothing ever replaced the VRAM one, so the
    /// tile confidently reported a 6 GB capacity that did not exist, sized its bar against it,
    /// and coloured warnings from it. A user eventually asked where the 6 GB came from, and the
    /// honest answer was that we made it up.
    /// </summary>
    public float TotalRamGb { get; private set; }
    public float TotalVramGb { get; private set; }

    /// <summary>
    /// Whether the video memory reading is memory borrowed from system RAM.
    /// </summary>
    /// Read by the overlay so the tile can be named for what it is showing rather than for
    /// what a graphics tile usually shows.
    public bool VramIsShared { get; private set; }

    /// <summary>
    /// Whether the graphics temperature is a die reading, because the graphics are part of the
    /// processor.
    /// </summary>
    /// Read by the overlay for the same reason as VramIsShared: the tile says which reading
    /// it is showing rather than leaving two identical numbers unexplained.
    public bool GpuTempIsDie { get; private set; }

    /// Every GPU detected on this machine, for the settings picker.
    public IReadOnlyList<GpuInfo> AvailableGpus { get; private set; } = Array.Empty<GpuInfo>();

    /// Name of the GPU the GPU tiles are currently reading from.
    public string ActiveGpuName { get; private set; } = "";

    /// Raised when the set of detected GPUs changes (first reading, or an eGPU appearing).
    public event EventHandler? GpuListChanged;

    /// <summary>
    /// True once sensors have been opened successfully. While false every tile reads "--",
    /// which previously looked identical to hardware that genuinely reports nothing.
    /// </summary>
    public bool IsHardwareReady { get; private set; }

    /// Why sensors are unavailable, for the control panel to show. Null when all is well.
    public string? HardwareFault { get; private set; }

    public event EventHandler? HardwareStateChanged;

    /// <summary>
    /// A one-line summary of the process taking the readings, for the diagnostics export.
    ///
    /// The restart count is the part worth having. Once a fault has been recovered from there
    /// is nothing in the readings to say it ever happened, so a report from a machine whose
    /// graphics driver keeps falling over looks identical to one where everything is fine.
    /// </summary>
    public string SensorHostStatus
    {
        get
        {
            lock (_hostLock)
            {
                var host = _host;
                var age  = TimeSpan.FromMilliseconds(Environment.TickCount64 - _hostStartedAt);
                var silence = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastSnapshotAt);

                if (host == null) return $"not running, restarted {_restarts} time(s) this session";

                return $"running {age.TotalMinutes:F0}m, last reading {silence.TotalSeconds:F0}s ago, "
                     + $"restarted {_restarts} time(s) this session";
            }
        }
    }

    // ── Supervision ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long GPU readings may stop arriving before those tiles are blanked.
    ///
    /// Only the GPU tiles, because the GPU is the part actually in doubt. Switching a graphics
    /// card off is the one event that reliably interrupts readings, and blanking the CPU,
    /// memory, disk and network tiles at the same moment made a recovery that works look like
    /// the whole app had fallen over. Those readings are held for a while longer instead — see
    /// <see cref="EverythingStaleAfter"/>.
    ///
    /// A stale GPU number is the specific thing worth removing quickly. It looks live and is
    /// not, which is exactly what a user saw when their readings froze at whatever they had
    /// been at the moment the card went away.
    /// </summary>
    private static readonly TimeSpan GpuStaleAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long everything else may be held before it is blanked too.
    ///
    /// Long enough to cover a card being switched off, the sensor host being replaced, and the
    /// machine's hardware being enumerated again — the whole sequence, without the tiles that
    /// have nothing to do with graphics ever flickering. Past this, readings are old enough
    /// that showing them would be a lie rather than a courtesy.
    /// </summary>
    private static readonly TimeSpan EverythingStaleAfter = TimeSpan.FromSeconds(30);

    /// When a host stops answering entirely. Generous, because opening sensors on a cold
    /// machine genuinely can take this long.
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(45);

    /// A host that lasts this long is considered healthy, and the restart backoff resets.
    /// Without it a machine that faults once a day would eventually be waiting minutes.
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The three deadlines above, but never shorter than the readings themselves are.
    /// </summary>
    /// <remarks>
    /// Those figures were written for the default two second interval and applied whatever the
    /// interval actually was. At five seconds, the fastest rate the panel offers, a poll that
    /// arrives a moment late is already past the five second mark, so the graphics tiles blank
    /// and refill on almost every cycle: a visible flicker caused by nothing being wrong. The
    /// setting also accepts up to sixty seconds for anyone who edits it by hand, and there the
    /// arithmetic is worse than a flicker. Every reading would be blanked at thirty seconds and
    /// the host killed and replaced at forty five, over and over, so a machine set to poll once
    /// a minute would never receive a single reading.
    ///
    /// Two intervals plus the original allowance. One late poll is ordinary; two in a row is
    /// the thing these deadlines were written to notice.
    /// </remarks>
    private TimeSpan Deadline(TimeSpan baseline)
    {
        double interval = PollingIntervalSeconds > 0 ? PollingIntervalSeconds : 2;
        return baseline + TimeSpan.FromSeconds(interval * 2);
    }

    private readonly object _hostLock = new();
    private readonly Dispatcher? _dispatcher;

    private Process? _host;
    private int _restarts;
    private long _hostStartedAt;
    private long _lastSnapshotAt;
    private bool _gpuBlanked;
    private bool _blanked;
    private bool _disposed;

    /// Set while we are deliberately ending a host, so its exit is not treated as a fault.
    private bool _replacing;

    private readonly System.Threading.Timer _watchdog;

    /// UTF-8 on every stream, explicitly on both sides. The default is the console's OEM
    /// codepage, which mangles anything outside ASCII — and adapter names are not ours.
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private HardwareService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher;

        try { PollingIntervalSeconds = SettingsService.Instance.Settings.PollingIntervalSeconds; }
        catch (Exception ex)
        {
            // Falls back to the default interval, which is a working Pulse rather than none.
            // Worth a line though: the user chose a rate and is not getting it.
            LogService.Error(nameof(HardwareService),
                "Could not read the polling interval from settings; using the default", ex);
        }

        StartHost();

        _watchdog = new System.Threading.Timer(_ => CheckHost(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        // Reconfigure when the user turns a whole category of tiles on or off, or picks a
        // different GPU. Both are just a line down the pipe now; the host does the work.
        SettingsService.Instance.SettingsChanged += (_, _) => ApplySettings();
    }

    /// <summary>
    /// Works out which sensor groups are actually needed for the tiles in use.
    ///
    /// LibreHardwareMonitor updates every sensor in an enabled group on each poll, and that
    /// isn't cheap — reading the discrete GPU alone dominates a poll. Enabling only what the
    /// visible tiles require avoids paying for data nothing displays.
    /// </summary>
    private static SensorSubsystems RequiredSubsystems()
    {
        var active = SettingsService.Instance.Settings.ActiveTileIds;
        var needed = SensorSubsystems.None;

        foreach (var id in active)
        {
            needed |= id switch
            {
                "cpu_usage" or "cpu_temp" or "cpu_clock" or "cpu_power" => SensorSubsystems.Cpu,
                "gpu_usage" or "gpu_temp" or "gpu_clock" or "gpu_power" or "gpu_vram" => SensorSubsystems.Gpu,
                "ram_used"      => SensorSubsystems.Memory,
                "disk_activity" => SensorSubsystems.Storage,
                "battery"       => SensorSubsystems.Battery,
                "net_upload" or "net_download" => SensorSubsystems.Network,
                // Total power is derived from both chips, so it needs each of them.
                "sys_power"     => SensorSubsystems.Cpu | SensorSubsystems.Gpu,
                _               => SensorSubsystems.None,
            };
        }

        // LibreHardwareMonitor only creates its Intel GPU group when CPU monitoring is on
        // (`if (_cpuEnabled) Add(new IntelGpuGroup(GetIntelCpus(), ...))`), because the
        // integrated GPU's sensors hang off the CPU package. Gating the CPU subsystem away
        // therefore removes every GPU reading on an Intel-iGPU-only machine — the user only
        // finds out after a restart. The CPU read costs ~14ms, which is worth paying.
        if (needed.HasFlag(SensorSubsystems.Gpu)) needed |= SensorSubsystems.Cpu;

        return needed;
    }

    private static string SubsystemsArgument() =>
        RequiredSubsystems().ToString().Replace(" ", "");

    private static string PinnedGpu()
    {
        try   { return SettingsService.Instance.Settings.SelectedGpuId ?? ""; }
        catch { return ""; }
    }

    private SensorSubsystems _sentSubsystems = (SensorSubsystems)(-1);
    private string _sentGpu = "\0";   // deliberately not a value any identifier can take

    /// Sends whatever has actually changed. Settings are saved often and mostly for reasons
    /// the host does not care about, so this is called far more than it does anything.
    private void ApplySettings()
    {
        try
        {
            var subsystems = RequiredSubsystems();
            if (subsystems != _sentSubsystems)
            {
                _sentSubsystems = subsystems;
                Send(SensorCommand.Subsystems(subsystems.ToString().Replace(" ", "")));
            }

            var gpu = PinnedGpu();
            if (gpu != _sentGpu)
            {
                _sentGpu = gpu;
                Send(SensorCommand.Gpu(gpu));
            }
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(HardwareService), "Passing a settings change to the sensor host failed", ex);
        }
    }

    public void SetInterval(double seconds)
    {
        PollingIntervalSeconds = seconds;
        Send(SensorCommand.Interval(seconds));
    }

    /// <summary>
    /// Asks the host to enumerate the machine's hardware again.
    ///
    /// LibreHardwareMonitor builds its device list once, when it opens, so a graphics card
    /// that has been switched off keeps being polled through handles the driver no longer
    /// honours, and one that has just appeared is never polled at all. Neither resolves
    /// itself, and the first of the two is what makes readings freeze at a stale value.
    /// </summary>
    public void Rescan()
    {
        LogService.Info(nameof(HardwareService), "Asking the sensor host to re-enumerate hardware.");
        Send(SensorCommand.RescanHardware());
    }

    /// <summary>
    /// Starts a fresh sensor host on purpose, because something it only looks at once has changed.
    /// </summary>
    /// <remarks>
    /// Installing the sensor driver is the case this exists for. A rescan builds a new Computer
    /// inside the same process, and whether the library looks for the driver again at that point
    /// or remembers what it found at startup is its own business. A new process settles it.
    /// </remarks>
    public void RestartHost(string why) => ReplaceHost(why, deliberate: true);

    private void Send(string command)
    {
        lock (_hostLock)
        {
            var host = _host;
            if (host == null || host.HasExited) return;

            try
            {
                host.StandardInput.Write(command);
                host.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                // The host has gone or the pipe broke. The watchdog will notice; a command
                // lost in the meantime is resent when the replacement starts, because the
                // replacement is given the current settings on its command line.
                LogService.Warn(nameof(HardwareService), $"Could not reach the sensor host: {ex.GetType().Name}");
            }
        }
    }

    // ── The child ───────────────────────────────────────────────────────────────────

    private void StartHost()
    {
        lock (_hostLock)
        {
            // Whatever brought us here, this is the attempt it was waiting for. Cleared first
            // so that every way out of this method, including the failures, leaves the
            // watchdog free to try again later.
            _relaunching = false;

            if (_disposed) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Fail("Sensors unavailable. Pulse could not locate its own program file.");
                return;
            }

            var subsystems = SubsystemsArgument();
            var gpu = PinnedGpu();

            _sentSubsystems = RequiredSubsystems();
            _sentGpu        = gpu;

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding  = Utf8,
                StandardInputEncoding  = Utf8,
            };

            // One argument per value, so an identifier straight out of a driver never has to
            // survive a second round of quoting.
            info.ArgumentList.Add(SensorHost.Argument);
            info.ArgumentList.Add($"--subsystems={subsystems}");
            info.ArgumentList.Add($"--gpu={gpu}");
            info.ArgumentList.Add($"--interval={PollingIntervalSeconds.ToString(CultureInfo.InvariantCulture)}");

            try
            {
                var host = new Process { StartInfo = info };
                host.Start();

                // Immediately after Start, so the window where an abrupt end to Pulse could
                // strand this process is as small as possible.
                if (!ChildProcessJob.Adopt(host))
                    LogService.Warn(nameof(HardwareService), "The sensor host could not be tied to Pulse's lifetime.");

                _host           = host;
                _hostStartedAt  = Environment.TickCount64;
                _lastSnapshotAt = Environment.TickCount64;
                _replacing      = false;

                // A host exists again, so the next launch failure starts its backoff afresh
                // rather than inheriting a long wait from whatever went wrong earlier.
                _launchAttempts      = 0;
                _nextLaunchAttemptAt = 0;

                Pump($"Pulse sensor readings",     () => ReadSnapshots(host));
                Pump($"Pulse sensor diagnostics",  () => ReadDiagnostics(host));

                LogService.Info(nameof(HardwareService), $"Sensor host started (pid {host.Id}, reading {subsystems}).");
            }
            catch (Exception ex)
            {
                LogService.Error(nameof(HardwareService), "Starting the sensor host failed", ex);

                // Disposed, not merely dropped. Process.Start can succeed and the wiring after
                // it fail, which leaves a live object holding handles that nothing will ever
                // reach again.
                try { _host?.Dispose(); } catch { }   // it already failed; nothing follows
                _host = null;
                Fail("Sensors unavailable. Pulse could not start the process that reads them.");
            }
        }
    }

    /// Dedicated threads rather than the thread pool. These block on a pipe for the lifetime
    /// of the host, which is exactly the thing pool threads must not do.
    private static void Pump(string name, Action work)
    {
        new Thread(() =>
        {
            try { work(); }
            catch (Exception ex)
            {
                // A pump thread ending early is not fatal — the watchdog notices the readings
                // stopping and replaces the host — but it was previously invisible, so the
                // symptom was "sensors stopped" with nothing anywhere saying why.
                LogOnce($"the {name} thread", ex);
            }
        })
        {
            IsBackground = true,
            Name         = name,
        }.Start();
    }

    /// <summary>
    /// Records a failure the first time each kind of it happens, and then stays quiet.
    /// </summary>
    /// <remarks>
    /// For the failures that repeat on a timer. The watchdog runs every two seconds and the
    /// reading pump runs per snapshot, so logging every occurrence of a persistent fault would
    /// write hundreds of identical lines an hour and push the evidence of whatever started it
    /// out of the file. Keyed by where it happened and what was thrown, so a second, different
    /// fault in the same place is still reported.
    /// </remarks>
    private static readonly HashSet<string> Reported = new();

    private static void LogOnce(string what, Exception ex)
    {
        lock (Reported)
        {
            if (!Reported.Add($"{what}|{ex.GetType().FullName}")) return;
        }

        LogService.Error(nameof(HardwareService),
            $"Unexpected failure in {what}; this is reported once per kind of fault", ex);
    }

    /// <summary>
    /// Reads snapshots until the host closes its output, which happens when it exits for any
    /// reason at all — including being terminated mid-instruction by a driver fault.
    /// </summary>
    private void ReadSnapshots(Process host)
    {
        try
        {
            while (host.StandardOutput.ReadLine() is { } line)
            {
                var snapshot = SensorProtocol.TryParse(line);
                if (snapshot == null) continue;   // the ready banner, or a line we cannot use

                _lastSnapshotAt = Environment.TickCount64;
                Publish(snapshot);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(nameof(HardwareService), $"The reading channel closed: {ex.GetType().Name}");
        }

        OnHostEnded(host);
    }

    /// The host's log lines, folded into ours. It deliberately does not write to the log file
    /// itself: two processes appending to one file lose lines to each other, and these are
    /// exactly the lines worth keeping.
    private void ReadDiagnostics(Process host)
    {
        try
        {
            while (host.StandardError.ReadLine() is { } line)
            {
                var split = line.IndexOf('|');
                var level = split > 0 ? line[..split] : "info";
                var text  = split > 0 ? line[(split + 1)..] : line;

                switch (level)
                {
                    case "error": LogService.Warn("SensorHost", text); break;   // logged, but not our crash
                    case "warn":  LogService.Warn("SensorHost", text); break;
                    case "info":  LogService.Info("SensorHost", text); break;

                    // No level in front of it, which means the host did not write it. The only
                    // other thing with a handle to that stream is the runtime, and the only
                    // time it writes there is while the process is dying: the unhandled
                    // exception and the stack under it.
                    //
                    // These were recorded at INFO, so a sensor host being killed by an access
                    // violation inside a graphics driver read like an ordinary line. A user's
                    // log had fourteen of them and they did not stand out at all, which is the
                    // opposite of what a log is for.
                    default: LogService.Error("SensorHost", text); break;
                }
            }
        }
        catch (Exception ex)
        {
            // This loop is the only route the sensor host has for explaining itself. Losing it
            // silently meant every later problem in the host became unexplainable, which is
            // the opposite of what this channel exists for.
            LogOnce("the sensor host's diagnostic reader", ex);
        }
    }

    /// <summary>
    /// Called when a host's output ends. Decides whether that was expected and, if not,
    /// says so and starts another.
    /// </summary>
    private void OnHostEnded(Process host)
    {
        lock (_hostLock)
        {
            if (_disposed) return;
            if (!ReferenceEquals(_host, host)) return;   // already replaced

            int code;
            try   { host.WaitForExit(2000); code = host.HasExited ? host.ExitCode : -1; }
            catch { code = -1; }

            var lived = TimeSpan.FromMilliseconds(Environment.TickCount64 - _hostStartedAt);

            if (_replacing)
            {
                LogService.Info(nameof(HardwareService), "Sensor host replaced.");
            }
            else
            {
                // The whole reason this process exists. An access violation inside a driver
                // shows up here as a nonzero exit code and nothing else — there is no
                // exception to catch, in this process or in that one.
                LogService.Warn(nameof(HardwareService),
                    $"The sensor host stopped unexpectedly after {lived.TotalSeconds:F0}s (exit code {code}). Starting another.");
            }

            // Disposed, not merely dropped. This runs on every replacement, and a machine whose
            // graphics driver faults occasionally will replace the host many times across a
            // session left running for days. Each one holds a process handle and the handles
            // for three redirected pipes, and none of it comes back on its own.
            try { host.Dispose(); } catch { }   // it has already exited; nothing follows

            _host = null;

            // Claimed before the backoff below, which happens outside the lock. The watchdog
            // runs every two seconds and would otherwise see no host during that wait and
            // start a second one beside the replacement this method is about to make.
            _relaunching = true;

            // A host that stayed up long enough to be healthy earns a clean slate, so a
            // machine that faults occasionally never accumulates its way into a long wait.
            if (lived >= Settled) _restarts = 0;
            _restarts++;
        }

        // Backoff, capped. Unlimited restarts on purpose: a driver being reinstalled can fault
        // repeatedly for a minute and then work perfectly, and giving up would leave Pulse
        // showing "--" until someone restarted it by hand.
        var wait = TimeSpan.FromSeconds(Math.Min(_restarts, 10));
        Thread.Sleep(wait);

        if (_restarts >= 4)
            Fail("Sensor readings keep stopping. A graphics driver on this machine may be faulting; "
               + "see the log for details.");

        StartHost();
    }

    /// <summary>
    /// Starts a host when there is none, which nothing else is in a position to do.
    /// </summary>
    /// <remarks>
    /// Every other route back from a dead host hangs off the child's Exited event, and a launch
    /// that threw before it had a process never raises one. That path set _host to null, showed
    /// "Sensors unavailable" and stopped: the watchdog's own remedy is ReplaceHost, which
    /// returns immediately when there is nothing to replace. So a machine that could not start
    /// the host once could not start it ever, for the rest of the session.
    ///
    /// The wait grows with the number of attempts and is capped, on the same reasoning as the
    /// restart backoff: whatever prevents a launch is usually brief, occasionally permanent,
    /// and worth retrying either way because the alternative is a dead Pulse.
    /// </remarks>
    private void RelaunchIfAbsent()
    {
        lock (_hostLock)
        {
            // _relaunching covers the gap in HostExited, which clears _host and then waits out
            // its backoff before starting the replacement. Without it the watchdog would see no
            // host during that wait and start a second one alongside.
            if (_disposed || _host != null || _relaunching) return;

            long now = Environment.TickCount64;
            if (now < _nextLaunchAttemptAt) return;

            _relaunching         = true;
            _launchAttempts++;
            _nextLaunchAttemptAt = now + (long)LaunchRetryWait(_launchAttempts).TotalMilliseconds;
        }

        LogService.Warn(nameof(HardwareService),
            $"No sensor host is running; starting one (attempt {_launchAttempts}).");

        StartHost();
    }

    /// Counted separately from _restarts, which means "the host keeps dying" and carries a
    /// message about faulting drivers. A host that never started is a different problem.
    private int  _launchAttempts;
    private long _nextLaunchAttemptAt;
    private bool _relaunching;

    private static TimeSpan LaunchRetryWait(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(attempt * 5, 60));

    /// <summary>
    /// Ends the current host so a fresh one takes its place. Used when it has stopped
    /// answering: there is nothing to ask a wedged process, and its replacement starts clean.
    /// </summary>
    private void ReplaceHost(string why, bool deliberate = false)
    {
        lock (_hostLock)
        {
            var host = _host;
            if (host == null) return;

            if (deliberate) LogService.Info(nameof(HardwareService), $"Restarting the sensor host: {why}");
            else            LogService.Warn(nameof(HardwareService), $"Replacing the sensor host: {why}");
            _replacing = true;

            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// Runs every couple of seconds and answers two questions: are readings still arriving,
    /// and if not, is what is on screen still worth believing?
    /// </summary>
    private void CheckHost()
    {
        if (_disposed) return;

        try
        {
            var silent = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastSnapshotAt);

            // Stale readings are worse than none. A frozen number looks live, and looking live
            // while being an hour old is how a user ends up reporting that their GPU sits at a
            // constant temperature. Taken away in two stages so that switching a graphics card
            // off does not blank the tiles that have nothing to do with graphics.
            if (silent > Deadline(GpuStaleAfter) && !_gpuBlanked)
            {
                _gpuBlanked = true;
                Hold(alsoClearTheRest: false);
            }

            if (silent > Deadline(EverythingStaleAfter) && !_blanked)
            {
                _blanked = true;
                Hold(alsoClearTheRest: true);
            }

            // Alive but not answering. Rarer than a crash and more confusing, because nothing
            // has ended and nothing is logged; the readings simply stop.
            if (silent > Deadline(SilentAfter))
            {
                _lastSnapshotAt = Environment.TickCount64;   // don't re-trigger while it dies
                ReplaceHost($"no readings for {silent.TotalSeconds:F0}s");
            }

            RelaunchIfAbsent();
        }
        catch (Exception ex)
        {
            // The watchdog is what notices a dead or wedged sensor host and replaces it. If it
            // throws, supervision has stopped and nothing else is watching; the readings would
            // simply never come back, with no crash and no log entry to explain it. Reported
            // once per kind of fault because this runs every two seconds.
            LogOnce("the sensor host watchdog", ex);
        }
    }

    /// <summary>
    /// Republishes the last reading with the parts we can no longer vouch for removed.
    ///
    /// Called when readings have stopped arriving — a graphics card switched off, the sensor
    /// host being replaced, hardware being enumerated again. The GPU tiles go first and the
    /// rest follow much later, so the common case (a card disappearing, the host recovering,
    /// the integrated GPU taking over) shows exactly the tiles that are genuinely unknown and
    /// leaves the others alone.
    ///
    /// Frame rate is deliberately left as it is. It comes from the capture process in Pulse
    /// itself, which is entirely unaffected by any of this and is still perfectly live.
    /// </summary>
    private void Hold(bool alsoClearTheRest)
    {
        var previous = Current;

        var data = new SensorData
        {
            // Never in doubt: measured here, not by the sensor host.
            Fps         = previous.Fps,
            Fps1Low     = previous.Fps1Low,

            // Totals are properties of the machine, not readings. Clearing them would make
            // every tile that shows "used of total" lose its scale as well as its value.
            TotalRamGb  = previous.TotalRamGb,
            TotalVramGb = previous.TotalVramGb,
            VramIsShared = previous.VramIsShared,
            GpuTempIsDie = previous.GpuTempIsDie,
        };

        if (!alsoClearTheRest)
        {
            data.CpuTemp      = previous.CpuTemp;
            data.CpuPower     = previous.CpuPower;
            data.CpuClock     = previous.CpuClock;
            data.CpuUsage     = previous.CpuUsage;
            data.RamUsed      = previous.RamUsed;
            data.NetUpload    = previous.NetUpload;
            data.NetDownload  = previous.NetDownload;
            data.DiskActivity = previous.DiskActivity;

            // Not carried over: it is the sum of CPU and GPU power, and half of that sum has
            // just become unknown. A total that silently means "CPU only" is the exact bug
            // this field was rewritten to avoid.
        }

        Current = data;
        Raise(() => SensorsUpdated?.Invoke(this, data));
    }

    // ── Publishing ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes one snapshot from the host, fills in what only Pulse knows, and raises it.
    ///
    /// Everything reaching subscribers goes through here and through the dispatcher, because
    /// tiles are bound to the UI. This used to be guaranteed by polling on a DispatcherTimer;
    /// the readings now arrive on a pipe thread instead, so the marshalling has to be explicit.
    /// </summary>
    private void Publish(SensorSnapshot snapshot)
    {
        var data = snapshot.Data;

        // Frame rate is captured here, not in the host: PresentMon belongs to this process.
        //
        // Only if it already exists. This runs on the thread reading the pipe, and touching
        // FpsService for the first time from here would build its DispatcherTimer against a
        // thread that has no message loop. App creates it during startup a moment after this
        // class, so at worst the first reading or two carry no frame rate.
        try
        {
            if (FpsService.IsStarted)
            {
                data.Fps     = FpsService.Instance.CurrentFps;
                data.Fps1Low = FpsService.Instance.OnePercentLowFps;
            }
        }
        catch (Exception ex)
        {
            // Runs per snapshot, so reported once per kind.
            LogOnce("copying the frame rate into a reading", ex);
        }

        // A capacity belongs to the adapter it was measured from, so a change of GPU clears it
        // rather than carrying it across. Without this, switching a laptop to its integrated
        // graphics left the discrete card's video memory on screen as the new one's capacity.
        if (snapshot.Ready && !string.Equals(ActiveGpuName, snapshot.ActiveGpuName, StringComparison.Ordinal))
            TotalVramGb = 0;

        // Totals otherwise persist. They are read from a sensor that does not report on every
        // poll, and zeroing them each time would make the capacity flicker away and back.
        if (data.TotalRamGb  > 0) TotalRamGb  = data.TotalRamGb;
        if (data.TotalVramGb > 0) TotalVramGb = data.TotalVramGb;

        // Which pool the reading came from, followed only while there is a reading. A held
        // snapshot keeps the last answer rather than flipping the tile's name to and fro while
        // the readings are briefly missing.
        if (data.GpuVram is not null) VramIsShared = data.VramIsShared;

        // Same rule for the temperature: followed only while there is a reading, so a blank
        // poll does not rename the tile back and forth.
        if (data.GpuTemp is not null) GpuTempIsDie = data.GpuTempIsDie;

        bool gpusChanged   = false;
        bool stateChanged  = false;

        if (snapshot.Ready)
        {
            _blanked    = false;
            _gpuBlanked = false;

            if (!IsHardwareReady || HardwareFault != null)
            {
                IsHardwareReady = true;
                HardwareFault   = null;
                _restarts       = 0;
                stateChanged    = true;
            }

            // Judged on whether the host looked, not on whether it found anything. An empty
            // list used to be discarded either way, so unplugging an external card and
            // rescanning left it still listed in the picker, still selectable, and connected
            // to nothing.
            if (snapshot.GpusKnown && !SameGpus(snapshot.Gpus))
            {
                AvailableGpus = snapshot.Gpus;
                gpusChanged   = true;
            }

            ActiveGpuName = snapshot.ActiveGpuName;
        }
        else if (snapshot.Fault is { Length: > 0 } fault && fault != HardwareFault)
        {
            IsHardwareReady = false;
            HardwareFault   = fault;
            stateChanged    = true;
        }

        Current = data;

        Raise(() =>
        {
            SensorsUpdated?.Invoke(this, data);
            if (gpusChanged)  GpuListChanged?.Invoke(this, EventArgs.Empty);
            if (stateChanged) HardwareStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private bool SameGpus(IReadOnlyList<GpuInfo> incoming)
    {
        if (incoming.Count != AvailableGpus.Count) return false;

        for (int i = 0; i < incoming.Count; i++)
            if (incoming[i].Id != AvailableGpus[i].Id || incoming[i].IsDiscrete != AvailableGpus[i].IsDiscrete)
                return false;

        return true;
    }

    /// <summary>
    /// Raises events on the UI thread, without waiting for them.
    ///
    /// BeginInvoke rather than Invoke: this runs on the thread reading the pipe, and blocking
    /// that thread on the UI stops readings arriving for as long as the UI is busy. Nothing
    /// here needs to complete before the next line is read.
    /// </summary>
    private void Raise(Action action)
    {
        try
        {
            if (_dispatcher == null || _dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            // Not only our own failures: when this is already on the interface thread the
            // subscriber runs inline, so a fault in a view model handler surfaces here. That
            // used to disappear entirely. Reported once per kind, because this runs for every
            // snapshot and a subscriber that throws will throw on all of them.
            LogOnce("a sensor update handler", ex);
        }
    }

    private void Fail(string reason)
    {
        if (HardwareFault == reason) return;

        IsHardwareReady = false;
        HardwareFault   = reason;
        Raise(() => HardwareStateChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _disposed = true;

        try { _watchdog.Dispose(); } catch { }

        lock (_hostLock)
        {
            var host = _host;
            _host = null;
            if (host == null) return;

            // Politely first. The host closes its own sensor library on the way out, which
            // releases the driver handle — a killed one leaves that to Windows, and the
            // installer then finds the driver in use.
            try
            {
                host.StandardInput.Write(SensorCommand.Stop());
                host.StandardInput.Flush();
                host.StandardInput.Close();
            }
            catch
            {
                // Asking the host to stop politely. It may already be gone, which is the
                // outcome being asked for; the kill below covers the case where it is not.
            }

            try
            {
                if (!host.WaitForExit(2000) && !host.HasExited) host.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited, or exited between the two checks. Either way it is gone, and
                // the job object guarantees it cannot outlive Pulse regardless.
            }

            try { host.Dispose(); } catch { }
        }
    }
}
