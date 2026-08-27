using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Models;

namespace Pulse.Services;

public class SettingsService
{
    /// <summary>
    /// Thread-safe by construction.
    ///
    /// A plain `??=` is not atomic, and startup reaches this from two threads at once: the
    /// UI thread building the services while a background task reconciles the startup task.
    /// Losing that race produced *two* settings services — each with its own SettingsChanged
    /// subscribers — so the overlay could end up listening to an object nothing ever
    /// notified, and changes would silently stop being applied.
    /// </summary>
    private static readonly Lazy<SettingsService> LazyInstance =
        new(() => new SettingsService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static SettingsService Instance => LazyInstance.Value;

    public AppSettings Settings { get; private set; } = AppSettings.Load();

    public event EventHandler? SettingsChanged;

    public void Save()
    {
        Settings.Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns whether the change actually took effect. The stored setting now reflects what
    /// Windows really did rather than what was asked for, so the toggle cannot sit there
    /// claiming Pulse starts with Windows when creating the task failed.
    /// </summary>
    public bool UpdateStartWithWindows(bool enabled)
    {
        // Pulse runs elevated, so an HKCU Run entry would trigger a UAC prompt on every
        // logon. A scheduled task with highest privileges starts it silently instead.
        RemoveLegacyRunEntry();

        bool succeeded = enabled
            ? StartupTask.Install(Environment.ProcessPath ?? "")
            : StartupTask.Remove();

        Settings.StartWithWindows = succeeded ? enabled : !enabled;
        Save();
        return succeeded;
    }

    /// <summary>
    /// Reconciles settings.json with the real scheduled task, and repairs the task when it
    /// points at a build that is no longer here.
    ///
    /// Every version of Pulse shares one task name and the task stores an absolute exe path,
    /// so whichever build last had the toggle switched on owned startup permanently — even
    /// after being replaced or deleted. Users saw an old version launching at logon, or
    /// nothing launching at all, while Pulse still reported startup as enabled.
    /// </summary>
    /// Safe to call from a background thread, and meant to be: it shells out to schtasks
    /// twice, which has no business sitting on the startup path. Returns true when the
    /// caller should Save() — done by the caller so SettingsChanged still fires on the UI
    /// thread, where its subscribers touch bound collections.
    public bool ReconcileStartupTask()
    {
        var task    = StartupTask.Query();
        var current = Environment.ProcessPath ?? "";

        if (task.Exists && current.Length > 0)
        {
            // Two reasons to rewrite an existing task. It may point at a build that is no
            // longer here — every version shares one task name and stores an absolute path,
            // so whichever build last had the toggle on owned startup permanently. Or it may
            // carry the schtasks defaults that stop it running on battery and kill Pulse
            // after three days, which is every task Pulse created before 1.1.1.
            bool wrongPath = task.CommandPath.IndexOf(current, StringComparison.OrdinalIgnoreCase) < 0;

            if (wrongPath || !task.SettingsCorrect)
            {
                LogService.Info(nameof(SettingsService),
                    $"Repairing startup task (wrongPath={wrongPath}, badSettings={!task.SettingsCorrect}).");

                if (StartupTask.Install(current)) task = StartupTask.Query();
            }
        }

        // Deliberately does not create a missing task. Absent means the user turned startup
        // off, or never turned it on; recreating it here would override that silently.
        if (Settings.StartWithWindows == task.Exists) return false;

        Settings.StartWithWindows = task.Exists;
        return true;
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("PulseMonitor", false);
        }
        catch { }
    }
}
