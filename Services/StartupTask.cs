using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Owns the "start Pulse when you sign in" scheduled task.
///
/// This exists as one place because the task was previously created in two: the installer
/// and the settings toggle both shelled out to <c>schtasks /Create</c> with their own
/// argument strings. That is how the defaults below went unnoticed for so long — nobody was
/// looking at one definition.
///
/// The task is registered from an XML definition rather than the bare <c>/Create</c> command
/// line, because three of its defaults are actively wrong for a monitoring app that is meant
/// to sit running all day:
///
///   DisallowStartIfOnBatteries  defaults true  — the task simply does not start on a laptop
///                                                running on battery. Nothing is logged and
///                                                the setting still reads as enabled.
///   StopIfGoingOnBatteries      defaults true  — Windows terminates Pulse when the machine
///                                                is unplugged.
///   ExecutionTimeLimit          defaults PT72H — Windows terminates Pulse after three days
///                                                of uptime.
///
/// None of these can be set through schtasks' command line, which is why the XML route is
/// used. Windows itself emits task XML as UTF-16, so that is what is written here.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "PulseMonitor";

    /// <summary>What the scheduled task currently looks like, as far as we care.</summary>
    public readonly record struct State(bool Exists, string CommandPath, bool SettingsCorrect)
    {
        public static State Missing => new(false, "", false);
    }

    /// <summary>
    /// Reads the task. CommandPath is the exe it launches, empty when there is no task.
    ///
    /// SettingsCorrect is false when the task exists but carries any of the harmful defaults,
    /// which is the case for every task created by Pulse 1.1.0 and earlier.
    /// </summary>
    public static State Query()
    {
        var xml = RunCapture($"/Query /TN \"{TaskName}\" /XML");
        if (string.IsNullOrWhiteSpace(xml) || xml.IndexOf("<Task", StringComparison.Ordinal) < 0)
            return State.Missing;

        var command = Between(xml, "<Command>", "</Command>");

        bool correct = HasValue(xml, "DisallowStartIfOnBatteries", "false")
                    && HasValue(xml, "StopIfGoingOnBatteries",     "false")
                    && HasValue(xml, "ExecutionTimeLimit",         "PT0S");

        return new State(true, command, correct);
    }

    /// <summary>
    /// Creates or repairs the task so it points at <paramref name="exePath"/> and carries
    /// settings that let Pulse actually run.
    ///
    /// An existing task is only replaced once the new definition has been accepted, so a
    /// failure here leaves whatever was there before rather than removing startup entirely.
    /// </summary>
    public static bool Install(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        string? file = null;
        try
        {
            file = Path.Combine(Path.GetTempPath(), $"pulse-task-{Guid.NewGuid():N}.xml");

            // UTF-16 with a BOM: the schema declares UTF-16 and that is what Windows emits
            // when asked for a task definition, so it is what it expects to be handed back.
            File.WriteAllText(file, BuildXml(exePath), new UnicodeEncoding(false, true));

            // /F replaces atomically, so the old task survives if this is rejected.
            return Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(StartupTask), "Could not register the startup task", ex);
            return false;
        }
        finally
        {
            if (file != null) try { File.Delete(file); } catch { }
        }
    }

    public static bool Remove() => Run($"/Delete /TN \"{TaskName}\" /F");

    private static string BuildXml(string exePath)
    {
        // Registering against the current account's SID rather than a name avoids the whole
        // domain\user vs .\user formatting question, and matches what schtasks stored before.
        string sid;
        try   { sid = WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch { sid = ""; }

        var principal = sid.Length > 0
            ? $"      <UserId>{sid}</UserId>\r\n"
            : "";

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Pulse when you sign in.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
        {principal}      <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <StartWhenAvailable>true</StartWhenAvailable>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <AllowHardTerminate>true</AllowHardTerminate>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <WakeToRun>false</WakeToRun>
            <Hidden>false</Hidden>
            <Enabled>true</Enabled>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exePath)}</Command>
              <Arguments>--startup</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static bool HasValue(string xml, string element, string expected)
        => string.Equals(Between(xml, $"<{element}>", $"</{element}>").Trim(),
                         expected, StringComparison.OrdinalIgnoreCase);

    private static string Between(string text, string open, string close)
    {
        int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += open.Length;

        int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? "" : text[start..end];
    }

    private static bool Run(string arguments) => RunCapture(arguments) != null;

    /// <summary>
    /// Runs schtasks and returns its standard output, or null when it failed.
    ///
    /// The output encoding is deliberately left alone. Task XML declares itself as UTF-16 and
    /// Windows writes it that way to a console, but redirected it arrives in the console
    /// codepage like everything else. Forcing the stream to Unicode turns it into mojibake,
    /// the task looks absent, and Pulse concludes startup is switched off.
    /// </summary>
    private static string? RunCapture(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = arguments,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var process = Process.Start(info);
            if (process is null) return null;

            // Both pipes are drained together. Reading one to the end and only then starting
            // on the other deadlocks if the child fills the second pipe's buffer in the
            // meantime, and neither WaitForExit nor its timeout is ever reached because we
            // are still blocked in the read.
            var errorTask = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            string error  = errorTask.GetAwaiter().GetResult();

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                LogService.Warn(nameof(StartupTask), $"schtasks timed out: {arguments}");
                return null;
            }

            if (process.ExitCode == 0) return output;

            // A query for a task that does not exist is an ordinary answer, not a fault.
            if (arguments.StartsWith("/Query", StringComparison.OrdinalIgnoreCase)) return null;

            LogService.Warn(nameof(StartupTask),
                $"schtasks failed ({process.ExitCode}): {arguments} :: {error.Trim()}");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(StartupTask), $"Could not run schtasks: {arguments}", ex);
            return null;
        }
    }
}
