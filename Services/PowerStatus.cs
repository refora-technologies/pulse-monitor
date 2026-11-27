using System.Runtime.InteropServices;

namespace Pulse.Services;

/// Where a laptop's power is coming from, as Windows sees it.
public enum BatteryPower { Unknown, OnBattery, Charging, PluggedIn }

/// <summary>
/// The battery as Windows itself reports it: the percentage the taskbar shows, and whether the
/// charger is in and charging.
/// </summary>
/// <remarks>
/// The percentage used to come only from the sensor library, which works it out from the
/// battery's charge and its full capacity, read once when the battery is first opened. On
/// 25 Nov 2026 the owner's laptop shut down on a flat battery, started again on the charger,
/// and Pulse showed 9 % through the evening while Windows showed 80 %; switching the tile off
/// and on, which opens the battery again, put it right. Windows' own figure is what anyone
/// compares the tile against, costs nothing to ask for, and cannot go stale like that.
/// </remarks>
internal static class PowerStatus
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    /// Windows' battery percentage (null with no battery, or when Windows does not know) and
    /// where the power is coming from.
    internal static (float? Level, BatteryPower Power) Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return (null, BatteryPower.Unknown);
            return Interpret(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent);
        }
        catch
        {
            return (null, BatteryPower.Unknown);
        }
    }

    /// The meaning of GetSystemPowerStatus's three bytes. Kept apart so it can be tested on a
    /// machine in any state: charger 0 off, 1 on, 255 unknown; flag 8 charging, 128 no battery,
    /// 255 unknown; percent 0 to 100, or 255 unknown.
    internal static (float? Level, BatteryPower Power) Interpret(byte acLine, byte flag, byte percent)
    {
        if (flag != 255 && (flag & 128) != 0) return (null, BatteryPower.Unknown);   // no battery
        float? level = percent <= 100 ? percent : null;
        if (level is null) return (null, BatteryPower.Unknown);

        var power = flag != 255 && (flag & 8) != 0 ? BatteryPower.Charging
                  : acLine == 1                    ? BatteryPower.PluggedIn
                  : acLine == 0                    ? BatteryPower.OnBattery
                  : BatteryPower.Unknown;
        return (level, power);
    }
}
