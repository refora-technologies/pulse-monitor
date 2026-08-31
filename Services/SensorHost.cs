using System.Globalization;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// The child process that actually reads the hardware.
///
/// Pulse launches a second copy of itself with --sensor-host and talks to it over the three
/// standard streams: snapshots out, commands in, diagnostics on standard error. The value of
/// the arrangement is entirely in the process boundary. Reading a sensor can raise an access
/// violation from inside a vendor driver, and .NET does not let anything catch that — it ends
/// the process on the spot. Over here that costs a restart nobody sees; in Pulse it took the
/// overlay, the tray icon and the control panel with it.
///
/// The host is deliberately dumb. It holds no settings, keeps no history, and makes no
/// decisions beyond which adapter to read. Everything it needs arrives on the command line or
/// over standard input, so a restarted host is indistinguishable from the one it replaced.
/// </summary>
public static class SensorHost
{
    public const string Argument = "--sensor-host";

    /// Sent once before the first reading, so Pulse can tell a host that is starting up from
    /// one that has died. Not JSON, so the snapshot parser ignores it either way.
    public const string ReadyBanner = "#pulse-sensor-host";

    /// <summary>
    /// Runs until standard input closes. Returns the process exit code.
    ///
    /// Never throws: whatever goes wrong here, the useful thing is an orderly exit with a line
    /// on standard error, because Pulse is watching for the exit and will start another one.
    /// </summary>
    public static int Run(string[] args)
    {
        // Both ends speak UTF-8 explicitly. The default here is the console's OEM codepage,
        // which would mangle any adapter name outside ASCII — and the JSON is escaped to ASCII
        // as well, so it takes two independent mistakes to corrupt a reading rather than one.
        TrySetUtf8();

        var stdout = Console.Out;
        var stderr = Console.Error;

        void Log(string level, string message)
        {
            try
            {
                // One line, level first, so Pulse can log it at the right severity. Newlines
                // in the message would look like a second diagnostic, so they are flattened.
                stderr.WriteLine(level + "|" + message.Replace('\r', ' ').Replace('\n', ' '));
                stderr.Flush();
            }
            catch { }
        }

        using var reader = new SensorReader(Log);

        double interval = ReadInterval(args);
        reader.SetSubsystems(ReadSubsystems(args));
        reader.SetSelectedGpu(ReadOption(args, "--gpu="));

        try
        {
            stdout.WriteLine(ReadyBanner);
            stdout.Flush();
        }
        catch { }

        reader.Open();

        // Commands arrive on their own thread. Reading them on the polling thread would mean
        // a command could only be noticed between polls, and a stalled poll would make Pulse
        // look unresponsive to a settings change it had already applied everywhere else.
        var stop = new ManualResetEventSlim(false);
        var pending = new object();
        double? newInterval = null;
        SensorSubsystems? newSubsystems = null;
        string? newGpu = null;
        bool gpuPending = false, rescanPending = false;

        var commands = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    // Null means Pulse has closed its end, which means Pulse is gone. There is
                    // nothing left to report to, so stop rather than poll hardware forever.
                    var line = Console.In.ReadLine();
                    if (line == null) break;

                    var command = SensorCommand.TryParse(line);
                    if (command == null) continue;

                    lock (pending)
                    {
                        switch (command.Value.Cmd)
                        {
                            case SensorCommand.SetInterval:
                                newInterval = command.Value.Seconds;
                                break;

                            case SensorCommand.SetSubsystems:
                                if (Enum.TryParse<SensorSubsystems>(command.Value.Text, ignoreCase: true, out var parsed))
                                    newSubsystems = parsed;
                                break;

                            case SensorCommand.SelectGpu:
                                newGpu     = command.Value.Text;
                                gpuPending = true;
                                break;

                            case SensorCommand.Rescan:
                                rescanPending = true;
                                break;

                            case SensorCommand.Quit:
                                stop.Set();
                                return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("warn", $"The command channel closed: {ex.GetType().Name}");
            }
            finally
            {
                stop.Set();
            }
        })
        {
            IsBackground = true,
            Name         = "Pulse sensor host commands",
        };

        commands.Start();

        // The set of graphics adapters as it was when sensors were opened. Any difference from
        // this means the device list the sensor library built is out of date.
        var adapters = DisplayAdapters.Signature();
        long adaptersCheckedAt = Environment.TickCount64;

        while (!stop.IsSet)
        {
            bool rescan = false;

            lock (pending)
            {
                if (newInterval is { } seconds)      { interval = Clamp(seconds); newInterval = null; }
                if (newSubsystems is { } subsystems) { reader.SetSubsystems(subsystems); newSubsystems = null; }
                if (gpuPending)                      { reader.SetSelectedGpu(newGpu); gpuPending = false; }
                if (rescanPending)                   { rescan = true; rescanPending = false; }
            }

            // Every few seconds rather than every poll: asking DXGI is cheap but not free, and
            // a card being switched off is not something anyone does twice a second.
            if (Environment.TickCount64 - adaptersCheckedAt >= 5000)
            {
                adaptersCheckedAt = Environment.TickCount64;

                var now = DisplayAdapters.Signature();

                // Null means DXGI could not be asked, which is not the same as the adapters
                // having gone. Acting on it would re-enumerate sensors on a loop.
                if (now != null && adapters != null && now != adapters)
                {
                    Log("info", $"The graphics adapters changed. Was: {DisplayAdapters.Describe(adapters)}. "
                              + $"Now: {DisplayAdapters.Describe(now)}.");
                    rescan = true;
                }

                if (now != null) adapters = now;
            }

            // Outside the lock. A rescan closes and reopens the whole sensor library, which
            // takes seconds on some machines, and holding the lock through it would stall the
            // command thread — the one part that has to keep working when hardware does not.
            if (rescan) reader.Rescan();

            var snapshot = reader.Read();

            var line = SensorProtocol.Serialize(snapshot);
            if (line != null)
            {
                try
                {
                    stdout.Write(line);
                    stdout.Flush();
                }
                catch (Exception ex)
                {
                    // Pulse has gone, or the pipe broke. Either way there is nobody to send to.
                    Log("warn", $"Could not send a reading: {ex.GetType().Name}");
                    break;
                }
            }

            // Interruptible, so a quit or a closed input does not have to wait out a poll.
            if (stop.Wait(TimeSpan.FromSeconds(interval))) break;
        }

        Log("info", "Sensor host stopping.");
        return 0;
    }

    /// Polls faster than twice a second cost more than they show, and a very long interval
    /// makes the host look hung to the watchdog on the other side.
    private static double Clamp(double seconds) => Math.Clamp(seconds, 0.5, 60);

    private static double ReadInterval(string[] args) =>
        double.TryParse(ReadOption(args, "--interval="), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Clamp(v)
            : 2;

    private static SensorSubsystems ReadSubsystems(string[] args) =>
        Enum.TryParse<SensorSubsystems>(ReadOption(args, "--subsystems="), ignoreCase: true, out var s)
            ? s
            : SensorSubsystems.None;

    /// <summary>
    /// Reads --name=value from the command line.
    ///
    /// Written as one argument rather than two so a value can be empty or contain spaces
    /// without a second round of quoting, which matters because adapter identifiers come
    /// straight from a driver and we do not get to choose what is in them.
    /// </summary>
    private static string? ReadOption(string[] args, string prefix)
    {
        foreach (var arg in args)
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return arg[prefix.Length..];

        return null;
    }

    private static void TrySetUtf8()
    {
        // Throws when there is no console and no redirection, which is what happens if someone
        // runs the host by hand. Harmless: nothing is reading it either.
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        try { Console.InputEncoding  = new UTF8Encoding(false); } catch { }
    }
}
