using System.IO;

namespace Pulse.Models;

// The Windows half of the settings (the shared half is AppSettings.cs): where settings.json lives,
// reading it patiently and saving it safely, and the keyboard shortcuts, whose key names are WPF's.
public partial class AppSettings
{
    // %APPDATA%\Refora\Pulse in Pulse; a folder of their own for the test suites (DataFolder).
    internal static readonly string SettingsPath = Path.Combine(Pulse.Services.DataFolder.Root, "settings.json");

    private static string BackupPath => SettingsPath + ".bak";

    /// <summary>
    /// Reads the saved settings, falling back to the backup, then to defaults.
    /// </summary>
    /// <remarks>
    /// Three different reasons for having no settings, and only two of them are safe to write
    /// over. A missing file is a first run. A file that is there but is not valid JSON has
    /// nothing in it to protect. But a file that is there and could not be read right now, which
    /// is most likely at logon when a scanner or a slow disk has it, still holds everything the
    /// user set up. Pulse used to treat that the same as a first run: the startup task check
    /// then saved, which moved the real file to the backup and wrote defaults in its place, and
    /// the next save overwrote the backup too.
    ///
    /// So an unreadable file is asked again a couple of times, briefly. If it still cannot be
    /// read, Pulse runs on defaults with ReadFailed set, and SettingsService refuses to save
    /// them. Losing this session's changes is recoverable; losing the saved ones is not.
    /// </remarks>
    public static AppSettings Load()
    {
        // The backup is only reached if the main file is missing or unreadable, which is
        // what an interrupted write leaves behind.
        var (main, mainState) = TryLoadPatiently(SettingsPath);
        if (main != null) return main;

        var (backup, backupState) = TryLoadPatiently(BackupPath);
        if (backup != null) return backup;

        var defaults = new AppSettings
        {
            ReadFailed = mainState == ReadState.Unreadable || backupState == ReadState.Unreadable,
        };

        if (defaults.ReadFailed)
            Services.LogService.Warn(nameof(AppSettings),
                "Saved settings exist but could not be read. Running on defaults, which will not be saved over them.");

        return defaults;
    }

    internal enum ReadState { Missing, Read, Corrupt, Unreadable }

    private static (AppSettings? Settings, ReadState State) TryLoadPatiently(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            var result = TryLoad(path);
            if (result.State != ReadState.Unreadable || attempt == 3) return result;

            // Short, because this is on the way to the first window. At logon a file held by a
            // scanner is usually released in well under a second.
            Thread.Sleep(250 * attempt);
        }
    }

    internal static (AppSettings? Settings, ReadState State) TryLoad(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return (null, ReadState.Missing);
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Worth recording: this is the path where a user silently loses every
            // preference, and without a trace it looks like Pulse simply forgot them.
            Services.LogService.Error(nameof(AppSettings), $"Could not read settings from {path}", ex);
            return (null, ReadState.Unreadable);
        }

        try
        {
            var settings = FromJson(text);
            return settings is null ? (null, ReadState.Corrupt) : (settings, ReadState.Read);
        }
        catch (Exception ex)
        {
            Services.LogService.Error(nameof(AppSettings), $"Settings in {path} could not be understood", ex);
            return (null, ReadState.Corrupt);
        }
    }

    /// <summary>
    /// Writes via a temporary file and File.Replace, so a crash or power loss mid-write
    /// cannot leave a truncated settings file — previously the only copy was overwritten in
    /// place, and losing it reset every preference silently.
    /// </summary>
    /// <returns>
    /// Whether the file was actually written. This used to return nothing, so a failure was
    /// logged and then dropped: the change applied in memory, the panel showed it, and it was
    /// gone at the next launch with no explanation. "Pulse keeps forgetting my settings" is not
    /// something anyone could have diagnosed from the outside.
    /// </returns>
    public bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);

            var json = ToJson();
            var temp = SettingsPath + ".tmp";

            File.WriteAllText(temp, json);

            if (File.Exists(SettingsPath))
                File.Replace(temp, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, SettingsPath);

            return true;
        }
        catch (Exception ex)
        {
            Services.LogService.Error(nameof(AppSettings), "Could not save settings", ex);
            return false;
        }
    }

    /// <summary>
    /// The keyboard shortcuts' part of the repair (see Sanitise).
    /// </summary>
    partial void SanitisePlatform()
    {
        // A shortcut that cannot be read, or that names a combination Pulse would have refused
        // if it had been typed in, becomes unset rather than being repaired into something
        // nobody chose. Rewritten from the parsed value so the stored text is normalised too:
        // "shift + alt + x" saved by hand comes back as "Alt+Shift+X".
        foreach (var action in new[] { ShortcutAction.ToggleOverlay,
                                       ShortcutAction.ToggleControlPanel,
                                       ShortcutAction.ToggleCompactMode })
        {
            SetShortcut(action, ShortcutFor(action));
        }

        // Two rows holding the same combination cannot both work: whichever registers second
        // is refused by Windows. The later row loses it, matching what the panel does when the
        // same thing is assigned twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in new[] { ShortcutAction.ToggleOverlay,
                                       ShortcutAction.ToggleControlPanel,
                                       ShortcutAction.ToggleCompactMode })
        {
            var text = ShortcutFor(action).ToString();
            if (text.Length > 0 && !seen.Add(text)) SetShortcut(action, Shortcut.None);
        }
    }

    /// The parsed combination for an action. Anything unreadable comes back as unset.
    public Shortcut ShortcutFor(ShortcutAction action) => Shortcut.Parse(action switch
    {
        ShortcutAction.ToggleOverlay      => ShortcutToggleOverlay,
        ShortcutAction.ToggleControlPanel => ShortcutToggleControlPanel,
        ShortcutAction.ToggleCompactMode  => ShortcutToggleCompactMode,
        _ => "",
    });

    public void SetShortcut(ShortcutAction action, Shortcut shortcut)
    {
        var text = shortcut.ToString();
        switch (action)
        {
            case ShortcutAction.ToggleOverlay:      ShortcutToggleOverlay      = text; break;
            case ShortcutAction.ToggleControlPanel: ShortcutToggleControlPanel = text; break;
            case ShortcutAction.ToggleCompactMode:  ShortcutToggleCompactMode  = text; break;
        }
    }
}
