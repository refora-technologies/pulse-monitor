// Tests the global keyboard shortcuts.
//
// A global hotkey is not an ordinary preference. Windows delivers it to Pulse and the
// application the user is actually working in never sees those keys at all, for as long as
// Pulse runs. So the interesting questions here are not "does it fire" but "what does it
// refuse to take", "what happens when another program already owns it", and "does it give the
// keys back". A shortcut that silently does nothing is the failure this feature cannot afford,
// because it is indistinguishable from a broken application.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

class Program
{
    static int _pass, _fail;
    static Assembly _asm;
    static Type _shortcut, _action, _settings;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    [STAThread]
    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }

        _asm      = Assembly.LoadFrom(dll);
        _shortcut = _asm.GetType("Pulse.Models.Shortcut");
        _action   = _asm.GetType("Pulse.Models.ShortcutAction");
        _settings = _asm.GetType("Pulse.Models.AppSettings");

        if (_shortcut == null || _action == null) { Console.WriteLine("Shortcut types missing"); return 2; }

        Console.WriteLine("Keyboard shortcuts\n");

        WhatWeRefuseToTake();
        ReadingAndWriting();
        TheDefaults();
        SettingsAreRepairedNotTrusted();
        RegisteringWithWindows();
        TheCaptureControl();
        PressingItForReal();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ── helpers over the real types ─────────────────────────────────────────────────

    static object Make(ModifierKeys modifiers, Key key)
        => Activator.CreateInstance(_shortcut, new object[] { modifiers, key });

    static string Rejection(object shortcut)
        => (string)_shortcut.GetMethod("Rejection").Invoke(shortcut, null);

    static object Parse(string text)
        => _shortcut.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { text });

    static object DefaultFor(string actionName)
        => _shortcut.GetMethod("Default", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { Enum.Parse(_action, actionName) });

    static bool IsSet(object shortcut) => (bool)_shortcut.GetProperty("IsSet").GetValue(shortcut);

    static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        return "";
    }

    // ── the combinations Pulse will not take ────────────────────────────────────────
    static void WhatWeRefuseToTake()
    {
        Console.WriteLine("A combination that would break other software must be refused:");

        // No modifier at all. This is the one that matters most: registering a bare letter
        // globally stops that letter being typed anywhere on the machine.
        var bare = Make(ModifierKeys.None, Key.P);
        Check("a plain key is refused", Rejection(bare) != null,
              "a global bare P would stop the letter P working everywhere");

        Check("and the reason says why",
              Rejection(bare)?.Contains("everywhere") == true,
              Rejection(bare));

        // Ctrl+Alt is AltGr on European layouts, where it types the third character on a key.
        var altGr = Make(ModifierKeys.Control | ModifierKeys.Alt, Key.Q);
        Check("Ctrl+Alt is refused", Rejection(altGr) != null,
              "Ctrl+Alt is AltGr, and this is @ on a German layout");

        Check("the reason names AltGr", Rejection(altGr)?.Contains("AltGr") == true,
              "so the refusal does not look arbitrary");

        // Modifiers alone are not a chord, they are a chord in progress.
        foreach (var modifier in new[] { Key.LeftAlt, Key.LeftShift, Key.LeftCtrl, Key.LWin })
            Check($"{modifier} on its own is not a combination",
                  Rejection(Make(ModifierKeys.Alt, modifier)) != null);

        // Windows keeps some for itself and will not hand them over whatever we ask.
        Check("Win+L is refused", Rejection(Make(ModifierKeys.Windows, Key.L)) != null,
              "Windows locks the machine and never delivers it");

        Check("PrintScreen is refused", Rejection(Make(ModifierKeys.Alt, Key.PrintScreen)) != null);

        // And the ones that are fine.
        foreach (var (modifiers, key, label) in new[]
                 {
                     (ModifierKeys.Alt | ModifierKeys.Shift, Key.X, "Alt+Shift+X"),
                     (ModifierKeys.Control | ModifierKeys.Shift, Key.F9, "Ctrl+Shift+F9"),
                     (ModifierKeys.Alt, Key.F12, "Alt+F12"),
                 })
            Check($"{label} is accepted", Rejection(Make(modifiers, key)) == null);

        // "Not set" is a state the user can choose, not a bad combination.
        Check("unset is not an error", Rejection(Make(ModifierKeys.None, Key.None)) == null,
              "clearing a row is a deliberate choice");
    }

    // ── the stored form ─────────────────────────────────────────────────────────────
    static void ReadingAndWriting()
    {
        Console.WriteLine();
        Console.WriteLine("The stored form must be readable and must survive a round trip:");

        foreach (var (modifiers, key, text) in new[]
                 {
                     (ModifierKeys.Alt | ModifierKeys.Shift, Key.X, "Alt+Shift+X"),
                     (ModifierKeys.Control | ModifierKeys.Shift, Key.F9, "Ctrl+Shift+F9"),
                     (ModifierKeys.Alt, Key.D1, "Alt+1"),
                     (ModifierKeys.Alt | ModifierKeys.Shift, Key.OemComma, "Alt+Shift+,"),
                 })
        {
            var built = Make(modifiers, key);
            Check($"{text} is written as itself", built.ToString() == text, built.ToString());
            Check($"{text} reads back the same", Parse(text).Equals(built), Parse(text).ToString());
        }

        // Every key the panel is willing to take, not a handful of representative ones.
        //
        // Two of them could not survive being written down, and both were found by reading
        // rather than by anything failing. The plus key wrote itself as "Alt++", which split
        // back into one part and came home unset, so a user who chose it lost it silently. And
        // OemBackslash and OemPipe both wrote themselves as "\\" and both read back as
        // OemPipe, so somebody on a European keyboard binding the key beside their left shift
        // got one bound to the key above their Enter instead.
        var brokenKeys = new List<string>();
        int accepted = 0;

        foreach (Key key in Enum.GetValues(typeof(Key)))
        {
            var candidate = Make(ModifierKeys.Alt | ModifierKeys.Shift, key);
            if (!IsSet(candidate) || Rejection(candidate) != null) continue;

            accepted++;

            var written = candidate.ToString() ?? "";
            var read    = Parse(written);

            if (!read.Equals(candidate) && brokenKeys.Count < 8)
                brokenKeys.Add($"{key} wrote \"{written}\" and read back as {read}");
        }

        Check("every key the panel accepts survives being written and read back",
              brokenKeys.Count == 0,
              brokenKeys.Count == 0
                  ? $"{accepted} keys, each one written to settings and parsed again"
                  : string.Join("; ", brokenKeys));

        // Named individually as well, so a regression says which key rather than a count.
        Check("the plus key survives, whose name is also the separator",
              Parse("Alt+Shift++").Equals(Make(ModifierKeys.Alt | ModifierKeys.Shift, Key.OemPlus)),
              "splitting on + used to drop it and leave the shortcut unset");

        Check("and the two backslash keys are told apart",
              !Make(ModifierKeys.Alt | ModifierKeys.Shift, Key.OemBackslash).ToString()!
                  .Equals(Make(ModifierKeys.Alt | ModifierKeys.Shift, Key.OemPipe).ToString()),
              "one is beside the left shift on a European keyboard, the other is above Enter");

        Check("a digit is not written as its enum name",
              Make(ModifierKeys.Alt, Key.D1).ToString() == "Alt+1",
              "D1 would be meaningless to anyone reading settings.json");

        // A user editing the file by hand.
        Check("spacing and case are tolerated",
              Parse(" shift + alt + x ").Equals(Make(ModifierKeys.Alt | ModifierKeys.Shift, Key.X)),
              "and normalised on the way back out");

        Console.WriteLine();
        Console.WriteLine("Anything unreadable becomes unset rather than something nobody chose:");

        foreach (var bad in new[] { "", "   ", "Alt+", "Alt+Nonsense", "Alt+X+Y", "++", "Frobnicate" })
            Check($"\"{bad}\" reads as unset", !IsSet(Parse(bad)));

        // The important one: a combination the panel would have refused must not become active
        // just because someone wrote it into the file.
        foreach (var refused in new[] { "P", "Ctrl+Alt+Q", "Win+L" })
            Check($"\"{refused}\" cannot be installed by hand", !IsSet(Parse(refused)),
                  "the file is not a way around the rules");
    }

    // ── what Pulse suggests ─────────────────────────────────────────────────────────
    static void TheDefaults()
    {
        Console.WriteLine();
        Console.WriteLine("The suggested combinations must be ones worth suggesting:");

        var defaults = new Dictionary<string, string>();
        foreach (var name in new[] { "ToggleOverlay", "ToggleControlPanel", "ToggleCompactMode" })
        {
            var value = DefaultFor(name).ToString();
            defaults[name] = value;
            Check($"{name} suggests {value}", value.Length > 0);
        }

        Check("all three are Alt+Shift",
              defaults.Values.All(v => v.StartsWith("Alt+Shift+")),
              string.Join(", ", defaults.Values));

        Check("none is a plain Alt combination",
              defaults.Values.All(v => !System.Text.RegularExpressions.Regex.IsMatch(v, @"^Alt\+[A-Z0-9]$")),
              "Alt+letter is the menu accelerator in nearly every Windows program");

        Check("and no two are the same",
              defaults.Values.Distinct().Count() == 3,
              "Windows refuses the second registration of a combination");

        foreach (var value in defaults.Values)
            Check($"{value} passes our own rules", Rejection(Parse(value)) == null && IsSet(Parse(value)),
                  "a suggestion the panel would refuse would be absurd");

        Console.WriteLine();
        Console.WriteLine("But nothing is registered until asked for:");

        var fresh = Activator.CreateInstance(_settings);

        Check("shortcuts are off out of the box",
              (bool)_settings.GetProperty("ShortcutsEnabled").GetValue(fresh) == false,
              "a global shortcut takes keys from every other program on the machine");

        Check("while the suggestions are still filled in",
              (string)_settings.GetProperty("ShortcutToggleOverlay").GetValue(fresh) == "Alt+Shift+X",
              "so the feature is discoverable without having taken anything");
    }

    // ── a settings file is not to be trusted ────────────────────────────────────────
    static void SettingsAreRepairedNotTrusted()
    {
        Console.WriteLine();
        Console.WriteLine("A hand-edited settings file must not be able to install a bad binding:");

        var sanitise = _settings.GetMethod("Sanitise", BindingFlags.NonPublic | BindingFlags.Instance);
        Check("there is a repair step", sanitise != null);
        if (sanitise == null) return;

        object Settings(string overlay, string panel, string compact)
        {
            var s = Activator.CreateInstance(_settings);
            _settings.GetProperty("ShortcutToggleOverlay").SetValue(s, overlay);
            _settings.GetProperty("ShortcutToggleControlPanel").SetValue(s, panel);
            _settings.GetProperty("ShortcutToggleCompactMode").SetValue(s, compact);
            sanitise.Invoke(s, null);
            return s;
        }

        string Read(object s, string property) => (string)_settings.GetProperty(property).GetValue(s);

        var repaired = Settings("Ctrl+Alt+Q", "garbage here", "shift+alt+c");

        Check("an AltGr combination is dropped",
              Read(repaired, "ShortcutToggleOverlay") == "",
              $"was Ctrl+Alt+Q, now \"{Read(repaired, "ShortcutToggleOverlay")}\"");

        Check("an unreadable value is dropped",
              Read(repaired, "ShortcutToggleControlPanel") == "");

        Check("and a valid one is kept, normalised",
              Read(repaired, "ShortcutToggleCompactMode") == "Alt+Shift+C",
              "written back the way the panel would show it");

        // Two rows holding the same keys cannot both work: Windows refuses the second one, so
        // one of them would simply stop working with nothing to explain it.
        var duplicated = Settings("Alt+Shift+X", "Alt+Shift+X", "Alt+Shift+C");

        Check("the first row keeps a duplicated combination",
              Read(duplicated, "ShortcutToggleOverlay") == "Alt+Shift+X");

        Check("and the second loses it",
              Read(duplicated, "ShortcutToggleControlPanel") == "",
              "rather than both rows claiming keys only one can have");

        Check("an unrelated row is untouched",
              Read(duplicated, "ShortcutToggleCompactMode") == "Alt+Shift+C");

        // Clearing one row must not disturb the others.
        var partly = Settings("", "Alt+Shift+V", "");
        Check("rows are independent",
              Read(partly, "ShortcutToggleControlPanel") == "Alt+Shift+V"
              && Read(partly, "ShortcutToggleOverlay") == "",
              "clearing one leaves the rest working");
    }

    // ── actually taking them from Windows ───────────────────────────────────────────
    static void RegisteringWithWindows()
    {
        Console.WriteLine();
        Console.WriteLine("Registration must be real, and must be given back:");

        var service = _asm.GetType("Pulse.Services.HotkeyService");
        Check("the service exists", service != null);
        if (service == null) return;

        var source = Source("Services/HotkeyService.cs");

        Check("holding the key does not repeat the action",
              source.Contains("ModNoRepeat = 0x4000") && source.Contains("| ModNoRepeat"),
              "without it, holding the overlay key makes it strobe");

        Check("a message-only window receives the presses",
              source.Contains("ParentWindow = new IntPtr(-3)"),
              "so the shortcuts work with no Pulse window open, which is most of the time");

        Check("a combination another program owns is reported, not swallowed",
              source.Contains("error == 1409") && source.Contains("already used by another program"),
              "silence here is what makes shortcut features feel broken");

        Check("and the failure is per shortcut, not for all of them",
              source.Contains("_failures[action]") || source.Contains("failures[action] ="),
              "one taken combination must not disable the other two");

        Check("everything is released on the way out",
              source.Contains("UnregisterAll();") && source.Contains("public void Dispose()"),
              "Windows holds a combination for as long as the handle lives");

        Check("our own shortcuts are released while recording a new one",
              source.Contains("public void Suspend()") && source.Contains("public void Resume()"),
              "otherwise pressing the current combination performs the action instead");

        // The real thing, against the live Windows API. Registered, then registered again from
        // the same process to prove the first one actually took, then released.
        var register = service.GetMethod("RegisterHotKey", BindingFlags.NonPublic | BindingFlags.Static);
        var unregister = service.GetMethod("UnregisterHotKey", BindingFlags.NonPublic | BindingFlags.Static);

        if (register == null || unregister == null)
        {
            Console.WriteLine("    (skipped: the Windows entry points are not reachable from here)");
            return;
        }

        // F24 with three modifiers, because this runs on a real desktop and must not fight
        // over anything a person or another program might plausibly be using.
        const uint modAlt = 0x0001, modControl = 0x0002, modShift = 0x0004, noRepeat = 0x4000;
        uint mods = modAlt | modControl | modShift | noRepeat;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(Key.F24);

        bool first = (bool)register.Invoke(null, new object[] { IntPtr.Zero, 0x4321, mods, vk });
        Check("a combination can be taken from Windows", first,
              "Alt+Ctrl+Shift+F24, chosen so nothing real is disturbed");

        if (first)
        {
            bool again = (bool)register.Invoke(null, new object[] { IntPtr.Zero, 0x4322, mods, vk });
            Check("and taking it twice fails, which is the case we report", !again,
                  "this is exactly what another program owning it looks like");

            if (again) unregister.Invoke(null, new object[] { IntPtr.Zero, 0x4322 });

            bool released = (bool)unregister.Invoke(null, new object[] { IntPtr.Zero, 0x4321 });
            Check("releasing it works", released);

            bool retaken = (bool)register.Invoke(null, new object[] { IntPtr.Zero, 0x4323, mods, vk });
            Check("and it can be taken again afterwards", retaken,
                  "which proves the release was real and not merely reported");

            if (retaken) unregister.Invoke(null, new object[] { IntPtr.Zero, 0x4323 });
        }
    }

    static object _fired;

    /// Subscribed to Pulse's Pressed event through a closed generic, since the argument type
    /// is an enum that only exists in the assembly under test.
    static void Record<T>(object sender, T action) => _fired = action;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    const uint KeyUp = 0x0002;

    /// Injects a key press with its scan code filled in. Passing zero for the scan code is
    /// accepted by the API and ignored by some of the input stack, which is enough for the
    /// press never to become a hotkey.
    static void Press(byte vk, bool up)
        => keybd_event(vk, (byte)MapVirtualKey(vk, 0), up ? KeyUp : 0, IntPtr.Zero);

    /// <summary>
    /// Presses one of Pulse's own shortcuts and checks the action is raised.
    /// </summary>
    /// <remarks>
    /// Everything above tests the pieces. This tests the one thing that cannot be inferred from
    /// them: that a message-only window, owned by no visible window, actually receives WM_HOTKEY
    /// and that the press reaches the right action. That is the whole mechanism the feature
    /// rests on, and it either works or the shortcuts silently do nothing.
    ///
    /// The settings object is changed in memory and never saved, so the machine's real
    /// shortcuts are not touched.
    /// </remarks>
    static void PressingItForReal()
    {
        Console.WriteLine();
        Console.WriteLine("Pressing the keys must actually reach the action:");

        var serviceType = _asm.GetType("Pulse.Services.HotkeyService");
        var settingsService = _asm.GetType("Pulse.Services.SettingsService");

        if (serviceType == null || settingsService == null) { Check("the services exist", false); return; }

        object live;
        try
        {
            live = settingsService.GetProperty("Instance").GetValue(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    (skipped: settings could not be loaded here: {ex.GetType().Name})");
            return;
        }

        var settings = settingsService.GetProperty("Settings").GetValue(live);

        // Read first, restored at the end. Nothing is written to disk either way.
        var enabledProperty = _settings.GetProperty("ShortcutsEnabled");
        var overlayProperty = _settings.GetProperty("ShortcutToggleOverlay");
        var panelProperty   = _settings.GetProperty("ShortcutToggleControlPanel");
        var compactProperty = _settings.GetProperty("ShortcutToggleCompactMode");

        var wasEnabled = enabledProperty.GetValue(settings);
        var wasOverlay = overlayProperty.GetValue(settings);
        var wasPanel   = panelProperty.GetValue(settings);
        var wasCompact = compactProperty.GetValue(settings);

        var service = serviceType.GetProperty("Instance").GetValue(null);

        try
        {
            // Alt+Shift+F24: nothing real uses F24, so a stray press cannot disturb the
            // machine running this. Deliberately not Ctrl+Alt+Shift, which is what this was
            // first written with — Pulse refuses Ctrl+Alt as AltGr, so it parsed to unset and
            // registered nothing, and the test failed for the right reason.
            enabledProperty.SetValue(settings, true);
            overlayProperty.SetValue(settings, "Alt+Shift+F24");
            panelProperty.SetValue(settings, "");
            compactProperty.SetValue(settings, "");

            _fired = null;

            // EventHandler<ShortcutAction> needs a delegate of exactly that type, and the enum
            // is only known at run time. A generic method closed over it has the right shape.
            var pressed = serviceType.GetEvent("Pressed");
            var closed = typeof(Program)
                .GetMethod(nameof(Record), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(_action);

            var typed = Delegate.CreateDelegate(pressed.EventHandlerType, closed, throwOnBindFailure: false);

            if (typed == null)
            {
                Console.WriteLine("    (skipped: the Pressed event could not be subscribed from here)");
                return;
            }

            pressed.AddEventHandler(service, typed);
            serviceType.GetMethod("Apply").Invoke(service, null);

            var failures = (System.Collections.IDictionary)serviceType.GetProperty("Failures").GetValue(service);

            if (failures.Count > 0)
            {
                Console.WriteLine("    (skipped: the test combination is already taken on this machine)");
                pressed.RemoveEventHandler(service, typed);
                return;
            }

            var handle = (IntPtr)serviceType.GetProperty("Handle",
                BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);

            Check("a listener window exists", handle != IntPtr.Zero,
                  "message-only, so no Pulse window has to be open");

            // Ctrl down, Alt down, Shift down, F24, and back up.
            const byte vkAlt = 0x12, vkShift = 0x10, vkF24 = 0x87;

            Press(vkAlt,   up: false);
            Press(vkShift, up: false);
            Press(vkF24,   up: false);
            Press(vkF24,   up: true);
            Press(vkShift, up: true);
            Press(vkAlt,   up: true);

            // WM_HOTKEY is posted, so the message loop has to run for it to arrive.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (_fired == null && DateTime.UtcNow < deadline)
            {
                // A real message pump. WM_HOTKEY is posted to the listener window, so it only
                // arrives once messages are dispatched; a Dispatcher.Invoke on this same
                // thread runs the queued delegate without pumping Win32 messages at all.
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }

            Check("the press reaches Pulse", _fired != null,
                  _fired != null ? "WM_HOTKEY arrived at the message-only window"
                                : "no WM_HOTKEY arrived within three seconds");

            if (_fired != null)
                Check("and is delivered as the right action",
                      _fired.ToString() == "ToggleOverlay",
                      _fired.ToString());

            // Suspending has to genuinely release it, or recording a new combination would
            // trigger the action the user is trying to change.
            serviceType.GetMethod("Suspend").Invoke(service, null);

            var register = serviceType.GetMethod("RegisterHotKey", BindingFlags.NonPublic | BindingFlags.Static);
            var unregister = serviceType.GetMethod("UnregisterHotKey", BindingFlags.NonPublic | BindingFlags.Static);

            uint mods = 0x0001 | 0x0004;   // Alt, Shift
            bool takenWhileSuspended = (bool)register.Invoke(null,
                new object[] { IntPtr.Zero, 0x4330, mods, (uint)vkF24 });

            Check("suspending really gives the combination back", takenWhileSuspended,
                  "so recording a new one cannot fire the old one");

            if (takenWhileSuspended) unregister.Invoke(null, new object[] { IntPtr.Zero, 0x4330 });

            pressed.RemoveEventHandler(service, typed);
        }
        finally
        {
            // The machine's real shortcuts, exactly as they were. Nothing was saved, so this
            // only has to undo the in-memory change.
            enabledProperty.SetValue(settings, wasEnabled);
            overlayProperty.SetValue(settings, wasOverlay);
            panelProperty.SetValue(settings, wasPanel);
            compactProperty.SetValue(settings, wasCompact);

            try { serviceType.GetMethod("Dispose").Invoke(service, null); } catch { }
        }
    }

    // ── the part that usually goes wrong ────────────────────────────────────────────
    static void TheCaptureControl()
    {
        Console.WriteLine();
        Console.WriteLine("Recording a combination must handle the awkward parts:");

        var window = Source("Views/MainWindow.xaml.cs");

        // Alt is held for all three suggested combinations, and WPF reports Alt+letter as
        // Key.System with the real key elsewhere. Reading Key alone records every Alt
        // combination as the same thing.
        Check("Alt combinations read the real key",
              window.Contains("e.Key == Key.System ? e.SystemKey : e.Key"),
              "otherwise every Alt chord records as System");

        Check("our own shortcuts are suspended while recording",
              window.Contains("HotkeyService.Instance.Suspend();"),
              "they fire before the panel sees the keys at all");

        Check("and restored afterwards",
              window.Contains("HotkeyService.Instance.Resume();"));

        Check("Escape cancels and keeps the previous binding",
              window.Contains("if (key == Key.Escape)"),
              "recording must be escapable without assigning something");

        Check("a modifier on its own keeps waiting",
              window.Contains("or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)"),
              "the user is part way through a valid combination");

        Check("a refusal shows against the row",
              window.Contains("if (problem != null) row.Problem = problem;"),
              "and leaves the previous binding alone");

        Check("losing focus ends recording",
              window.Contains("ShortcutChip_LostFocus"),
              "clicking away must not leave it listening forever");

        var vm = Source("ViewModels/SettingsViewModel.cs");

        Check("assigning the same keys to two rows is refused by name",
              vm.Contains("is already set to"),
              "naming the other row is the difference between a refusal and a mystery");

        Check("reset restores all three suggestions",
              vm.Contains("public void ResetShortcuts()") && vm.Contains("Shortcut.Default(action)"));

        var app = Source("App.xaml.cs");

        Check("the compact shortcut does not also unhide the overlay",
              app.Contains("Deliberately does not show the overlay"),
              "a key labelled switch compact mode should do one thing");

        Check("the panel shortcut brings a buried window forward",
              app.Contains("if (_mainWindow.IsActive)"),
              "a plain show/hide feels broken when the panel is behind a game");

        Check("and it is driven straight from the hotkey message",
              app.Contains("Windows grants foreground rights"),
              "which is what lets it genuinely come to the front");
    }
}
