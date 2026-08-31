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

    /// <summary>
    /// A short string describing every adapter present, or null if DXGI could not be asked.
    ///
    /// Compare two of these to know whether the hardware changed. Null is returned rather than
    /// an empty string on failure, so "we could not look" is never mistaken for "every adapter
    /// has gone" — which would otherwise re-enumerate the sensors on a loop.
    /// </summary>
    public static string? Signature()
    {
        IntPtr factory = IntPtr.Zero;

        try
        {
            if (CreateDXGIFactory1(ref FactoryId, out factory) != 0 || factory == IntPtr.Zero)
                return null;

            var enumerate = Method<EnumAdapters1Fn>(factory, EnumAdapters1);
            var signature = new StringBuilder();

            for (uint i = 0; ; i++)
            {
                if (enumerate(factory, i, out var adapter) != 0 || adapter == IntPtr.Zero) break;

                try
                {
                    if (Method<GetDesc1Fn>(adapter, GetDesc1)(adapter, out var desc) == 0)
                    {
                        // The identifier alone would do. The description is carried too so a
                        // log line about a change says which card, which is the first thing
                        // anyone asks.
                        signature.Append(desc.AdapterLuid.ToString("x")).Append(':')
                                 .Append(desc.Description).Append('|');
                    }
                }
                finally
                {
                    Method<ReleaseFn>(adapter, Release)(adapter);
                }
            }

            // No adapters at all means the call did not work as expected; every machine that
            // can run Pulse has at least one. Treated as "could not look".
            return signature.Length == 0 ? null : signature.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                try { Method<ReleaseFn>(factory, Release)(factory); } catch { }
            }
        }
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
