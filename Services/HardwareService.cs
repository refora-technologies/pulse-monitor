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
    public float? Fps { get; set; }
    public float? Fps1Low { get; set; }
    public float TotalRamGb { get; set; }
    public float TotalVramGb { get; set; }

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

    public float TotalRamGb { get; private set; } = 16f;
    public float TotalVramGb { get; private set; } = 6f;

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
        catch { }

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

                Pump($"Pulse sensor readings",     () => ReadSnapshots(host));
                Pump($"Pulse sensor diagnostics",  () => ReadDiagnostics(host));

                LogService.Info(nameof(HardwareService), $"Sensor host started (pid {host.Id}, reading {subsystems}).");
            }
            catch (Exception ex)
            {
                LogService.Error(nameof(HardwareService), "Starting the sensor host failed", ex);
                _host = null;
                Fail("Sensors unavailable. Pulse could not start the process that reads them.");
            }
        }
    }

    /// Dedicated threads rather than the thread pool. These block on a pipe for the lifetime
    /// of the host, which is exactly the thing pool threads must not do.
    private static void Pump(string name, Action work)
    {
        new Thread(() => { try { work(); } catch { } })
        {
            IsBackground = true,
            Name         = name,
        }.Start();
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
                    default:      LogService.Info("SensorHost", text); break;
                }
            }
        }
        catch { }
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

            _host = null;

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
    /// Ends the current host so a fresh one takes its place. Used when it has stopped
    /// answering: there is nothing to ask a wedged process, and its replacement starts clean.
    /// </summary>
    private void ReplaceHost(string why)
    {
        lock (_hostLock)
        {
            var host = _host;
            if (host == null) return;

            LogService.Warn(nameof(HardwareService), $"Replacing the sensor host: {why}");
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
            if (silent > GpuStaleAfter && !_gpuBlanked)
            {
                _gpuBlanked = true;
                Hold(alsoClearTheRest: false);
            }

            if (silent > EverythingStaleAfter && !_blanked)
            {
                _blanked = true;
                Hold(alsoClearTheRest: true);
            }

            // Alive but not answering. Rarer than a crash and more confusing, because nothing
            // has ended and nothing is logged; the readings simply stop.
            if (silent > SilentAfter)
            {
                _lastSnapshotAt = Environment.TickCount64;   // don't re-trigger while it dies
                ReplaceHost($"no readings for {silent.TotalSeconds:F0}s");
            }
        }
        catch { }
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
        catch { }

        // Totals persist. They are read once from a sensor that does not always report, and
        // zeroing them would make every percentage tile jump to nothing for a cycle.
        if (data.TotalRamGb  > 0) TotalRamGb  = data.TotalRamGb;
        if (data.TotalVramGb > 0) TotalVramGb = data.TotalVramGb;

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

            if (snapshot.Gpus.Count > 0 && !SameGpus(snapshot.Gpus))
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
        catch { }
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
            catch { }

            try
            {
                if (!host.WaitForExit(2000) && !host.HasExited) host.Kill(entireProcessTree: true);
            }
            catch { }

            try { host.Dispose(); } catch { }
        }
    }
}
