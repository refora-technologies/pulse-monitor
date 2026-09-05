using LibreHardwareMonitor.Hardware;

namespace Pulse.Services;

/// Sensor groups Pulse can ask for. Round-trips through its own ToString, which is how the
/// selection is carried to the sensor host.
[Flags]
public enum SensorSubsystems
{
    None = 0, Cpu = 1, Gpu = 2, Memory = 4, Storage = 8, Network = 16, Battery = 32,
}

/// <summary>
/// Everything that talks to LibreHardwareMonitor, and nothing else.
///
/// This runs in the sensor host process rather than in Pulse, which is the whole point of it.
/// A fault inside a vendor driver library cannot be caught: reading GPU power through a handle
/// belonging to a card that has just been disabled raises an access violation, and .NET
/// terminates the process without running a single handler. Keeping that code over here means
/// the cost is a restarted child and a second of blank tiles rather than Pulse disappearing.
///
/// Deliberately knows nothing about settings, timers, the dispatcher or frame rate. It is
/// given what to read and asked for a reading; everything else is the caller's business. That
/// also makes it the part that can be exercised on its own.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly Action<string, string>? _log;

    /// Adapters seen so far, keyed by identifier. Accumulated rather than rebuilt: see Publish.
    private readonly Dictionary<string, GpuInfo> _seenGpus = new();

    private SensorSubsystems _subsystems;
    private string? _pinnedGpuId;

    public bool    IsReady { get; private set; }
    public string? Fault   { get; private set; }

    /// <param name="log">Where to send diagnostics. In the host this goes to standard error,
    /// which Pulse folds into its own log — two processes appending to one file lose lines.</param>
    public SensorReader(Action<string, string>? log = null)
    {
        _log = log;
        _computer = new Computer();
    }

    private void Log(string level, string message)
    {
        try { _log?.Invoke(level, message); } catch { }   // the logger itself; nowhere to say so
    }

    public void SetSubsystems(SensorSubsystems subsystems)
    {
        if (subsystems == _subsystems) return;
        _subsystems = subsystems;
        Apply(_computer, subsystems);
    }

    public void SetSelectedGpu(string? id) => _pinnedGpuId = string.IsNullOrEmpty(id) ? null : id;

    private static void Apply(Computer computer, SensorSubsystems s)
    {
        computer.IsCpuEnabled         = s.HasFlag(SensorSubsystems.Cpu);
        computer.IsGpuEnabled         = s.HasFlag(SensorSubsystems.Gpu);
        computer.IsMemoryEnabled      = s.HasFlag(SensorSubsystems.Memory);
        computer.IsStorageEnabled     = s.HasFlag(SensorSubsystems.Storage);
        computer.IsNetworkEnabled     = s.HasFlag(SensorSubsystems.Network);
        computer.IsBatteryEnabled     = s.HasFlag(SensorSubsystems.Battery);
        computer.IsMotherboardEnabled = false;
    }

    /// <summary>
    /// Opens the sensor library, retrying a few times before giving up.
    ///
    /// The driver is installed by our own installer moments earlier and occasionally is not
    /// ready on the first attempt, particularly on the reboot straight after installation. A
    /// single silent attempt meant Pulse polled empty hardware forever, showing "--" on every
    /// tile with nothing to say why and no way back short of restarting it.
    /// </summary>
    public void Open()
    {
        const int attempts = 3;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                _computer.Open();

                IsReady = true;
                Fault   = null;
                Log("info", $"Sensors opened (attempt {attempt}).");
                return;
            }
            catch (Exception ex)
            {
                Log("error", $"Opening sensors failed (attempt {attempt} of {attempts}): {ex.GetType().Name}: {ex.Message}");

                if (attempt == attempts)
                {
                    Fault = "Sensors unavailable. The PawnIO driver may not be installed, "
                          + "or Pulse may not be running as administrator.";
                    return;
                }

                Thread.Sleep(2000 * attempt);
            }
        }
    }

    /// <summary>
    /// One further attempt at opening sensors that are not open, from scratch.
    /// </summary>
    /// <remarks>
    /// <see cref="Open"/> tries three times over about six seconds and then stops, which is the
    /// right shape for startup and the wrong shape for the rest of the session. The case those
    /// three attempts exist for is the reboot straight after installation, where the driver is
    /// still settling; if it settles seven seconds later instead of six, Pulse showed "--" on
    /// every tile until somebody restarted it, and nothing was ever going to try again.
    ///
    /// A fresh Computer rather than another go at the old one, for the same reason a rescan
    /// builds one: the vendor libraries keep state behind it, and that state is part of what
    /// failed.
    /// </remarks>
    public void Retry()
    {
        if (IsReady) return;

        _openRetries++;

        try { _computer.Close(); } catch { }   // it never opened; there may be nothing to close

        _computer = new Computer();
        Apply(_computer, _subsystems);

        try
        {
            _computer.Open();

            IsReady = true;
            Fault   = null;
            _openRetries = 0;

            Log("info", "Sensors opened on a later attempt; readings resume.");
        }
        catch (Exception ex)
        {
            // The first failure, then occasionally. This runs for as long as Pulse does, and a
            // machine with no working driver must not write a line every half minute forever.
            if (_openRetries == 1 || _openRetries % 10 == 0)
                Log("warn", $"Sensors still will not open (attempt {_openRetries}): "
                          + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private int _openRetries;

    /// <summary>
    /// How many polls in a row may fail before the reader declares itself unavailable.
    /// </summary>
    /// <remarks>
    /// A single failed poll is ordinary: one device throws, that reading is missing, the next
    /// poll is fine. What was not handled is the same thing failing every time, which produced
    /// a stream of incomplete snapshots all claiming to be ready. Pulse saw a healthy child
    /// answering promptly and had no reason to do anything, while the tiles showed nothing.
    ///
    /// Saying so puts it back on the path that already exists: not ready means the message
    /// reaches the user, and <see cref="Retry"/> rebuilds everything from scratch.
    /// </remarks>
    private const int MaxConsecutiveReadFailures = 5;

    private int _consecutiveReadFailures;

    /// <summary>
    /// Throws away the open sensor library and enumerates the machine again.
    ///
    /// Needed when the set of graphics adapters changes. LibreHardwareMonitor builds its
    /// device list once, at open, so a card that has been switched off is still polled through
    /// handles the driver no longer honours, and a card that has just appeared is not polled at
    /// all. Neither resolves itself.
    ///
    /// A fresh Computer rather than a reopen of the old one, because the vendor libraries keep
    /// their own state behind it and the point of doing this is to be rid of that state.
    /// </summary>
    public void Rescan()
    {
        Log("info", "Re-enumerating hardware.");

        try { _computer.Close(); } catch (Exception ex) { Log("warn", $"Closing sensors before a rescan failed: {ex.GetType().Name}"); }

        _computer = new Computer();
        Apply(_computer, _subsystems);

        IsReady = false;
        Fault   = null;

        // The picker list is rebuilt from here on. Adapters that are genuinely gone should
        // stop being offered, which is the other half of what a rescan is for.
        _seenGpus.Clear();

        // Capacities belong to the adapters that were there a moment ago.
        _dedicatedVram.Clear();

        // And so do the memory counters, whose keys are identifiers Windows reassigns.
        _gpuMemory.Refresh();

        Open();
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One reading of everything enabled. Frame rate is left empty: that is captured in Pulse
    /// itself, which owns the capture process, and is filled in when the snapshot arrives.
    /// </summary>
    public SensorSnapshot Read()
    {
        var snapshot = new SensorSnapshot { Ready = IsReady, Fault = Fault };
        var data = snapshot.Data;

        if (!IsReady) return snapshot;

        _vendorGpuLoad     = null;
        _adaptersThisPoll  = null;

        try
        {
            var gpus = new List<IHardware>();

            foreach (var hw in _computer.Hardware)
            {
                // Accept recurses: UpdateVisitor.VisitHardware updates this device and then
                // visits its children, so the children must not be Accept'ed again below or
                // every subhardware sensor is read twice per poll.
                hw.Accept(_visitor);
                ReadHardware(hw, data);
                if (IsGpu(hw.HardwareType)) gpus.Add(hw);

                foreach (var sub in hw.SubHardware)
                {
                    ReadHardware(sub, data);
                    if (IsGpu(sub.HardwareType)) gpus.Add(sub);
                }
            }

            // Before anything asks Windows about a card by name.
            NoteAmbiguousNames(gpus);

            Publish(gpus);

            // Said explicitly, so an empty list can be believed. Only true when graphics were
            // actually enumerated this poll; with every GPU tile switched off they are not,
            // and an empty list then says nothing about what is fitted.
            snapshot.GpusKnown = _subsystems.HasFlag(SensorSubsystems.Gpu);
            snapshot.Gpus = new List<GpuInfo>(_seenGpus.Values);
            snapshot.Gpus.Sort((a, b) => b.IsDiscrete.CompareTo(a.IsDiscrete));

            // Read GPU fields from a single chosen device so temp/power/clock/usage never get
            // mixed across an iGPU and a dGPU on the same poll.
            var chosen = SelectGpu(gpus);
            if (chosen != null)
            {
                snapshot.ActiveGpuName = chosen.Name;
                ReadGpu(chosen, data);

                ApplyWindowsTemperature(chosen.Name, IsDiscrete(chosen), data);
            }
        }
        catch (Exception ex)
        {
            // One misbehaving device aborts the whole enumeration, so unrelated tiles go blank
            // for that cycle. Still swallowed — a monitoring overlay must not fall over because
            // one sensor threw — but recorded, and only once per fault rather than every poll,
            // which at two-second intervals would bury the log in minutes.
            ReportFailure(ex);

            // Failing every time is a different thing from failing once, and it used to look
            // identical from outside: an incomplete reading that still called itself ready.
            if (++_consecutiveReadFailures >= MaxConsecutiveReadFailures)
            {
                _consecutiveReadFailures = 0;

                IsReady = false;
                Fault   = "Sensors stopped responding. Pulse is trying to open them again.";

                Log("error", $"{MaxConsecutiveReadFailures} readings in a row failed; "
                           + "treating sensors as unavailable and reopening them.");
            }

            snapshot.Ready = IsReady;
            snapshot.Fault = Fault;
            return snapshot;
        }

        _consecutiveReadFailures = 0;

        // Only a real total. Adding whichever of the two happened to be readable produced a
        // number labelled "CPU+GPU Power" that was silently just one of them — indisting-
        // uishable from a genuine total, and roughly half the true figure.
        //
        // Both have to be above zero, not merely present. That is not pedantry: an unreadable
        // processor power sensor reports 0.0 rather than nothing, which passes a test for
        // having a value and contributes nothing to the sum. Measured here on this machine with
        // the driver unavailable, CpuPower came back as 0.0 and the total was published as
        // 6.302 W, which was the graphics figure alone wearing a label that claims to be both.
        // Exactly the fault the paragraph above says was fixed, arriving through the one door
        // that had been left open. Nothing that is running draws no power.
        data.SysPower = data.CpuPower is > 0 and { } cpuW && data.GpuPower is > 0 and { } gpuW
            ? cpuW + gpuW
            : null;

        return snapshot;
    }

    private string? _lastFault;
    private int _faultCount;

    private void ReportFailure(Exception ex)
    {
        var signature = ex.GetType().Name + ": " + ex.Message;

        if (signature != _lastFault)
        {
            _lastFault  = signature;
            _faultCount = 1;
            Log("error", $"A sensor read failed; this poll is incomplete. {signature}");
            return;
        }

        if (++_faultCount % 100 == 0)
            Log("warn", $"Same sensor read has now failed {_faultCount} times: {signature}");
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>
    /// Adds newly seen adapters to the picker list.
    ///
    /// Accumulated between rescans rather than replaced each poll: LibreHardwareMonitor stops
    /// enumerating an integrated GPU entirely while a game has the discrete one active, so
    /// rebuilding from each poll would make the picker empty itself mid-session and reappear
    /// later. Rescan clears it, which is the one moment an adapter can genuinely have gone.
    /// </summary>
    private void Publish(List<IHardware> gpus)
    {
        foreach (var g in gpus)
        {
            var id = g.Identifier.ToString();
            if (_seenGpus.ContainsKey(id)) continue;

            _seenGpus[id] = new GpuInfo
            {
                Id         = id,
                Name       = g.Name,
                IsDiscrete = IsDiscrete(g),
            };
        }
    }

    /// <summary>
    /// Chooses which GPU every GPU tile reads from. An explicit user choice always wins.
    /// Otherwise we prefer the adapter with real dedicated video memory, because that is what
    /// actually distinguishes a discrete GPU from integrated graphics. Vendor alone is not a
    /// reliable signal: an AMD APU's Radeon iGPU reports as HardwareType.GpuAmd exactly like a
    /// discrete Radeon does, which previously caused Pulse to lock onto the integrated GPU on
    /// Ryzen laptops that also have an NVIDIA card.
    /// </summary>
    private IHardware? SelectGpu(List<IHardware> gpus)
    {
        if (gpus.Count == 0) return null;

        if (_pinnedGpuId is { Length: > 0 })
        {
            foreach (var g in gpus)
                if (g.Identifier.ToString() == _pinnedGpuId) return g;
            // Pinned GPU is gone (eGPU unplugged, driver change) — fall through to auto.
        }

        IHardware? best = null;
        float bestVram = -1f;
        int   bestVendor = -1;

        foreach (var g in gpus)
        {
            float vram   = GetDedicatedVramMb(g);
            int   vendor = VendorRank(g);

            if (vram > bestVram || (vram == bestVram && vendor > bestVendor))
            {
                best       = g;
                bestVram   = vram;
                bestVendor = vendor;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether an adapter is a real graphics card rather than graphics built into the
    /// processor. Decides the "Discrete"/"Integrated" label in the GPU picker, and which
    /// adapter is chosen when the user has not picked one.
    ///
    /// Four tests, in order of how much they can be trusted:
    ///
    ///   LibreHardwareMonitor marks some integrated adapters in the identifier itself. On this
    ///   machine the Intel UHD comes through as "/gpu-intel-integrated/...". When it says so,
    ///   that settles it. It only ever says so for Intel: the library has no equivalent for
    ///   AMD, whose integrated parts are plain "/gpu-amd/" like any Radeon card.
    ///
    ///   No integrated part has ever shipped under NVIDIA, so that settles it too. Worth
    ///   stating outright because an idle laptop card reports no memory figures at all on some
    ///   polls, and a memory test alone would then call an RTX card integrated.
    ///
    ///   Otherwise, how much dedicated video memory Windows says the adapter has. This is what
    ///   separates an AMD APU from a Radeon card, which nothing above can do: the APU's figure
    ///   is the small pool the BIOS reserved, while a card reports its actual memory. The
    ///   vendor sensor cannot be used for this, because it reports that reserved slice as
    ///   though it were a card's own memory, which is exactly how a Radeon 740M came to be
    ///   labelled discrete.
    ///
    ///   The boundary is a judgement rather than a fact, and it is worth being honest about
    ///   where it fails. Integrated graphics reserve tens to a few hundred megabytes: the Intel
    ///   here reports 128 MB and the reporter's 740M around 460 MB. Discrete cards still in
    ///   service carry gigabytes. A desktop APU configured with a large frame buffer in its
    ///   BIOS would be read as discrete, and a very old card with under a gigabyte would be
    ///   read as integrated. Both are rare, and both are better than the previous rule, which
    ///   was wrong for every AMD APU.
    ///
    ///   The vendor sensor remains the last resort for an adapter Windows cannot describe.
    /// </summary>
    private const float IntegratedVramCeilingMb = 1024f;

    private bool IsDiscrete(IHardware gpu)
    {
        if (gpu.Identifier.ToString().Contains("integrated", StringComparison.OrdinalIgnoreCase))
            return false;

        if (gpu.HardwareType == HardwareType.GpuNvidia) return true;

        // Windows' own answer, where it gives one. The display driver marks the integrated half
        // of a hybrid laptop, and that is a statement of fact rather than the inference from
        // memory size below.
        //
        // Consulted here so that one question has one answer. The picker decided integrated or
        // discrete from the tests in this method while the temperature and load code decided it
        // from this flag, and on a machine where the two disagreed the tile could be renamed
        // "GPU Die Temp" for an adapter the picker was calling a graphics card. Only trusted
        // when it says integrated: the flag is about hybrid systems, so a desktop with nothing
        // but an APU can leave it clear, and the memory test below is what catches that.
        if (FindAdapter(gpu.Name) is { Integrated: true }) return false;

        var windowsMb = DedicatedVideoMemoryMb(gpu.Name);
        if (windowsMb > 0) return windowsMb >= IntegratedVramCeilingMb;

        return GetDedicatedVramMb(gpu) > 0;
    }

    /// Dedicated video memory in MB, or 0 for an adapter that only carves out of system RAM.
    /// Integrated graphics report shared memory only ("D3D Shared Memory *"), while a discrete
    /// card reports "GPU Memory Total" and/or "D3D Dedicated Memory Used".
    private static float GetDedicatedVramMb(IHardware gpu)
    {
        float total = 0f, dedicated = 0f;

        foreach (var s in gpu.Sensors)
        {
            if (s.SensorType != SensorType.SmallData || s.Value is null) continue;

            // Exact match so "D3D Shared Memory Total" can never be mistaken for this.
            if (s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                total = s.Value.Value;
            else if (s.Name.Contains("Dedicated Memory", StringComparison.OrdinalIgnoreCase))
                dedicated = MathF.Max(dedicated, s.Value.Value);
        }

        return total > 0f ? total : dedicated;
    }

    /// Tiebreaker only, used when two adapters report the same dedicated memory (usually when
    /// neither reports any). There are no integrated NVIDIA parts in this context.
    private static int VendorRank(IHardware gpu)
    {
        if (gpu.Identifier.ToString().Contains("integrated", StringComparison.OrdinalIgnoreCase))
            return 0;

        return gpu.HardwareType switch
        {
            HardwareType.GpuNvidia => 3,
            HardwareType.GpuAmd    => 2,
            HardwareType.GpuIntel  => 1,
            _                      => 0,
        };
    }

    private void ReadHardware(IHardware hw, SensorData data)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:     ReadCpu(hw, data);     break;
            case HardwareType.Memory:  ReadMemory(hw, data);  break;
            case HardwareType.Network: ReadNetwork(hw, data); break;
            case HardwareType.Battery: ReadBattery(hw, data); break;
            case HardwareType.Storage: ReadStorage(hw, data); break;
        }
    }

    private void ReadCpu(IHardware hw, SensorData data)
    {
        float clockSum = 0; int clockCount = 0;
        float usageSum = 0; int usageCount = 0;

        // Priority-based temp/power tracking for Intel + AMD compatibility
        // Intel: "CPU Package" (temp), "CPU Package" (power)
        // AMD:   "Core (Tctl/Tdie)" or "Tdie" (temp), "Package" or "PPT" (power)
        float? tempPackage = null, tempTctl = null, tempFallback = null;
        float? powerPackage = null, powerPpt = null, powerFallback = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    // The graphics block of the die, skipped on purpose. It is not the cores,
                    // so it must not become the processor's reading, and the graphics tile
                    // does not want it either: Task Manager shows the die temperature on its
                    // GPU page, and this sensor sits ten to seventeen degrees below that, so
                    // showing it guarantees Pulse disagrees with the thing people check.
                    if (s.Name.Equals("GFX", StringComparison.OrdinalIgnoreCase)
                        || s.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        tempPackage = s.Value;
                    else if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                          || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                        tempTctl = s.Value;
                    else if (tempFallback is null && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        tempFallback = s.Value;
                    break;

                case SensorType.Power:
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        powerPackage = s.Value;
                    else if (s.Name.Contains("PPT", StringComparison.OrdinalIgnoreCase))
                        powerPpt = s.Value;
                    else if (powerFallback is null)
                        powerFallback = s.Value;
                    break;

                // Core clocks only, and only the real ones.
                //
                // This used to average every clock the processor published except the bus,
                // which on an Intel chip happens to be nothing but core clocks and was right
                // by luck. An AMD processor publishes far more: alongside four core clocks it
                // reports an "effective" figure per core, two aggregates, and separate clocks
                // for the fabric, the memory controller, the uncore and the integrated
                // graphics. Averaging all fourteen gave 1,110 MHz where the cores were running
                // at 1,895, and Task Manager said 2.13 GHz.
                //
                // Excluded by name: the bus, the "effective" figures, which are averages over
                // idle time and pull the number down, the aggregates, which would double count,
                // and the uncore, which contains the word "core" without being one.
                case SensorType.Clock when IsCoreClock(s.Name):
                    clockSum += s.Value.Value; clockCount++;
                    break;

                case SensorType.Load when s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                    data.CpuUsage = s.Value;
                    break;

                case SensorType.Load:
                    usageSum += s.Value.Value; usageCount++;
                    break;
            }
        }

        // Apply priority: Package > Tctl/Tdie > any Core sensor
        data.CpuTemp  = tempPackage  ?? tempTctl  ?? tempFallback;
        data.CpuPower = powerPackage ?? powerPpt  ?? powerFallback;

        if (clockCount > 0 && data.CpuClock is null)
            data.CpuClock = MathF.Round(clockSum / clockCount / 1000f, 2);

        if (data.CpuUsage is null && usageCount > 0)
            data.CpuUsage = usageSum / usageCount;
    }

    private void ReadGpu(IHardware hw, SensorData data)
    {
        // GPU Usage comes from the vendor's own "GPU Core" load in preference to the D3D 3D
        // engine counter, and this is a deliberate reversal of what it used to be.
        //
        // The two measure different things and both are honest. The engine counter is the
        // fraction of wall time the 3D engine had work executing, which is what the Windows
        // scheduler reports and what Task Manager draws. The vendor figure is the share of
        // sample periods in which any kernel was running, which ignores how idle the card was
        // inside those periods.
        //
        // Measured on an RTX 3050 over thirty seconds in a game, all three at once:
        //
        //     D3D 3D       51.4 average, 54.3 peak      Task Manager reads 50.8
        //     GPU Core     88.4 average, 93.0 peak      Afterburner and the NVIDIA overlay
        //
        // So the engine counter agrees with Task Manager almost exactly, and the vendor figure
        // is what every overlay a gamer already runs is showing them. Pulse used to choose
        // Task Manager. Two people reported the same thing within a week: that Pulse read low
        // beside Afterburner and the NVIDIA overlay in a game. Nobody has ever reported that it
        // disagreed with Task Manager.
        //
        // Discrete cards only, and that limit was learned the hard way. A Vega 8 reports its
        // "GPU Core" load as a flat 100.00 whatever the machine is doing: measured at 100 while
        // the engine counter read 8.02 and Task Manager read 8%. Intel graphics report no
        // vendor load at all. So on graphics built into a processor the vendor figure is not a
        // better number, it is not a number, and the engine counter is the only honest one.
        //
        // GPU Usage therefore means the vendor's figure on a discrete card and the engine
        // figure on integrated graphics. That is a real inconsistency, accepted because the
        // alternative is either disagreeing with the tools people hold Pulse against, or
        // reporting a constant 100% on every AMD APU.
        float? d3dEngineLoad = null;
        float? coreLoad      = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            switch (s.SensorType)
            {
                case SensorType.Load when s.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase)
                                       && d3dEngineLoad is null:
                    d3dEngineLoad = s.Value;
                    break;
                case SensorType.Temperature when data.GpuTemp is null:
                    data.GpuTemp = s.Value;
                    break;
                case SensorType.Power when data.GpuPower is null:
                    data.GpuPower = s.Value;
                    break;
                case SensorType.Clock when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && data.GpuClock is null:
                    data.GpuClock = s.Value;
                    break;
                // Dedicated memory only, and only the first match.
                //
                // A plain "Memory Used" test also catches "D3D Shared Memory Used" and
                // "GPU Memory Used", so whichever the driver happened to enumerate last won.
                // On a hybrid laptop that meant VRAM flipping between the card's own memory
                // and system memory borrowed for sharing, with nothing to indicate which was
                // on screen.
                case SensorType.SmallData when data.GpuVram is null
                                            && IsDedicatedMemorySensor(s.Name, "Used"):
                    data.GpuVram = MathF.Round(s.Value.Value / 1024f, 2);
                    break;
                // Kept only as a fallback for an adapter Windows cannot tell us about; the
                // preferred source is applied after this loop. Not rounded to whole gigabytes
                // any more either, because a rounded capacity turns anything under 512 MB into
                // a total of zero.
                case SensorType.SmallData when data.TotalVramGb == 0
                                            && IsDedicatedMemorySensor(s.Name, "Total"):
                    data.TotalVramGb = s.Value.Value / 1024f;
                    break;
                case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && coreLoad is null:
                    coreLoad = s.Value;
                    break;
            }
        }

        data.GpuUsage   = d3dEngineLoad ?? coreLoad;
        _vendorGpuLoad  = coreLoad;

        ApplyVideoMemory(hw, data);
    }

    /// <summary>
    /// How much dedicated memory an adapter must have before that is the pool worth showing.
    /// </summary>
    /// <remarks>
    /// Its only job is to tell a framebuffer stub from a real pool. An Intel UHD reports 128 MB
    /// and uses none of it, with everything it touches coming from the shared pool; an AMD
    /// Vega 8 reports 2,033 MB and genuinely uses it, and Task Manager shows that as its
    /// Dedicated GPU memory. Both were measured. The boundary sits well above the first and far
    /// below the second, and low enough that an APU configured with a 512 MB slice in its BIOS
    /// still reads as having a real pool.
    ///
    /// Deliberately not the same question as discrete versus integrated, and deliberately not
    /// the same constant: the Vega 8 is integrated and has two gigabytes.
    /// </remarks>
    private const float RealDedicatedPoolMb = 256f;

    /// <summary>
    /// Fills in the video memory reading, its capacity, and which pool it came from.
    /// </summary>
    /// <remarks>
    /// Windows first, the sensor library second, nothing third.
    ///
    /// Windows publishes both pools for every adapter under "GPU Adapter Memory", which is the
    /// source Task Manager reads, so agreeing with it is the point rather than a coincidence.
    /// The library is kept as a fallback because these counters are absent on old Windows and
    /// on machines whose counter registry has been damaged.
    ///
    /// Which pool is shown follows what the adapter actually has. A card with real dedicated
    /// memory shows that; graphics with none shows the shared pool, which is where its memory
    /// genuinely is, and the tile is renamed so nobody has to guess which they are looking at.
    /// </remarks>
    private void ApplyVideoMemory(IHardware hw, SensorData data)
    {
        var adapter = FindAdapter(hw.Name);

        float dedicatedTotalMb = adapter?.DedicatedVideoMemoryMb ?? 0f;
        float sharedTotalMb    = adapter?.SharedSystemMemoryMb   ?? 0f;

        // Nothing is known about this adapter, so nothing can be claimed about which pool a
        // reading came from. The sensor library's own figure stands, under its ordinary name.
        if (adapter is null) return;

        bool useShared = dedicatedTotalMb < RealDedicatedPoolMb;
        var usage = _gpuMemory.Read(adapter.Value.CounterKey);

        // Usage, capacity and the name of the pool are one measurement and are decided
        // together.
        //
        // They were not. If the Windows counters could not be read, the amount in use was left
        // as the sensor library had it, which is dedicated memory; the capacity was still
        // replaced with the shared pool's; and the tile was still renamed "Shared VRAM". The
        // result read as a coherent measurement and was three quarters of one: dedicated usage,
        // shared capacity, and a name belonging to neither.
        if (usage is not { } used)
        {
            // The capacity still applies when the adapter really has dedicated memory, since
            // that is what the library's figure is measuring.
            if (!useShared && dedicatedTotalMb > 0) data.TotalVramGb = dedicatedTotalMb / 1024f;
            return;
        }

        float bytes = useShared ? used.SharedBytes : used.DedicatedBytes;
        if (bytes >= 0) data.GpuVram = MathF.Round(bytes / (1024f * 1024f * 1024f), 2);

        // The capacity of whichever pool is being shown. Zero still means "not known" and is
        // never drawn, so an adapter Windows cannot describe leaves the tile without a total
        // rather than with an invented one.
        float totalMb = useShared ? sharedTotalMb : dedicatedTotalMb;
        if (totalMb > 0) data.TotalVramGb = totalMb / 1024f;

        // Said out loud so the tile can be named honestly. Only claimed when there is a
        // reading to label; otherwise the tile keeps its ordinary name and shows nothing.
        data.VramIsShared = useShared && data.GpuVram is not null;
    }

    /// <summary>
    /// Fills in the temperature of graphics that publish none of their own, from Windows.
    /// </summary>
    /// <remarks>
    /// Graphics built into a processor commonly have no temperature sensor the sensor library
    /// can read. On the AMD laptop this was written against, the adapter publishes clocks,
    /// loads, memory and voltage and not one temperature.
    ///
    /// Windows still has an answer for some of them, because the display driver reports it,
    /// and that report is what Task Manager's GPU page is drawn from. Asking the same source
    /// means Pulse agrees with Task Manager by construction rather than by resemblance: where
    /// Task Manager shows 71 for a Vega 8, Windows holds 71; where Task Manager shows N/A for
    /// Intel graphics, Windows holds nothing and Pulse shows nothing too.
    ///
    /// That last part is the rule. A driver with no temperature to give is not a gap to be
    /// filled from somewhere else in the machine. The earlier attempt did fill it, with the
    /// processor die, and it was right on the AMD laptop and wrong on the Intel one, where it
    /// put a number on screen that Task Manager does not show at all.
    ///
    /// The adapter's own sensor is still preferred when it has one. A discrete card reads its
    /// own die directly, that is the figure its vendor's own tools show, and it needs no
    /// second opinion.
    /// </remarks>
    /// <param name="discrete">Whether this is a graphics card rather than part of the
    /// processor, decided once by <see cref="IsDiscrete"/> and passed in.
    ///
    /// Passed rather than read from the adapter's own flag, so that the picker's answer and
    /// this one cannot differ. They could: the picker weighs four tests and this used the
    /// hybrid flag alone, so a machine the two disagreed about would list an adapter as a
    /// graphics card and then rename its temperature tile to say it was part of the
    /// processor.</param>
    private void ApplyWindowsTemperature(string adapterName, bool discrete, SensorData data)
    {
        if (FindAdapter(adapterName) is not { } adapter) return;

        // The vendor's own load, but only from a card that has one worth reading. See the
        // reasoning above ReadGpu: on integrated graphics this figure is either absent or a
        // constant 100, so there the engine counter already in place is the right answer.
        if (discrete && _vendorGpuLoad is { } vendor) data.GpuUsage = vendor;

        data.GpuTemp ??= DisplayAdapters.Temperature(adapter.Luid);

        // Said out loud so the tile can be named honestly, and only while there is something
        // to name. On graphics that are part of the processor this reading is the die's, which
        // is why it matches CPU Temp exactly: one piece of silicon, measured once.
        data.GpuTempIsDie = !discrete && data.GpuTemp is not null;
    }

    /// <summary>
    /// The vendor's own load figure for the chosen graphics device, kept aside until the
    /// adapter is known, because whether it can be trusted depends on what kind of adapter
    /// it is. Reset every poll: a figure held over from a previous cycle is a stale reading.
    /// </summary>
    private float? _vendorGpuLoad;

    /// The adapter Windows knows by this name, or null. Preferring one that has performance
    /// counters, because a machine can list the same adapter twice and only one of the two
    /// carries them.
    /// <summary>
    /// Adapter names this machine has more than one of, where a name no longer identifies a
    /// card. Rebuilt every poll from the devices the sensor library reports.
    /// </summary>
    /// <remarks>
    /// Two identical cards carry identical descriptions, and matching by description then hands
    /// both of them whatever Windows says about the first. Someone with a pair of the same card
    /// would pick the second in the picker and read the first one's memory and temperature,
    /// with nothing to suggest the numbers were not its own.
    ///
    /// Windows offers no key that can be tied back to a sensor library device, so the honest
    /// answer is to stop claiming. Where the name is ambiguous, the readings that come from
    /// Windows are left out and the vendor's own sensors stand alone; those are read through a
    /// handle to a specific card and cannot be confused. Fewer readings, and the ones shown
    /// belong to the card named above them.
    ///
    /// The same description appearing twice in Windows' own list is a different thing and not
    /// ambiguous: one physical card is routinely listed twice, which is why the search below
    /// prefers the entry that carries performance counters.
    /// </remarks>
    private readonly HashSet<string> _ambiguousGpuNames = new(StringComparer.OrdinalIgnoreCase);

    private void NoteAmbiguousNames(List<IHardware> gpus)
    {
        _ambiguousGpuNames.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gpu in gpus)
            if (!seen.Add(gpu.Name))
                _ambiguousGpuNames.Add(gpu.Name);
    }

    /// <summary>
    /// Windows' adapter list for the poll in progress, asked for once rather than per lookup.
    /// </summary>
    /// <remarks>
    /// Enumerating DXGI costs about a millisecond and this is now consulted from three places
    /// per reading rather than two. Cleared at the top of every poll, so an adapter appearing
    /// or disappearing is still noticed on the next one.
    /// </remarks>
    private List<DisplayAdapters.Adapter>? _adaptersThisPoll;

    private DisplayAdapters.Adapter? FindAdapter(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return null;
        if (_ambiguousGpuNames.Contains(adapterName)) return null;

        DisplayAdapters.Adapter? first = null;

        foreach (var adapter in _adaptersThisPoll ??= DisplayAdapters.All())
        {
            if (!string.Equals(adapter.Description.Trim(), adapterName.Trim(),
                               StringComparison.OrdinalIgnoreCase)) continue;

            first ??= adapter;
            if (_gpuMemory.Read(adapter.CounterKey) != null) return adapter;
        }

        return first;
    }

    private readonly GpuMemoryCounters _gpuMemory = new();

    /// Cached per adapter name. Asking DXGI costs about a millisecond, which is not much until
    /// it happens on every poll. Cleared by Rescan, which is the only time the answer changes.
    private readonly Dictionary<string, float> _dedicatedVram = new(StringComparer.OrdinalIgnoreCase);

    private float DedicatedVideoMemoryMb(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return 0;

        if (_dedicatedVram.TryGetValue(adapterName, out var cached)) return cached;

        float mb;
        try   { mb = DisplayAdapters.DedicatedVideoMemoryMb(adapterName); }
        catch { mb = 0; }

        _dedicatedVram[adapterName] = mb;
        return mb;
    }

    /// <summary>
    /// Whether a sensor name refers to the adapter's own video memory rather than system
    /// memory it has been lent. Vendors name these differently — "GPU Memory Used",
    /// "D3D Dedicated Memory Used" — but the shared ones consistently say so.
    /// </summary>
    private static bool IsDedicatedMemorySensor(string name, string suffix)
    {
        if (!name.Contains("Memory", StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.Contains(suffix, StringComparison.OrdinalIgnoreCase))   return false;

        return !name.Contains("Shared", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("System", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a clock sensor is one of the processor's cores.
    /// </summary>
    /// <remarks>
    /// Measured names, not guessed. Intel publishes "Bus Speed", "P-Core #n" and "E-Core #n".
    /// AMD publishes "Core #n", "Core #n (Effective)", "Cores (Average)", "Cores (Average
    /// Effective)", "Fabric", "GFX", "Memory" and "Uncore" as well as the bus.
    ///
    /// "Uncore" has to be excluded explicitly because it contains the word.
    /// </remarks>
    private static bool IsCoreClock(string name) =>
        name.Contains("Core", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Uncore",    StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Effective", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Average",   StringComparison.OrdinalIgnoreCase);

    private static void ReadMemory(IHardware hw, SensorData data)
    {
        float? used = null, available = null;
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.SensorType != SensorType.Data) continue;
            var name = s.Name;
            if (name.Contains("Used", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                used = s.Value.Value;
            else if (name.Contains("Available", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                available = s.Value.Value;
        }
        if (used.HasValue)
            data.RamUsed = MathF.Round(used.Value, 2);
        if (used.HasValue && available.HasValue)
            data.TotalRamGb = MathF.Round(used.Value + available.Value, 0);
    }

    /// <summary>
    /// Adds an adapter's throughput to the totals, skipping anything that isn't a real
    /// network card.
    ///
    /// Every adapter used to be summed together, so a VPN, a Hyper-V switch or a virtual
    /// display's network stack counted the same packets a second time — the tiles could
    /// report two or three times the traffic actually crossing the wire.
    /// </summary>
    private static void ReadNetwork(IHardware hw, SensorData data)
    {
        if (!IsPhysicalAdapter(hw.Name)) return;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.SensorType != SensorType.Throughput) continue;
            if (s.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase))
                data.NetUpload = (data.NetUpload ?? 0) + s.Value.Value / 1_048_576f;
            else if (s.Name.Contains("Download", StringComparison.OrdinalIgnoreCase))
                data.NetDownload = (data.NetDownload ?? 0) + s.Value.Value / 1_048_576f;
        }
    }

    /// <summary>
    /// The charge left in the battery, as a percentage.
    /// </summary>
    /// <remarks>
    /// One sensor of the eight the library offers for a battery. The others are capacity in
    /// watt-hours, charge and discharge rates, voltage, wear, and an estimated time remaining.
    /// Only the percentage is shown, because it is the one people asked for and the one that
    /// needs no explaining. The time estimate in particular was left out on purpose: it swings
    /// wildly with load, and a tile that reads two hours and then forty minutes a moment later
    /// is the kind of number this project keeps having to remove.
    ///
    /// A machine with no battery reports no battery device at all, so the tile shows nothing
    /// rather than zero. Zero percent is a reading a desktop must never appear to have.
    /// </remarks>
    private static void ReadBattery(IHardware hw, SensorData data)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not { } value) continue;

            if (s.SensorType == SensorType.Level
                && s.Name.Contains("Charge", StringComparison.OrdinalIgnoreCase)
                && data.BatteryLevel is null)
            {
                data.BatteryLevel = value;
            }
        }
    }

    /// Substrings that mark an adapter as something other than a physical network card.
    private static readonly string[] VirtualAdapterMarkers =
    {
        "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "loopback",
        "wireguard", "openvpn", "tailscale", "zerotier", "parsec", "npcap",
        "pseudo", "wan miniport", "bluetooth", "tap-windows",
    };

    private static readonly object PhysicalAdapterLock = new();
    private static HashSet<string>? _physicalAdapters;
    private static long _physicalAdaptersFetchedAt;

    /// <summary>
    /// Whether this adapter should count toward network throughput. The list is rebuilt
    /// periodically rather than per poll, since enumerating interfaces is not free and
    /// adapters rarely appear or disappear.
    /// </summary>
    private static bool IsPhysicalAdapter(string name)
    {
        lock (PhysicalAdapterLock)
        {
            long now = Environment.TickCount64;
            if (_physicalAdapters is null || now - _physicalAdaptersFetchedAt > 30_000)
            {
                var (set, enumerated) = BuildPhysicalAdapterSet();

                if (enumerated || _physicalAdapters is null)
                {
                    _physicalAdapters          = set;
                    _physicalAdaptersFetchedAt = now;
                }
                else
                {
                    // The enumeration failed and a good answer is already held, so that one
                    // stands and this is tried again in a few seconds rather than in thirty.
                    //
                    // Replacing it would have meant an empty set, and an empty set is read
                    // below as "count everything" — which puts back the six times overcount
                    // this filter exists to prevent, on a machine where nothing has changed
                    // except that one call to Windows did not answer.
                    _physicalAdaptersFetchedAt = now - 25_000;
                }
            }

            // If nothing survived the filter, something about this machine's naming defeats
            // it — count everything rather than reporting a flat zero.
            return _physicalAdapters.Count == 0 || _physicalAdapters.Contains(name);
        }
    }

    private static (HashSet<string> Set, bool Enumerated) BuildPhysicalAdapterSet()
    {
        // One physical address, one entry. Windows exposes every NDIS filter bound to an
        // adapter as an adapter in its own right, reporting the same bytes over the same
        // wire, and they all carry the physical address of the adapter they sit on. Counting
        // them separately is what made the tiles read six times the real traffic on the
        // machine this was found on: one Wi-Fi card, and six things reporting its bytes.
        //
        // The real adapter is the one with the shortest name, because a binding is named
        // after the adapter it attaches to and then extended.
        var chosenByAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Adapters that report no address at all are kept as they are. Deduplicating them
        // against each other would mean treating "unknown" as a shared identity.
        var withoutAddress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                             or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    continue;

                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (LooksVirtual(nic.Name) || LooksVirtual(nic.Description)) continue;

                string address = nic.GetPhysicalAddress().ToString();

                if (string.IsNullOrEmpty(address))
                {
                    withoutAddress.Add(nic.Name);
                    continue;
                }

                if (!chosenByAddress.TryGetValue(address, out var chosen)
                    || nic.Name.Length < chosen.Length)
                {
                    chosenByAddress[address] = nic.Name;
                }
            }
        }
        catch
        {
            // Silent, because there is nothing to report it through: this is static, and in
            // the sensor host, whose only channel out is the instance logger. The caller is
            // told the enumeration did not finish, which is the part that matters. It used to
            // be told nothing, so a failure looked like a machine with no physical adapters,
            // and the caller reads that as "count every adapter" — putting back the six times
            // overcount this filter exists to prevent.
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
        }

        var physical = new HashSet<string>(chosenByAddress.Values, StringComparer.OrdinalIgnoreCase);
        physical.UnionWith(withoutAddress);
        return (physical, true);

        static bool LooksVirtual(string text) =>
            VirtualAdapterMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadStorage(IHardware hw, SensorData data)
    {
        float? activity = null;
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.SensorType != SensorType.Load) continue;
            if (s.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
            {
                activity = s.Value.Value;
                break;
            }
            activity ??= s.Value.Value;
        }
        if (activity.HasValue && activity > (data.DiskActivity ?? -1f))
            data.DiskActivity = activity;
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }

        // Performance counters hold handles into the counter provider, so they are released
        // rather than left to a finaliser.
        try { _gpuMemory.Dispose(); } catch { }   // shutting down; nothing follows to inform
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var s in hardware.SubHardware) s.Accept(this); }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
