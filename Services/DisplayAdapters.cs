using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Notices when the machine's set of graphics adapters changes.
///
/// This is what makes a switched-off GPU recoverable. LibreHardwareMonitor builds its device
/// list once, when it opens, and never revisits it — so a card disabled in Device Manager goes
/// on being polled through handles the driver no longer honours, which is how a user's GPU
/// readings froze at whatever they happened to be at the moment the card went away. Nothing in
/// the sensor library notices, and nothing about the readings themselves says they are dead
/// rather than merely steady.
///
/// DXGI is asked rather than EnumDisplayDevices, which was the obvious alternative. On the
/// laptop this was developed against, EnumDisplayDevices reports twenty-four entries — four
/// per adapter plus sixteen from a virtual display driver — under names like \\.\DISPLAY29
/// that are renumbered by Windows when adapters come and go. DXGI reports the four real
/// adapters, each with a locally unique identifier that survives being renamed. Comparing
/// those identifiers therefore reacts to a card appearing or disappearing, and to nothing else.
/// </summary>
internal static class DisplayAdapters
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // ── What Windows itself holds about each adapter ────────────────────────────────────
    //
    // DXGI describes an adapter's memory and nothing else. The display kernel holds the rest,
    // including the temperature Task Manager puts on its GPU page and whether the adapter is
    // built into the processor, and it answers for every vendor without any of them being
    // named here. That is the whole point: each time this project has decided something about
    // graphics from a name or a memory size it has had to be undone later.
    //
    // Temperature especially. Graphics with no sensor of their own report nothing here, which
    // is exactly why Task Manager shows N/A for them, so asking Windows and showing nothing
    // when Windows has nothing gives the same answer Task Manager gives, on any machine,
    // without Pulse ever having to invent one.

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuid { public AdapterLuid Luid; public uint Handle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint   Handle;
        public int    Type;
        public IntPtr Data;
        public uint   DataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint Handle; }

    /// KMTQAITYPE_ADAPTERTYPE.
    private const int AdapterTypeQuery = 15;

    /// KMTQAITYPE_ADAPTERPERFDATA and KMTQAITYPE_ADAPTERPERFDATA_CAPS.
    private const int PerfDataQuery     = 62;
    private const int PerfDataCapsQuery = 63;

    /// <summary>
    /// What the display driver reports about the adapter, and what Task Manager's GPU page is
    /// drawn from.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfData
    {
        public uint  PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOc;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint  FanRpm;
        public uint  Power;
        public uint  Temperature;
        public byte  PowerDrawP1;
    }

    /// The limits for the above. TemperatureMax is what says which scale Temperature is on.
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfDataCaps
    {
        public uint  PhysicalAdapterIndex;
        public ulong MaxMemoryBandwidth;
        public ulong MaxPcieBandwidth;
        public uint  MaxFanRpm;
        public uint  TemperatureMax;
        public uint  TemperatureWarning;
    }

    /// Bit 5 of D3DKMT_ADAPTERTYPE: the adapter is the one built into the processor.
    private const uint HybridIntegrated = 1u << 5;

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid a);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo a);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter a);

    /// <summary>
    /// The temperature Windows holds for this adapter, in degrees Celsius, or null when it
    /// holds none.
    /// </summary>
    /// <remarks>
    /// This is the figure on Task Manager's GPU page, taken from the same place Task Manager
    /// takes it, which is why null here means Task Manager shows nothing either. Measured on
    /// this machine: the GeForce reports 475 with a stated maximum of 1050, and Task Manager
    /// shows 47; the Intel graphics report zero with a maximum of zero, and Task Manager shows
    /// N/A. So a driver that has no temperature to give says so by giving nothing, and Pulse
    /// leaves the tile empty rather than substituting something from elsewhere in the machine.
    ///
    /// The scale is read rather than assumed. The values above are tenths of a degree, which
    /// is what the field is documented to be, but a driver reporting whole degrees would put
    /// 47 where 470 was expected and the tile would read 4.7. TemperatureMax says which it is:
    /// a maximum has to land somewhere near a hundred degrees, so whichever reading of it does
    /// is the reading applied to the temperature as well. A driver that leaves the maximum
    /// unset is judged the same way on the temperature alone.
    /// </remarks>
    public static float? Temperature(long luid)
    {
        if (!Open(luid, out uint handle)) return null;

        try
        {
            if (!Query<AdapterPerfData>(handle, PerfDataQuery, out var perf)) return null;
            if (perf.Temperature == 0) return null;

            uint max = Query<AdapterPerfDataCaps>(handle, PerfDataCapsQuery, out var caps)
                     ? caps.TemperatureMax : 0;

            // Tenths unless something says otherwise. "Otherwise" is a stated maximum that only
            // makes sense read as whole degrees, or, when there is no maximum, a temperature
            // too small to be tenths of anything a running adapter reaches.
            bool tenths = max > 0 ? max >= 200 : perf.Temperature >= 200;
            float celsius = tenths ? perf.Temperature / 10f : perf.Temperature;

            // Nothing outside this is a temperature, whichever way it was read.
            return celsius is > 0f and <= 150f ? celsius : null;
        }
        finally { Close(handle); }
    }

    /// <summary>
    /// Whether Windows considers this adapter to be part of the processor.
    ///
    /// False whenever the question cannot be answered, so a failure here can only ever leave a
    /// reading absent, never replace one with something borrowed from elsewhere.
    /// </summary>
    private static bool IsIntegrated(long luid)
    {
        if (!Open(luid, out uint handle)) return false;

        try
        {
            return Query<uint>(handle, AdapterTypeQuery, out uint flags)
                && (flags & HybridIntegrated) != 0;
        }
        finally { Close(handle); }
    }

    /// A handle to the adapter from the display kernel, or false when it will not give one.
    private static bool Open(long luid, out uint handle)
    {
        handle = 0;

        var open = new OpenAdapterFromLuid
        {
            Luid = new AdapterLuid { Low = (uint)luid, High = (int)(luid >> 32) }
        };

        try
        {
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.Handle == 0) return false;
        }
        catch (DllNotFoundException)       { return false; }   // no display kernel to ask
        catch (EntryPointNotFoundException){ return false; }   // an older one that cannot answer

        handle = open.Handle;
        return true;
    }

    /// <summary>
    /// One question to the display kernel about an open adapter.
    /// </summary>
    /// <remarks>
    /// The size passed is the size of the structure asked for, and the kernel refuses anything
    /// else, which is how a structure that changed shape between Windows versions would be
    /// caught rather than silently misread: the call fails and the caller reports nothing.
    /// </remarks>
    private static bool Query<T>(uint handle, int type, out T value) where T : struct
    {
        value = default;

        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            // Zeroed first: these structures carry an input field, and everything not written
            // by the kernel is read back as though it had been.
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);

            var query = new QueryAdapterInfo
            {
                Handle   = handle,
                Type     = type,
                Data     = buffer,
                DataSize = (uint)size,
            };

            if (D3DKMTQueryAdapterInfo(ref query) != 0) return false;

            value = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        catch (Exception) { return false; }   // an adapter that answers nothing is not a fault
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// Handing the handle back. Leaking one per poll accumulates for as long as Pulse runs.
    private static void Close(uint handle)
    {
        var close = new CloseAdapter { Handle = handle };
        try { D3DKMTCloseAdapter(ref close); } catch { }   // nothing useful follows failing to hand a handle back
    }

    /// IID_IDXGIFactory1.
    private static Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    // Slots in the COM interface tables. Written out rather than hidden behind an interop
    // package, which for two calls would be a dependency to audit and ship for no gain.
    private const int Release       = 2;    // IUnknown
    private const int EnumAdapters1 = 12;   // IDXGIFactory1
    private const int GetDesc1      = 10;   // IDXGIAdapter1

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    /// <summary>One graphics adapter as Windows describes it.</summary>
    public readonly record struct Adapter(
        long Luid, string Description, float DedicatedVideoMemoryMb, float SharedSystemMemoryMb,
        bool Integrated)
    {
        /// <summary>
        /// How this adapter is named in Windows' "GPU Adapter Memory" performance counters.
        /// </summary>
        /// <remarks>
        /// A LUID is two halves, and the counters spell them high first: an adapter whose
        /// identifier is 0x000000000000cf0b appears as luid_0x00000000_0x0000cf0b_phys_0.
        /// Windows publishes the hexadecimal in upper case, so anything comparing against this
        /// must ignore case.
        ///
        /// The trailing _phys_0 is the physical adapter index within the identifier, which is
        /// zero for every adapter seen so far.
        /// </remarks>
        public string CounterKey =>
            $"luid_0x{(uint)(Luid >> 32):x8}_0x{(uint)Luid:x8}_phys_0";
    }

    /// <summary>
    /// Every adapter Windows can see, or an empty list if DXGI could not be asked.
    ///
    /// The dedicated video memory here is the figure Task Manager shows as "Dedicated GPU
    /// memory", because it comes from the same place. That matters: it is the number users
    /// compare us against, and it is right on integrated graphics, where the sensor library
    /// either reports nothing at all or reports the slice of system memory the BIOS set aside
    /// as though it were a card's own memory.
    /// </summary>
    public static List<Adapter> All()
    {
        var found = new List<Adapter>();
        IntPtr factory = IntPtr.Zero;

        try
        {
            if (CreateDXGIFactory1(ref FactoryId, out factory) != 0 || factory == IntPtr.Zero)
                return found;

            var enumerate = Method<EnumAdapters1Fn>(factory, EnumAdapters1);

            for (uint i = 0; ; i++)
            {
                if (enumerate(factory, i, out var adapter) != 0 || adapter == IntPtr.Zero) break;

                try
                {
                    if (Method<GetDesc1Fn>(adapter, GetDesc1)(adapter, out var desc) == 0)
                    {
                        found.Add(new Adapter(
                            desc.AdapterLuid,
                            desc.Description ?? "",
                            (float)(desc.DedicatedVideoMemory.ToUInt64() / (1024.0 * 1024.0)),
                            (float)(desc.SharedSystemMemory.ToUInt64()   / (1024.0 * 1024.0)),
                            IsIntegrated(desc.AdapterLuid)));
                    }
                }
                finally
                {
                    Method<ReleaseFn>(adapter, Release)(adapter);
                }
            }
        }
        catch
        {
            return new List<Adapter>();
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                // Releasing the COM factory. Nothing useful follows a failure here, and this
                // is already the cleanup path for whatever went wrong above.
                try { Method<ReleaseFn>(factory, Release)(factory); } catch { }
            }
        }

        return found;
    }

    /// <summary>
    /// Dedicated video memory for the adapter with this name, in MB, or 0 when it is not known.
    ///
    /// Matched by name because that is the only thing the sensor library and DXGI both report.
    /// Zero means "no answer", never "no memory", so callers must not display it as a capacity.
    /// </summary>
    public static float DedicatedVideoMemoryMb(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return 0;

        foreach (var adapter in All())
            if (string.Equals(adapter.Description.Trim(), adapterName.Trim(),
                              StringComparison.OrdinalIgnoreCase))
                return adapter.DedicatedVideoMemoryMb;

        return 0;
    }

    /// <summary>
    /// A short string describing every adapter present, or null if DXGI could not be asked.
    ///
    /// Compare two of these to know whether the hardware changed. Null is returned rather than
    /// an empty string on failure, so "we could not look" is never mistaken for "every adapter
    /// has gone" — which would otherwise re-enumerate the sensors on a loop.
    /// </summary>
    public static string? Signature()
    {
        var adapters = All();
        if (adapters.Count == 0) return null;

        var signature = new StringBuilder();

        foreach (var adapter in adapters)
        {
            // The identifier alone would do. The description is carried too so a log line
            // about a change says which card, which is the first thing anyone asks.
            signature.Append(adapter.Luid.ToString("x")).Append(':')
                     .Append(adapter.Description).Append('|');
        }

        return signature.ToString();
    }

    /// <summary>
    /// Turns a signature into something worth reading in a log: just the adapter names.
    /// </summary>
    public static string Describe(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return "unknown";

        var names = signature.Split('|', StringSplitOptions.RemoveEmptyEntries)
                             .Select(entry => entry[(entry.IndexOf(':') + 1)..]);

        return string.Join(", ", names);
    }
}
