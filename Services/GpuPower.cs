using System.Runtime.InteropServices;

namespace Pulse.Services;

/// <summary>
/// Whether a graphics adapter is asleep, asked of Windows rather than of the adapter.
/// </summary>
/// <remarks>
/// Pulse kept laptop graphics cards awake. Measured on 1 October 2026 on the battery of an RTX 3050
/// laptop with nothing else connected: with Pulse closed the card was awake 18% and then 1% of the
/// time, and with Pulse running 92%. Reading a card through its vendor's library wakes it, and
/// reading it every couple of seconds keeps it that way, which on a laptop away from its charger is
/// battery spent on a number nobody needed while the card had nothing to do.
///
/// Windows records each device's power state in its device properties, and reading that property
/// is a question to Windows, not to the card. The same check found the card asleep through every
/// closed phase of that test, while the card was being read this way every five seconds. So the
/// sensor host asks this first, and leaves a sleeping card alone.
///
/// Matched by name, the one thing the sensor library and Windows both give an adapter. Two adapters
/// with the same name cannot be told apart this way, so for them the answer is "unknown", and an
/// unknown card is read as it always was. Being wrong in that direction costs battery; being wrong
/// in the other would blank a card that is working.
/// </remarks>
internal static class GpuPower
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    // DEVPKEY_Device_DeviceDesc, DEVPKEY_Device_FriendlyName and DEVPKEY_Device_PowerData share a
    // property set; the number picks the property.
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint DeviceDescription = 2;
    private const uint FriendlyName      = 14;
    private const uint PowerData         = 32;

    /// D0 in CM_POWER_DATA's PD_MostRecentPowerState: fully on. D1 to D3 are the sleep states.
    private const uint FullyOn = 1;

    private const uint PresentOnly = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevInfoData
    {
        public uint   Size;
        public Guid   ClassGuid;
        public uint   DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Set;
        public uint Id;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DevInfoData data, ref DevPropKey key,
        out uint type, byte[]? buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    /// <summary>
    /// True when Windows says the adapter of this name is in a sleep state, false when it says it
    /// is on, and null when that cannot be said: no such adapter, more than one, or no answer.
    /// </summary>
    public static bool? IsAsleep(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return null;

        var guid = DisplayClass;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, PresentOnly);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;

        try
        {
            bool? answer = null;
            int matches = 0;

            for (uint i = 0; ; i++)
            {
                var data = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                var name = ReadString(set, ref data, FriendlyName) ?? ReadString(set, ref data, DeviceDescription);
                if (!string.Equals(name?.Trim(), adapterName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                matches++;
                var state = ReadPowerState(set, ref data);
                answer = state is { } s ? s != FullyOn : null;
            }

            return matches == 1 ? answer : null;
        }
        catch
        {
            return null;   // a question about power must never cost a reading; unknown means read it
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? ReadString(IntPtr set, ref DevInfoData data, uint id)
    {
        var key = new DevPropKey { Set = DeviceProperties, Id = id };
        var buffer = new byte[512];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, (uint)buffer.Length, out var used, 0) || used < 2)
            return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)used).TrimEnd('\0');
    }

    /// The most recent power state from CM_POWER_DATA, which starts with its own size and then the
    /// state as a 32-bit value.
    private static uint? ReadPowerState(IntPtr set, ref DevInfoData data)
    {
        var key = new DevPropKey { Set = DeviceProperties, Id = PowerData };
        var buffer = new byte[256];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, (uint)buffer.Length, out var used, 0) || used < 8)
            return null;
        return BitConverter.ToUInt32(buffer, 4);
    }
}
