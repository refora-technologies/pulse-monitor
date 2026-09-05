using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

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

    /// How long schtasks gets before it is assumed to be stuck. Generous: it normally answers
    /// in well under a second, and the only cost of waiting is a slower diagnostics export.
    private const int TimeoutMs = 10_000;

    /// <summary>
    /// The full path to schtasks, rather than its name.
    /// </summary>
    /// <remarks>
    /// A bare name is resolved through the search path, and the search path is not ours. Pulse
    /// runs elevated, so anything it starts starts elevated too, and a program that decides
    /// what to run by searching directories it does not control is handing that decision to
    /// whoever can write to one of them. Naming the file removes the question.
    /// </remarks>
    private static readonly string SchTasks =
        Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>
    /// What Windows says about the task.
    /// </summary>
    /// <remarks>
    /// Four answers rather than two, because "no" was covering three different situations and
    /// the difference matters. A task that is registered but switched off will not start Pulse,
    /// and used to read exactly like one that is correctly set up. And a query that failed used
    /// to read exactly like a task that is not there, which is how turning startup off could
    /// report success while leaving the task in place.
    /// </remarks>
    public enum TaskPresence
    {
        /// No such task. The user turned startup off, or never turned it on.
        Missing,

        /// Registered, switched on, and with a trigger that will fire at logon.
        Enabled,

        /// Registered but switched off, or with no logon trigger that will fire. Nothing will
        /// start. Usually because somebody disabled it in Task Scheduler.
        Disabled,

        /// Windows did not answer. Says nothing either way, and must never be acted on as if
        /// it did.
        Unreadable,
    }

    /// <summary>What the scheduled task currently looks like, as far as we care.</summary>
    public readonly record struct State(TaskPresence Presence, string CommandPath, bool SettingsCorrect)
    {
        /// The task is registered, whether or not it would actually run.
        public bool Exists => Presence is TaskPresence.Enabled or TaskPresence.Disabled;

        /// The task is registered and will start Pulse at the next logon.
        public bool WillRun => Presence == TaskPresence.Enabled;

        /// Windows gave a straight answer, whatever that answer was.
        public bool Known => Presence != TaskPresence.Unreadable;

        public static State Missing    => new(TaskPresence.Missing,    "", false);
        public static State Unreadable => new(TaskPresence.Unreadable, "", false);
    }

    /// <summary>
    /// Reads the task. CommandPath is the exe it launches, empty when there is no task.
    ///
    /// SettingsCorrect is false when the task exists but carries any of the harmful defaults,
    /// which is the case for every task created by Pulse 1.1.0 and earlier.
    /// </summary>
    public static State Query()
    {
        var reply = RunCapture($"/Query /TN \"{TaskName}\" /XML");

        // schtasks never ran, timed out, or was killed. Nothing has been learned.
        if (!reply.Ran) return State.Unreadable;

        // A non-zero exit from a query is overwhelmingly "no such task". It is not worth
        // trying to tell that from other failures by reading the message, which is translated
        // on a localised Windows and cannot be matched on.
        if (reply.ExitCode != 0) return State.Missing;

        return ReadState(reply.Output);
    }

    /// <summary>
    /// Turns a task definition into the handful of facts Pulse acts on.
    /// </summary>
    /// <remarks>
    /// Parsed as XML rather than searched as text, which fixes two real faults at once.
    ///
    /// The command is written into the definition escaped, so an install under a folder whose
    /// name contains an ampersand came back as <c>C:\Games &amp;amp; Apps\Pulse.exe</c> and
    /// never matched the path it had just been created from. Pulse concluded the task belonged
    /// to some other build and rewrote it, at every single launch, forever.
    ///
    /// And <c>Enabled</c> appears twice in a task definition, once for the trigger and once
    /// for the task. Searching for the first one finds the trigger's, so a task somebody had
    /// switched off in Task Scheduler still read as switched on, and Pulse went on promising
    /// a startup that was never going to happen.
    ///
    /// Internal so the tests can put a definition in and read the answer out, without a
    /// scheduled task, administrator rights, or a machine willing to have one created.
    /// </remarks>
    internal static State ReadState(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return State.Unreadable;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            // A reply arrived and could not be understood. Not the same as no task, and
            // treating it as one is how Pulse used to talk itself into rewriting things.
            LogService.Warn(nameof(StartupTask), $"The startup task definition could not be read: {ex.Message}");
            return State.Unreadable;
        }

        var root = doc.Root;
        if (root == null || root.Name.LocalName != "Task") return State.Unreadable;

        var ns       = root.Name.Namespace;
        var settings = root.Element(ns + "Settings");
        var exec     = root.Element(ns + "Actions")?.Element(ns + "Exec");

        var command   = exec?.Element(ns + "Command")?.Value.Trim()   ?? "";
        var arguments = exec?.Element(ns + "Arguments")?.Value.Trim() ?? "";

        // Absent means the default, which for all three of these is true.
        bool taskOn    = Flag(settings, ns, "Enabled", fallback: true);
        bool triggerOn = AnyTriggerEnabled(root, ns);

        bool correct = Is(settings, ns, "DisallowStartIfOnBatteries", "false")
                    && Is(settings, ns, "StopIfGoingOnBatteries",     "false")
                    && Is(settings, ns, "ExecutionTimeLimit",         "PT0S")
                    // Without this the control panel opens itself at every logon, which is
                    // not what "start with Windows" is asking for.
                    && arguments.Contains("--startup", StringComparison.OrdinalIgnoreCase);

        return new State(taskOn && triggerOn ? TaskPresence.Enabled : TaskPresence.Disabled,
                         command, correct);
    }

    /// <summary>Whether anything in the definition will actually fire.</summary>
    /// <remarks>
    /// A task with no triggers at all is registered and will never start on its own, which for
    /// our purposes is the same as being switched off. A trigger with no Enabled element is
    /// enabled: Windows writes the element only when it is false.
    /// </remarks>
    private static bool AnyTriggerEnabled(XElement root, XNamespace ns)
    {
        var triggers = root.Element(ns + "Triggers");
        if (triggers == null) return false;

        foreach (var trigger in triggers.Elements())
            if (Flag(trigger, ns, "Enabled", fallback: true)) return true;

        // Either there were none, or every one of them is switched off. Same outcome.
        return false;
    }

    /// Whether an element carries exactly this value. Absent counts as no, which is right for
    /// the three settings this is used on: absent means the schtasks default, and all three
    /// defaults are the wrong ones.
    private static bool Is(XElement? parent, XNamespace ns, string name, string expected)
        => string.Equals(parent?.Element(ns + name)?.Value.Trim(), expected,
                         StringComparison.OrdinalIgnoreCase);

    private static bool Flag(XElement? parent, XNamespace ns, string name, bool fallback)
    {
        var value = parent?.Element(ns + name)?.Value.Trim();
        if (string.IsNullOrEmpty(value)) return fallback;
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a task's recorded command is this build, tolerating what the console codepage
    /// does to the text on the way out.
    /// </summary>
    /// <remarks>
    /// A plain substring test is not enough. The task XML comes back through a redirected pipe
    /// decoded in the console codepage, for the reason set out on RunCapture, and any character
    /// that codepage cannot represent arrives as a question mark. So a Pulse installed to a path
    /// containing non-Latin characters never matched itself: the caller concluded the task
    /// pointed at some other build and rewrote it, on every single launch, forever.
    ///
    /// The current path is therefore put through the same loss before comparing. Two different
    /// paths that differ only in characters the codepage destroys would now be treated as the
    /// same, which is worth it: the cost of that is one stale task, and the cost of the
    /// alternative is rewriting a scheduled task at every logon.
    /// </remarks>
    public static bool CommandIsThisBuild(string commandPath, string exePath)
    {
        if (string.IsNullOrEmpty(commandPath) || string.IsNullOrEmpty(exePath)) return false;
        if (SamePath(commandPath, exePath)) return true;

        var lossy = ThroughConsoleCodepage(exePath);

        // Nothing was lost, so the first test was already conclusive.
        if (string.Equals(lossy, exePath, StringComparison.Ordinal)) return false;

        return SamePath(commandPath, lossy);
    }

    /// <summary>
    /// Whether two recorded paths name the same program.
    /// </summary>
    /// <remarks>
    /// A whole path each, rather than asking whether one appears inside the other. Containment
    /// says yes to things that are not true: a task running <c>C:\Pulse copy\Pulse.exe</c>
    /// contains <c>C:\Pulse</c>, and an installation at <c>C:\Pulse</c> would have claimed it.
    /// The definition holds one command and there is no reason to search it.
    /// </remarks>
    private static bool SamePath(string a, string b)
    {
        if (Equal(a, b)) return true;

        // A recorded command that carries its arguments as well. Windows keeps those in an
        // element of their own, but a task created by an older Pulse went through
        // schtasks /Create and could store the whole command line, so the program is taken
        // from the front of it before comparing. Still a whole path against a whole path.
        var head = LeadingProgram(a);
        return head.Length > 0 && Equal(head, b);
    }

    private static bool Equal(string a, string b)
    {
        a = Tidy(a);
        b = Tidy(b);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// The program named at the start of a command line, quoted or not.
    private static string LeadingProgram(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return "";

        if (command[0] == '"')
        {
            int close = command.IndexOf('"', 1);
            return close > 1 ? command[1..close] : "";
        }

        // Unquoted, so the extension is the only reliable boundary: an unquoted path may
        // contain spaces, and the first one is not necessarily where the arguments start.
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    private static string Tidy(string path)
    {
        path = path.Trim().Trim('"').Trim();
        if (path.Length == 0) return "";

        // GetFullPath settles the differences that do not matter: a trailing slash, a relative
        // segment, doubled separators. It refuses a path containing characters the console
        // codepage destroyed, which is exactly the case the caller handles above, so the
        // original is kept rather than the comparison being abandoned.
        try   { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path.TrimEnd('\\'); }
    }

    /// The same mangling a redirected schtasks reply goes through, applied deliberately.
    private static string ThroughConsoleCodepage(string text)
    {
        try
        {
            var encoding = Console.OutputEncoding;
            return encoding.GetString(encoding.GetBytes(text));
        }
        catch
        {
            // No console attached, or a codepage that refuses the round trip. Falling back to
            // the original means the caller behaves exactly as it did before this existed.
            return text;
        }
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
            //
            // CreateNew and no sharing, rather than WriteAllText. This file is handed to an
            // elevated schtasks a moment later and it decides what will run at every logon, so
            // it is worth a little care about what is between writing it and reading it back.
            // CreateNew refuses to follow an existing name, so a file or link already sitting
            // there is an error rather than a target; FileShare.None means nothing can open it
            // while we are writing. The name is a fresh GUID either way.
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UnicodeEncoding(false, true)))
            {
                writer.Write(BuildXml(exePath));
            }

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

    /// <summary>
    /// Removes the task, and treats a task that was never there as removed.
    ///
    /// schtasks exits non-zero when asked to delete something that does not exist, which this
    /// used to report as failure. The caller then wrote the opposite of what was asked, so
    /// turning startup off while the task was already gone flipped the switch back on and left
    /// it claiming Pulse starts with Windows when nothing would. There was no way to turn it
    /// off from the panel at all.
    ///
    /// Absence is confirmed by looking rather than by reading schtasks' error text, which is
    /// translated on a localised Windows and cannot be matched on.
    /// </summary>
    public static bool Remove()
    {
        // Asked first so the log is not filled with schtasks errors for a task nobody has.
        var before = Query();

        // A query that failed is not an answer, and it used to be treated as one. Absence was
        // inferred from it, so turning startup off while Task Scheduler was momentarily
        // unavailable reported success, moved the toggle, and left the task exactly where it
        // was. Pulse then went on starting with Windows while claiming it would not.
        if (!before.Known)
        {
            LogService.Warn(nameof(StartupTask),
                "Windows did not answer, so the startup task was left alone rather than reported as removed.");
            return false;
        }

        if (!before.Exists) return true;

        if (Run($"/Delete /TN \"{TaskName}\" /F")) return true;

        // Report success only if it is genuinely gone. A delete that failed because the task
        // is still there is a real failure, and the caller needs to know: startup really is
        // still on, whatever the user just asked for.
        var after = Query();
        return after.Known && !after.Exists;
    }

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

    private static bool Run(string arguments)
    {
        var reply = RunCapture(arguments);
        return reply.Ran && reply.ExitCode == 0;
    }

    /// <summary>
    /// What came back from schtasks: whether it ran at all, what it exited with, and what it
    /// said. Three separate facts, because collapsing them into "output or null" is what made
    /// a failure to ask indistinguishable from an answer of no.
    /// </summary>
    private readonly record struct Reply(bool Ran, int ExitCode, string Output)
    {
        public static Reply Failed => new(false, -1, "");
    }

    /// <summary>
    /// Runs schtasks and returns its standard output, or null when it failed.
    ///
    /// The output encoding is deliberately left alone. Task XML declares itself as UTF-16 and
    /// Windows writes it that way to a console, but redirected it arrives in the console
    /// codepage like everything else. Forcing the stream to Unicode turns it into mojibake,
    /// the task looks absent, and Pulse concludes startup is switched off.
    /// </summary>
    private static Reply RunCapture(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName               = SchTasks,
                Arguments              = arguments,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var process = Process.Start(info);
            if (process is null) return Reply.Failed;

            // Both pipes are drained, and neither is waited on before the process is known to
            // have finished. Reading one to the end deadlocks if the child fills the other
            // pipe's buffer, so both are started asynchronously first. Waiting on those reads
            // before WaitForExit is just as bad in the other direction: a read only returns
            // once the child closes its pipe, so a schtasks that hangs blocks here forever and
            // the timeout below is never evaluated. That is what this code did, which meant a
            // hung Task Scheduler could freeze whatever called it, including the diagnostics
            // export on the UI thread.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                LogService.Warn(nameof(StartupTask), $"schtasks timed out: {arguments}");
                return Reply.Failed;
            }

            // The pipes close when the process ends, so these are already complete. Bounded
            // regardless, because a grandchild that inherited the handles would hold them open
            // and put the wait we just escaped straight back.
            if (!Task.WhenAll(outputTask, errorTask).Wait(2_000))
            {
                LogService.Warn(nameof(StartupTask), $"schtasks output could not be read: {arguments}");
                return Reply.Failed;
            }

            string output = outputTask.Result;
            string error  = errorTask.Result;

            if (process.ExitCode != 0
                // A query for a task that does not exist is an ordinary answer, not a fault.
                && !arguments.StartsWith("/Query", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Warn(nameof(StartupTask),
                    $"schtasks failed ({process.ExitCode}): {arguments} :: {error.Trim()}");
            }

            // It ran and it answered. Whether the answer was yes is the caller's business.
            return new Reply(true, process.ExitCode, output);
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(StartupTask), $"Could not run schtasks: {arguments}", ex);
            return Reply.Failed;
        }
    }
}
