using LibreHardwareMonitor.Hardware;

namespace Pulse.Services;

/// Sensor groups Pulse can ask for. Round-trips through its own ToString, which is how the
/// selection is carried to the sensor host.
[Flags]
public enum SensorSubsystems
{
    None = 0, Cpu = 1, Gpu = 2, Memory = 4, Storage = 8, Network = 16,
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
        try { _log?.Invoke(level, message); } catch { }
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

            Publish(gpus);
            snapshot.Gpus = new List<GpuInfo>(_seenGpus.Values);
            snapshot.Gpus.Sort((a, b) => b.IsDiscrete.CompareTo(a.IsDiscrete));

            // Read GPU fields from a single chosen device so temp/power/clock/usage never get
            // mixed across an iGPU and a dGPU on the same poll.
            var chosen = SelectGpu(gpus);
            if (chosen != null)
            {
                snapshot.ActiveGpuName = chosen.Name;
                ReadGpu(chosen, data);
            }
        }
        catch (Exception ex)
        {
            // One misbehaving device aborts the whole enumeration, so unrelated tiles go blank
            // for that cycle. Still swallowed — a monitoring overlay must not fall over because
            // one sensor threw — but recorded, and only once per fault rather than every poll,
            // which at two-second intervals would bury the log in minutes.
            ReportFailure(ex);
        }

        // Only a real total. Adding whichever of the two happened to be readable produced a
        // number labelled "CPU+GPU Power" that was silently just one of them — indisting-
        // uishable from a genuine total, and roughly half the true figure.
        data.SysPower = data.CpuPower.HasValue && data.GpuPower.HasValue
            ? data.CpuPower.Value + data.GpuPower.Value
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
    /// Two cheap certainties first, then the memory test that was here before:
    ///
    ///   LibreHardwareMonitor marks some integrated adapters in the identifier itself. On this
    ///   machine the Intel UHD comes through as "/gpu-intel-integrated/...". When it says so,
    ///   that settles it.
    ///
    ///   No integrated part has ever shipped under NVIDIA, so that settles it too. Worth
    ///   stating outright because an idle laptop card reports no memory figures at all on some
    ///   polls, and the memory test alone would then call an RTX card integrated.
    ///
    ///   Otherwise, dedicated video memory, as before. This is the case that is still not
    ///   airtight: an AMD APU reports "GPU Memory Total" for the slice of system RAM the BIOS
    ///   reserved for it, which looks identical to a card's own memory, so a Ryzen laptop may
    ///   still list its integrated graphics as discrete. Left alone deliberately — the obvious
    ///   alternative is Windows' D3D counters, but the only dedicated one LibreHardwareMonitor
    ///   exposes is "D3D Dedicated Memory Used", which reads zero on any idle card and would
    ///   trade a label that is wrong on AMD APUs for one that is wrong on every sleeping GPU.
    ///   There is no AMD hardware here to test a better rule against.
    /// </summary>
    private static bool IsDiscrete(IHardware gpu)
    {
        if (gpu.Identifier.ToString().Contains("integrated", StringComparison.OrdinalIgnoreCase))
            return false;

        if (gpu.HardwareType == HardwareType.GpuNvidia) return true;

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

    private static void ReadHardware(IHardware hw, SensorData data)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:     ReadCpu(hw, data);     break;
            case HardwareType.Memory:  ReadMemory(hw, data);  break;
            case HardwareType.Network: ReadNetwork(hw, data); break;
            case HardwareType.Storage: ReadStorage(hw, data); break;
        }
    }

    private static void ReadCpu(IHardware hw, SensorData data)
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

                case SensorType.Clock when !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase):
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

    private static void ReadGpu(IHardware hw, SensorData data)
    {
        // GPU Usage comes from the D3D 3D engine counter in preference to the vendor's own
        // "GPU Core" load.
        //
        // Measured on an RTX 3050 under sustained load: D3D 3D averaged 80.9% while GPU Core
        // averaged 99.3%. Task Manager read 79% and NVIDIA's overlay 82% — both agree with
        // the engine counter, so reporting the vendor figure made Pulse look wrong against
        // every other tool a user can check it against.
        //
        // It also makes the number mean the same thing on every vendor. Intel iGPUs expose
        // no Core load at all, so they were already being read this way, and GPU Usage
        // silently changed meaning depending on which GPU was selected.
        //
        // Core load is kept as the fallback for any adapter that reports no engine counters.
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
                case SensorType.SmallData when data.TotalVramGb == 0
                                            && IsDedicatedMemorySensor(s.Name, "Total"):
                    data.TotalVramGb = MathF.Round(s.Value.Value / 1024f, 0);
                    break;
                case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && coreLoad is null:
                    coreLoad = s.Value;
                    break;
            }
        }

        data.GpuUsage = d3dEngineLoad ?? coreLoad;
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
                _physicalAdapters          = BuildPhysicalAdapterSet();
                _physicalAdaptersFetchedAt = now;
            }

            // If nothing survived the filter, something about this machine's naming defeats
            // it — count everything rather than reporting a flat zero.
            return _physicalAdapters.Count == 0 || _physicalAdapters.Contains(name);
        }
    }

    private static HashSet<string> BuildPhysicalAdapterSet()
    {
        var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                             or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    continue;

                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (LooksVirtual(nic.Name) || LooksVirtual(nic.Description)) continue;

                physical.Add(nic.Name);
            }
        }
        catch { }

        return physical;

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
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var s in hardware.SubHardware) s.Accept(this); }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
