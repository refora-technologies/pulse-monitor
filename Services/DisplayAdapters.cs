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

    // ── Windows' own opinion of what kind of adapter this is ────────────────────────────
    //
    // DXGI describes an adapter's memory but never says whether it is built into the
    // processor. The display kernel does, and it is the only vendor-neutral answer available:
    // asked about this machine it marks the Intel graphics HybridIntegrated and the GeForce
    // HybridDiscrete without either driver being consulted by name.
    //
    // This matters for temperature. Integrated graphics frequently publish no temperature of
    // their own, and the processor die they sit on is the figure Task Manager shows in their
    // place. Substituting it is only honest when Windows agrees the adapter really is part of
    // the processor, which is what this asks.

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

    /// Bit 5 of D3DKMT_ADAPTERTYPE: the adapter is the one built into the processor.
    private const uint HybridIntegrated = 1u << 5;

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid a);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo a);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter a);

    /// <summary>
    /// Whether Windows considers this adapter to be part of the processor.
    ///
    /// False whenever the question cannot be answered, so a failure here can only ever leave a
    /// reading absent, never replace one with something borrowed from elsewhere.
    /// </summary>
    private static bool IsIntegrated(long luid)
    {
        var open = new OpenAdapterFromLuid
        {
            Luid = new AdapterLuid { Low = (uint)luid, High = (int)(luid >> 32) }
        };

        try
        {
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.Handle == 0) return false;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, 0);
            var query = new QueryAdapterInfo
            {
                Handle   = open.Handle,
                Type     = AdapterTypeQuery,
                Data     = buffer,
                DataSize = sizeof(uint),
            };

            if (D3DKMTQueryAdapterInfo(ref query) != 0) return false;
            return ((uint)Marshal.ReadInt32(buffer) & HybridIntegrated) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);

            // The handle is the display kernel's, and leaking one per poll would accumulate
            // for as long as Pulse runs. Nothing useful follows a failure to hand it back.
            var close = new CloseAdapter { Handle = open.Handle };
            try { D3DKMTCloseAdapter(ref close); } catch { }
        }
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
