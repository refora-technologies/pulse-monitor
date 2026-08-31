using Pulse.Services;

namespace Pulse;

/// <summary>
/// The entry point, written by hand rather than generated from App.xaml.
///
/// Only so that the sensor host can start without WPF. Pulse launches a second copy of its own
/// exe to read the hardware, and that copy has no windows, no theme and no resources — but the
/// generated entry point constructs the Application and loads every merged dictionary before
/// any of our code runs, so the host would have carried a full duplicate of the UI's resource
/// tree for the lifetime of the app to display nothing at all.
///
/// See &lt;StartupObject&gt; in Pulse.csproj, which is what makes the compiler choose this over
/// the generated one.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // First, before anything touches WPF.
        if (args.Contains(SensorHost.Argument))
        {
            try
            {
                return SensorHost.Run(args);
            }
            catch (Exception ex)
            {
                // Standard error is the host's channel to Pulse, and Pulse writes what arrives
                // there into the log. Nothing else here can report anything.
                try { Console.Error.WriteLine($"error|The sensor host failed: {ex.GetType().Name}: {ex.Message}"); }
                catch { }
                return 1;
            }
        }

        var app = new App();

        // Loading the merged dictionaries can fail, and when it did the process ended before
        // there was any handler to notice — a startup crash that left nothing behind but a
        // session marker saying Pulse had been "starting up". Logged here, then rethrown,
        // because a Pulse with no styles is not a Pulse worth running.
        try
        {
            app.InitializeComponent();
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(Program), "Loading the application resources failed", ex);
            throw;
        }

        return app.Run();
    }
}
