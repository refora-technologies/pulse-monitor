using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>
/// What Pulse knows about the PawnIO sensor driver on this machine, and the one thing it can do
/// about it: install it when the user asks.
/// </summary>
/// <remarks>
/// Everything here is read from Windows directly and is cheap, so it is simply read again
/// whenever the answer matters (the control panel opening, an install finishing) rather than
/// watched.
/// </remarks>
public sealed class SensorDriver
{
    private static readonly Lazy<SensorDriver> LazyInstance =
        new(() => new SensorDriver(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static SensorDriver Instance => LazyInstance.Value;

    private const string ServiceName = "PawnIO";

    public MachineFacts Facts { get; private set; }

    /// <summary>
    /// Whether FACEIT's anti-cheat is installed.
    /// </summary>
    /// <remarks>
    /// FACEIT refuses to start while PawnIO is loaded, because PawnIO's signing certificate has
    /// also been used by cheat software. Both FACEIT and PawnIO's author confirm it. Pulse cannot
    /// fix that, so the least it owes the user is to say so before installing the driver, and to
    /// say it louder when FACEIT is on this machine.
    ///
    /// FACEITService is the name FACEIT's own support pages tell people to look for. The install
    /// folder is checked as well, in case the service was removed and the client was not.
    /// </remarks>
    public bool FaceitInstalled { get; private set; }

    /// Where Pulse's installer puts the driver's own installer. Absent in a build run from source.
    public static string InstallerPath => Path.Combine(AppContext.BaseDirectory, "PawnIO_setup.exe");

    public bool CanInstall => File.Exists(InstallerPath);

    /// Raised on the thread that called Refresh, whenever any fact changes.
    public event EventHandler? Changed;

    private SensorDriver()
    {
        Facts = Read();
        FaceitInstalled = ReadFaceit();
        LogService.Info(nameof(SensorDriver), Describe());
    }

    public string Describe() =>
        $"Sensor driver: {Facts.Driver}. Battery: {(Facts.HasBattery ? "yes" : "no")}. "
      + $"FACEIT: {(FaceitInstalled ? "installed" : "not found")}. "
      + $"Driver installer {(CanInstall ? "present" : "missing")}.";

    public void Refresh()
    {
        var facts  = Read();
        var faceit = ReadFaceit();

        if (facts == Facts && faceit == FaceitInstalled) return;

        LogService.Info(nameof(SensorDriver), $"Changed. Was {Facts.Driver}, battery {Facts.HasBattery}, FACEIT {FaceitInstalled}.");

        Facts           = facts;
        FaceitInstalled = faceit;

        LogService.Info(nameof(SensorDriver), Describe());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TileStatus StatusOf(string tileId) => TileAvailability.Evaluate(tileId, Facts);

    // ── Installing ──────────────────────────────────────────────────────────────────

    public enum InstallOutcome { Working, NeedsRestart, Failed }

    /// <summary>
    /// Runs the driver's own installer and reports what is on the machine afterwards.
    /// </summary>
    /// <remarks>
    /// Judged by the service, not the exit code. The installer reports success in its own words
    /// and still exits non-zero, which is what made Pulse's setup once tell working machines their
    /// driver had failed (see DriverIsInstalled in setup.iss). The code is only kept for the log
    /// and for 3010, which means installed but not until Windows restarts.
    ///
    /// No prompt: Pulse already runs as administrator, which is what the installer needs.
    /// </remarks>
    public async Task<(InstallOutcome Outcome, string Detail)> InstallAsync()
    {
        if (!CanInstall)
            return (InstallOutcome.Failed, "The driver installer is not in Pulse's folder. Reinstalling Pulse puts it back.");

        LogService.Info(nameof(SensorDriver), "Installing the sensor driver, asked for from the tile chooser.");

        int code;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(InstallerPath, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });

            if (process == null)
                return (InstallOutcome.Failed, "Windows would not start the driver installer.");

            // Generous. It installs one small driver and normally takes a few seconds, but a
            // scanner inspecting a kernel driver on its way in can hold it far longer.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                LogService.Warn(nameof(SensorDriver), "The driver installer was still running after three minutes; leaving it be.");
                Refresh();
                return (InstallOutcome.Failed, "The driver installer is taking far longer than it should. It may still finish on its own.");
            }

            code = process.ExitCode;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(SensorDriver), "Starting the driver installer failed", ex);
            return (InstallOutcome.Failed, "Windows would not start the driver installer: " + ex.Message);
        }

        Refresh();
        LogService.Info(nameof(SensorDriver), $"Driver installer exited with {code}; driver is now {Facts.Driver}.");

        return Facts.Driver switch
        {
            DriverState.Running    => (InstallOutcome.Working, ""),
            DriverState.NotRunning => (InstallOutcome.NeedsRestart, ""),
            _ when code == 3010    => (InstallOutcome.NeedsRestart, ""),
            _                      => (InstallOutcome.Failed, $"Its installer stopped with code {code}. The details are in Pulse's log."),
        };
    }

    // ── Reading the machine ─────────────────────────────────────────────────────────

    private static MachineFacts Read() => new(ReadDriver(), ReadBattery());

    /// <summary>
    /// Whether the driver is on this machine and working, judged by its device rather than its service.
    /// </summary>
    /// <remarks>
    /// The service entry is not the answer, which was measured the hard way on 1 October 2026.
    /// PawnIO's own uninstaller removed its files, its entry under Installed apps and its device,
    /// and left the service entry behind with the driver still loaded, so Windows went on
    /// reporting it as running. Reading the service, Pulse would have called a removed driver
    /// working, and after the next restart called it installed but blocked. Pulse's setup has the
    /// same blind spot, for the same reason.
    ///
    /// The device is what the sensor library talks to, and Windows lists the devices a driver is
    /// serving under the service's Enum key: one before that uninstall, none after. The Installed
    /// apps entry is the second witness, for a driver that is installed but whose device did not
    /// start, which is what a block by anti-cheat or the vulnerable driver list looks like.
    /// </remarks>
    private static DriverState ReadDriver()
    {
        bool hasDevice = DeviceCount() > 0;
        bool listed    = HasInstalledAppsEntry();

        // What is left is Windows tidying up at the next restart, not a driver.
        if (!hasDevice && !listed) return DriverState.NotInstalled;

        return hasDevice && ServiceRunning() != false ? DriverState.Running : DriverState.NotRunning;
    }

    private static int DeviceCount()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}\Enum");
            return key?.GetValue("Count") is int count ? count : 0;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(SensorDriver), "Reading the sensor driver's devices failed", ex);
            return 0;
        }
    }

    private static bool HasInstalledAppsEntry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ServiceName}");
            return key != null;
        }
        catch { return false; }   // DeviceCount has already logged if the registry itself is failing
    }

    /// True or false from the service manager, or null when it would not say.
    private static bool? ServiceRunning()
    {
        IntPtr scm = IntPtr.Zero, service = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return null;

            service = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST ? false : null;

            return QueryServiceStatus(service, out var status) ? status.dwCurrentState == SERVICE_RUNNING : null;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(SensorDriver), "Asking Windows whether the sensor driver is running failed", ex);
            return null;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (scm     != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Whether Windows says this machine has a battery.
    /// </summary>
    /// <remarks>
    /// 128 is Windows saying there is no system battery. Anything else, including 255 for
    /// unknown, counts as having one: a tile wrongly switched off is worse than one that shows
    /// nothing, because the user has no way to turn it back on.
    /// </remarks>
    private static bool ReadBattery()
    {
        try
        {
            return !GetSystemPowerStatus(out var status) || status.BatteryFlag != 128;
        }
        catch { return true; }   // see above: unknown counts as present
    }

    private static bool ReadFaceit()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\FACEITService");
            if (key != null) return true;

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FACEIT AC");
            return Directory.Exists(folder);
        }
        catch { return false; }   // only changes how loudly the warning is worded; it is always shown
    }

    private const uint SC_MANAGER_CONNECT   = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_RUNNING      = 0x0004;
    private const int  ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                    dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int  BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
