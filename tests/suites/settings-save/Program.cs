// Tests that a failure to write settings is reported rather than swallowed.
//
// It used to be swallowed. Save logged and returned nothing, so a change applied in memory,
// the panel showed it, and it was gone at the next launch with no explanation. "Pulse keeps
// forgetting my settings" is not something anyone could diagnose from the outside.
//
// The failure is produced for real, by making the settings file unwritable, rather than by
// mocking anything. The user's own settings are backed up first and put back afterwards, and
// the last check in this suite is that they came back.

using System;
using System.IO;
using System.Reflection;

class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }

        var asm = Assembly.LoadFrom(dll);
        var settingsType = asm.GetType("Pulse.Models.AppSettings")!;
        var save = settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance)!;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Refora", "Pulse", "settings.json");

        Console.WriteLine("Settings save\n");
        Console.WriteLine("A save must report whether it actually happened:");

        Check("Save returns a result rather than nothing",
              save.ReturnType == typeof(bool),
              save.ReturnType.Name);

        // Everything below writes to the real settings file, so it is copied aside first and
        // restored at the end whatever happens.
        string? backup = null;
        if (File.Exists(path))
        {
            backup = path + ".savetest-backup";
            File.Copy(path, backup, overwrite: true);
        }

        try
        {
            var loaded = settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!
                                     .Invoke(null, null)!;

            Check("a normal save succeeds", (bool)save.Invoke(loaded, null)!, path);

            // ── the failure that used to be invisible ───────────────────────────────
            Console.WriteLine();
            Console.WriteLine("When the file cannot be written, that must be reported:");

            if (!File.Exists(path))
            {
                Console.WriteLine("    (skipped: no settings file to make unwritable)");
            }
            else
            {
                // Snapshotted here, not from the backup taken at the top: a successful save has
                // already rewritten the file since then, so comparing against the backup would
                // be measuring that rewrite rather than the failure.
                var before = File.ReadAllText(path);

                // Read only is enough: File.Replace cannot overwrite it.
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    bool result = (bool)save.Invoke(loaded, null)!;
                    Check("Save reports failure", !result,
                          result ? "it claimed success against a read only file" : "");

                    var after = File.ReadAllText(path);
                    Check("and the existing settings are left intact", before == after,
                          "a failed save must not damage what was already there");
                }
                finally
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Check("saving works again once the file is writable",
                      (bool)save.Invoke(loaded, null)!,
                      "the failure must not be sticky");
            }

            // ── the service carries that up to the panel ────────────────────────────
            Console.WriteLine();
            Console.WriteLine("The panel must be able to see it:");

            var service = asm.GetType("Pulse.Services.SettingsService")!;
            Check("the service exposes whether the last save failed",
                  service.GetProperty("LastSaveFailed") != null);
            Check("and reports a change of that state",
                  service.GetEvent("SaveStateChanged") != null,
                  "so the warning can appear and disappear on its own");

            var vm = asm.GetType("Pulse.ViewModels.SettingsViewModel")!;
            Check("the view model offers a warning to bind to",
                  vm.GetProperty("HasSaveWarning") != null && vm.GetProperty("SaveWarning") != null);
        }
        finally
        {
            if (backup != null && File.Exists(backup))
            {
                try
                {
                    File.Copy(backup, path, overwrite: true);
                    File.Delete(backup);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING  could not restore your settings: {ex.Message}");
                }
            }
        }

        Console.WriteLine();
        var restoredOk = backup == null || !File.Exists(backup);
        Check("your own settings were put back", restoredOk,
              restoredOk ? "" : "a backup is still on disk beside settings.json");

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
