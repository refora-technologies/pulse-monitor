using System.Windows.Input;

namespace Pulse.Models;

/// <summary>
/// The three things a keyboard shortcut can do.
/// </summary>
/// The names are stored in settings, so they are part of the file format and must not be
/// renamed casually.
public enum ShortcutAction
{
    ToggleOverlay,
    ToggleControlPanel,
    ToggleCompactMode,
}

/// <summary>
/// A key combination, and the rules about which ones Pulse is willing to take.
///
/// These are registered as global hotkeys, which means Windows delivers them to Pulse and the
/// application the user is actually working in never sees them at all. That is the whole point
/// — it has to work while a game is in front — and it is also why the rules below are strict.
/// Taking a combination that other software needs breaks that software everywhere, for as long
/// as Pulse is running, and the person affected has no reason to suspect us.
/// </summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    public static readonly Shortcut None = new(ModifierKeys.None, Key.None);

    public bool IsSet => Key != Key.None;

    /// <summary>
    /// Why this combination cannot be used, or null when it can.
    /// </summary>
    /// <remarks>
    /// Checked when the user presses the keys rather than when registration is attempted.
    /// Accepting a combination and then failing to register it teaches people that the feature
    /// is unreliable, when in fact it was never going to work.
    /// </remarks>
    public string? Rejection()
    {
        if (!IsSet) return null;   // "not set" is a valid state, not a bad combination

        // A global hotkey with no modifier takes that key away from every application on the
        // machine. Pressing "P" would stop typing the letter P anywhere.
        if (Modifiers == ModifierKeys.None)
            return "Add Alt, Ctrl, Shift or the Windows key — a plain key would stop working everywhere else.";

        // Ctrl+Alt is how AltGr arrives on European keyboards, where it types the characters
        // in the third position on a key: @ on a German layout, or the accented letters on a
        // French one. Taking a Ctrl+Alt combination stops one of those being typed at all.
        if (Modifiers.HasFlag(ModifierKeys.Control) && Modifiers.HasFlag(ModifierKeys.Alt))
            return "Ctrl+Alt is AltGr on many keyboards, and this would stop it typing that character.";

        if (IsModifierKey(Key))
            return "Hold the modifiers and press another key as well.";

        if (Reserved.Contains(Key) || (Modifiers.HasFlag(ModifierKeys.Windows) && WindowsOwns.Contains(Key)))
            return "Windows keeps this combination for itself and will not pass it to Pulse.";

        return null;
    }

    /// Whether a key is only a modifier, which is never a combination on its own.
    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or
        Key.System or Key.None;

    /// Combinations Windows intercepts before any application sees them. Registering these
    /// fails, and on some of them it fails silently, which is worse.
    private static readonly HashSet<Key> Reserved = new()
    {
        Key.LWin, Key.RWin, Key.Apps, Key.Sleep,
        Key.PrintScreen, Key.Cancel,
    };

    /// Keys the Windows key already claims. Win+L locks the machine and cannot be taken.
    private static readonly HashSet<Key> WindowsOwns = new()
    {
        Key.L, Key.G, Key.D, Key.E, Key.R, Key.X, Key.Tab, Key.Space,
    };

    /// <summary>
    /// The text form stored in settings and shown in the panel, e.g. "Alt+Shift+X".
    ///
    /// Deliberately readable rather than a packed number: someone opening settings.json should
    /// be able to see what their shortcuts are, and fix one by hand if it comes to that.
    /// </summary>
    public override string ToString()
    {
        if (!IsSet) return "";

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));

        return string.Join("+", parts);
    }

    /// How a key is written. The enum names are close enough for letters and digits and quite
    /// wrong for the rest: "D1" is the 1 key and "OemComma" is a comma.
    public static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.OemComma        => ",",
        Key.OemPeriod       => ".",
        Key.OemMinus        => "-",
        Key.OemPlus         => "+",
        Key.OemQuestion     => "/",
        Key.OemTilde        => "`",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets=> "]",
        Key.OemSemicolon    => ";",
        Key.OemQuotes       => "'",
        Key.OemBackslash or Key.OemPipe => "\\",
        Key.Return          => "Enter",
        Key.Next            => "PageDown",
        Key.Prior           => "PageUp",
        _                   => key.ToString(),
    };

    /// <summary>
    /// Reads the text form back, returning None for anything unrecognised.
    /// </summary>
    /// <remarks>
    /// Defensive on purpose. This parses a value out of a file a user can edit, and the only
    /// safe failure is "no shortcut": a half-understood combination would register something
    /// nobody chose. A rejected combination also comes back as None, so a settings file
    /// written by hand cannot install a binding the panel would have refused.
    /// </remarks>
    public static Shortcut Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        var modifiers = ModifierKeys.None;
        var key = Key.None;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) return None;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                case "alt":               modifiers |= ModifierKeys.Alt;     continue;
                case "shift":             modifiers |= ModifierKeys.Shift;   continue;
                case "win" or "windows":  modifiers |= ModifierKeys.Windows; continue;
            }

            // Two keys named in one combination is not something we would ever write.
            if (key != Key.None) return None;

            key = ParseKey(part);
            if (key == Key.None) return None;
        }

        var shortcut = new Shortcut(modifiers, key);
        return shortcut.Rejection() == null ? shortcut : None;
    }

    private static Key ParseKey(string part)
    {
        switch (part)
        {
            case ",":  return Key.OemComma;
            case ".":  return Key.OemPeriod;
            case "-":  return Key.OemMinus;
            case "+":  return Key.OemPlus;
            case "/":  return Key.OemQuestion;
            case "`":  return Key.OemTilde;
            case "[":  return Key.OemOpenBrackets;
            case "]":  return Key.OemCloseBrackets;
            case ";":  return Key.OemSemicolon;
            case "'":  return Key.OemQuotes;
            case "\\": return Key.OemPipe;
        }

        if (part.Length == 1 && part[0] >= '0' && part[0] <= '9')
            return Key.D0 + (part[0] - '0');

        if (part.StartsWith("Num", StringComparison.OrdinalIgnoreCase)
            && part.Length == 4 && part[3] >= '0' && part[3] <= '9')
            return Key.NumPad0 + (part[3] - '0');

        if (string.Equals(part, "Enter", StringComparison.OrdinalIgnoreCase))    return Key.Return;
        if (string.Equals(part, "PageDown", StringComparison.OrdinalIgnoreCase)) return Key.Next;
        if (string.Equals(part, "PageUp", StringComparison.OrdinalIgnoreCase))   return Key.Prior;

        return Enum.TryParse<Key>(part, ignoreCase: true, out var parsed) ? parsed : Key.None;
    }

    /// <summary>
    /// What Pulse suggests, pre-filled in the panel but not registered until the feature is
    /// switched on.
    /// </summary>
    /// <remarks>
    /// Alt+Shift rather than plain Alt, because Alt+letter is the menu accelerator in
    /// essentially every Windows program: taking Alt+V would break "View" menus system-wide.
    /// Not Ctrl+Alt, which is AltGr. Not Ctrl+Shift, where V is paste-as-plain-text and C is
    /// the browser inspector. Alt+Shift is rarely bound, and still reachable one-handed from
    /// near the movement keys, which is the point for a tool used while playing.
    ///
    /// Alt+Shift pressed alone switches keyboard layout when several are installed; adding a
    /// letter is a different chord, so that does not apply here.
    /// </remarks>
    public static Shortcut Default(ShortcutAction action) => action switch
    {
        ShortcutAction.ToggleOverlay      => new(ModifierKeys.Alt | ModifierKeys.Shift, Key.X),
        ShortcutAction.ToggleControlPanel => new(ModifierKeys.Alt | ModifierKeys.Shift, Key.V),
        ShortcutAction.ToggleCompactMode  => new(ModifierKeys.Alt | ModifierKeys.Shift, Key.C),
        _ => None,
    };

    /// What the row is called in the panel.
    public static string Describe(ShortcutAction action) => action switch
    {
        ShortcutAction.ToggleOverlay      => "Show or hide the overlay",
        ShortcutAction.ToggleControlPanel => "Open or close the control panel",
        ShortcutAction.ToggleCompactMode  => "Switch compact mode",
        _ => "",
    };
}
