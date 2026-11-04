// Tests Pulse's side of the in-game overlay: the block it writes for games, the rules for which
// programs the overlay may go into, and the settings that drive it.
//
// The block is read by C++ inside games, so a byte out of place shows a player the wrong number
// or nothing at all, and only in a game. Its layout is checked here against the C++ header's own
// static_asserts, so the two sides cannot drift apart without this failing.
//
// The anti-cheat rules are the ones that protect a player's account. When unsure they must say
// "anti-cheat": a game wrongly left on the window overlay costs nothing, a ban cannot be undone.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    static int _pass, _fail;
    static Assembly _asm = null!;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }
        _asm = Assembly.LoadFrom(dll);

        Console.WriteLine("In-game overlay\n");

        BlockMatchesTheHeader();
        BlockEncoding();
        TextIsCutWhole();
        AntiCheatRules();
        NotGames();
        WindowOverlayPlace();
        SettingsAreRepaired();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static Type T(string name) => _asm.GetType(name, true)!;
    static int Const(Type t, string name) => (int)t.GetField(name, Any)!.GetValue(null)!;

    // ── the block ────────────────────────────────────────────────────────────────

    static string Header()
    {
        // The suite runs from its own folder; the header is in the repository's InGame\src.
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var path = Path.Combine(dir.FullName, "InGame", "src", "pulse_ingame_shared.h");
                if (File.Exists(path)) return File.ReadAllText(path);
                dir = dir.Parent;
            }
        }
        return null;
    }

    static void BlockMatchesTheHeader()
    {
        var w = T("Pulse.Services.InGame.InGameBlockWriter");
        var h = Header();
        Check("the C++ header is found", h != null);
        if (h == null) return;

        int Assert(string pattern)
        {
            var m = Regex.Match(h, pattern);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        Check("tile size agrees", Assert(@"sizeof\(Tile\) == (\d+)") == Const(w, "TileSize"),
              $"C++ {Assert(@"sizeof\(Tile\) == (\d+)")}, C# {Const(w, "TileSize")}");
        Check("block size agrees", Assert(@"sizeof\(Block\) == (\d+)") == Const(w, "BlockSize"),
              $"C++ {Assert(@"sizeof\(Block\) == (\d+)")}, C# {Const(w, "BlockSize")}");
        foreach (var (field, constant) in new[] { ("enabled", "OffEnabled"), ("scale", "OffScale"), ("targetPid", "OffTarget"), ("tiles", "OffTiles") })
        {
            int cpp = Assert($@"offsetof\(Block, {field}\) == (\d+)");
            Check($"offset of {field} agrees", cpp == Const(w, constant), $"C++ {cpp}, C# {Const(w, constant)}");
        }

        // The rest of the block, laid out the C++ way by hand from the header's field order, so
        // a field added in the middle on one side only is caught too.
        Check("the remaining offsets follow the C++ order",
              Const(w, "OffLayout") == 25 && Const(w, "OffAnchorX") == 32 && Const(w, "OffAnchorY") == 36 &&
              Const(w, "OffPanel") == 40 && Const(w, "OffOverlay") == 44 &&
              Regex.IsMatch(h, @"uint8_t enabled;[^\n]*\n\s*uint8_t layout;[^\n]*\n\s*uint8_t reserved4\[2\];[^\n]*\n\s*float scale;[^\n]*\n\s*float anchorX, anchorY;[^\n]*\n\s*float panelOpacity;[^\n]*\n\s*float overlayOpacity;[^\n]*\n\s*uint32_t targetPid;"));
        Check("tile fields follow the C++ order",
              Const(w, "TileId") == 0 && Const(w, "TileLabel") == 16 && Const(w, "TileValue") == 40 &&
              Const(w, "TileUnit") == 64 && Const(w, "TileState") == 76 && Const(w, "TileHasBar") == 77 && Const(w, "TileBar") == 80 &&
              Regex.IsMatch(h, @"char id\[16\];[^\n]*\n\s*char label\[24\];[^\n]*\n\s*char value\[24\];[^\n]*\n\s*char unit\[12\];"));
        Check("the name and version agree",
              h.Contains("Local\\\\PulseIngame-v3") && (string)w.GetField("Name", Any)!.GetValue(null)! == @"Local\PulseIngame-v3" &&
              Regex.IsMatch(h, @"kVersion = 3;"));
    }

    static object Tile(string id, string label, string value, string unit, byte state, bool bar, float fraction)
        => Activator.CreateInstance(T("Pulse.Services.InGame.InGameTile"), id, label, value, unit, state, bar, fraction)!;

    static byte[] Encode(object frame) =>
        (byte[])T("Pulse.Services.InGame.InGameBlockWriter").GetMethod("Encode", Any)!.Invoke(null, new[] { frame })!;

    static object Frame(bool enabled, bool bar, float scale, float ax, float ay, float panel, float overlay, IList<object> tiles, uint target)
    {
        var tileType = T("Pulse.Services.InGame.InGameTile");
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(tileType))!;
        foreach (var t in tiles) list.Add(t);
        return Activator.CreateInstance(T("Pulse.Services.InGame.InGameFrame"), enabled, bar, scale, ax, ay, panel, overlay, list, target)!;
    }

    static string Text(byte[] b, int at, int size)
    {
        int n = Array.IndexOf(b, (byte)0, at, size) - at;
        return Encoding.UTF8.GetString(b, at, n < 0 ? size : n);
    }

    static void BlockEncoding()
    {
        var tiles = new List<object>
        {
            Tile("cpu_temp", "CPU Temp", "62", "°C", 1, true, 0.62f),
            Tile("fps", "FPS", "144", "fps", 0, false, 0f),
        };
        var b = Encode(Frame(true, true, 1.25f, 1f, 0.5f, 0.4f, 0.85f, tiles, 4242));

        Check("block is 2088 bytes", b.Length == 2088, b.Length.ToString());
        Check("magic and version", BitConverter.ToUInt32(b, 0) == 0x504C5345 && BitConverter.ToUInt32(b, 4) == 3);
        Check("sequence left for the writer", BitConverter.ToUInt32(b, 8) == 0);
        Check("tile count", BitConverter.ToUInt32(b, 12) == 2);
        Check("enabled and the straight layout", b[24] == 1 && b[25] == 1);
        Check("size, place and opacities",
              BitConverter.ToSingle(b, 28) == 1.25f && BitConverter.ToSingle(b, 32) == 1f && BitConverter.ToSingle(b, 36) == 0.5f &&
              BitConverter.ToSingle(b, 40) == 0.4f && BitConverter.ToSingle(b, 44) == 0.85f);
        Check("the one game that may draw", BitConverter.ToUInt32(b, 48) == 4242);
        Check("first tile's text", Text(b, 72, 16) == "cpu_temp" && Text(b, 72 + 16, 24) == "CPU Temp" &&
              Text(b, 72 + 40, 24) == "62" && Text(b, 72 + 64, 12) == "°C");
        Check("first tile's state and bar", b[72 + 76] == 1 && b[72 + 77] == 1 && BitConverter.ToSingle(b, 72 + 80) == 0.62f);
        Check("second tile starts 84 bytes on", Text(b, 72 + 84, 16) == "fps" && Text(b, 72 + 84 + 40, 24) == "144");

        var off = Encode(Frame(false, false, 1, 0, 0, 1, 1, new List<object>(), 0));
        Check("turned off: nothing enabled, no tiles, no game named",
              off[24] == 0 && BitConverter.ToUInt32(off, 12) == 0 && BitConverter.ToUInt32(off, 48) == 0);

        var many = Enumerable.Range(0, 30).Select(i => Tile($"t{i}", "L", "1", "", 0, false, 0)).ToList();
        var full = Encode(Frame(true, false, 1, 0, 0, 1, 1, many, 1));
        Check("more tiles than fit: the first 24, and no overrun", BitConverter.ToUInt32(full, 12) == 24 && full.Length == 2088);

        var wild = Encode(Frame(true, false, 1, 0, 0, 1, 1, new List<object> { Tile("x", "X", "1", "", 0, true, 7f) }, 1));
        Check("a bar beyond full is held at full", BitConverter.ToSingle(wild, 72 + 80) == 1f);
    }

    static void TextIsCutWhole()
    {
        var put = T("Pulse.Services.InGame.InGameBlockWriter").GetMethod("PutText", Any)!;
        string Put(int size, string text)
        {
            var b = new byte[size + 4];
            put.Invoke(null, new object[] { b, 0, size, text });
            return Text(b, 0, size);
        }

        Check("short text whole", Put(12, "°C") == "°C");
        Check("long text cut, room left for the end marker", Put(8, "abcdefghij") == "abcdefg");
        // "°" is two bytes: cut at 7 bytes, "aaaaa°°" would split the second one.
        var cut = Put(8, "aaaaa°°");
        Check("never in the middle of a character", cut == "aaaaa°" && !cut.Contains('�'), cut);
        Check("nothing given, nothing written", Put(8, null) == "");
    }

    // ── which programs ───────────────────────────────────────────────────────────

    static string FindAntiCheat(string process, string exe, params string[] modules)
    {
        var set = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
        return (string)T("Pulse.Services.InGame.GameCheck").GetMethod("FindAntiCheat", Any)!
            .Invoke(null, new object[] { process, exe, set });
    }

    static void AntiCheatRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulse-ingame-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Game(string name, params string[] files)
            {
                var gameRoot = Path.Combine(root, "steamapps", "common", name);
                var bin = Path.Combine(gameRoot, "Game", "Binaries", "Win64");
                Directory.CreateDirectory(bin);
                foreach (var f in files)
                {
                    var path = Path.Combine(gameRoot, f);
                    if (f.EndsWith("\\")) Directory.CreateDirectory(path);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); }
                }
                var exe = Path.Combine(bin, name + "-Win64-Shipping.exe");
                File.WriteAllText(exe, "");
                return exe;
            }

            var clean = Game("CleanGame");
            Check("a game with no anti-cheat is allowed", FindAntiCheat("CleanGame-Win64-Shipping", clean) == null);

            var eac = Game("EacGame", "EasyAntiCheat\\");
            Check("EasyAntiCheat at the game's root, three folders up, is found", FindAntiCheat("EacGame-Win64-Shipping", eac) != null);

            var be = Game("BeGame", "BattlEye\\BEService_x64.exe");
            Check("BattlEye beside the game is found", FindAntiCheat("BeGame-Win64-Shipping", be) != null);

            var launcher = Game("ProtectedGame", "start_protected_game.exe");
            Check("EAC's protected launcher is found", FindAntiCheat("ProtectedGame-Win64-Shipping", launcher) != null);

            // A file in the library folder belongs to no game in particular; the scan stops below it.
            File.WriteAllText(Path.Combine(root, "steamapps", "common", "EasyAntiCheat_EOS_Setup.exe"), "");
            Check("not past the library's 'common' folder", FindAntiCheat("CleanGame-Win64-Shipping", clean) == null);

            Check("anti-cheat loaded in the game is found",
                  FindAntiCheat("CleanGame-Win64-Shipping", clean, "kernel32.dll", "BEClient_x64.dll") != null);
            Check("names compared without case",
                  FindAntiCheat("CleanGame-Win64-Shipping", clean, "EASYANTICHEAT_EOS.DLL") != null);
            Check("a known anti-cheat game by name, with nothing on disk", FindAntiCheat("VALORANT-Win64-Shipping", clean) != null);
            Check("BloodStrike, whose anti-cheat leaves no file by name", FindAntiCheat("BloodStrike", clean) != null);
            Check("Counter-Strike 2", FindAntiCheat("cs2", clean) != null);
            Check("ordinary libraries are not mistaken for anti-cheat",
                  FindAntiCheat("CleanGame-Win64-Shipping", clean, "d3d11.dll", "dxgi.dll", "xinput1_4.dll", "steam_api64.dll",
                                "nvapi64.dll", "amd_ags_x64.dll", "GFSDK_Aftermath_Lib.x64.dll", "bink2w64.dll", "fmod.dll") == null);
            // Deep enough that the walk up stays inside this test's own folder.
            Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));
            Check("a missing folder is not an error",
                  FindAntiCheat("Gone", Path.Combine(root, "a", "b", "c", "nowhere", "x.exe")) == null);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var display = T("Pulse.Services.InGame.GameCheck").GetMethod("DisplayName", Any)!;
        Check("a game's name falls back to the process name", (string)display.Invoke(null, new object[] { "", "SomeGame" }) == "SomeGame");
        Check("Windows' own name is not used as a game's",
              !((string)display.Invoke(null, new object[] { Path.Combine(Environment.SystemDirectory, "kernel32.dll"), "x" }))
                  .Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
    }

    static void NotGames()
    {
        var set = (HashSet<string>)T("Pulse.Services.InGame.GameCheck").GetField("NotGames", Any)!.GetValue(null)!;
        foreach (var name in new[] { "Pulse", "explorer", "chrome", "msedge", "Discord", "steam", "steamwebhelper", "obs64", "RTSS", "Code" })
            Check($"{name} is not a game", set.Contains(name));
        Check("compared without case", set.Contains("DISCORD"));
    }

    // ── its place and settings ───────────────────────────────────────────────────

    static void WindowOverlayPlace()
    {
        var settingsType = T("Pulse.Models.AppSettings");
        var anchor = T("Pulse.Services.InGame.InGameOverlayService").GetMethod("WindowOverlayAnchor", Any)!;
        (float, float) Place(string position, double fx = -1, double fy = -1)
        {
            var s = Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("OverlayPosition")!.SetValue(s, position);
            settingsType.GetProperty("OverlayAnchorFx")!.SetValue(s, fx);
            settingsType.GetProperty("OverlayAnchorFy")!.SetValue(s, fy);
            return ((float, float))anchor.Invoke(null, new[] { s })!;
        }

        Check("top left", Place("TopLeft") == (0f, 0f));
        Check("top right", Place("TopRight") == (1f, 0f));
        Check("bottom left", Place("BottomLeft") == (0f, 1f));
        Check("bottom right", Place("BottomRight") == (1f, 1f));
        Check("dragged: the same share of the screen", Place("Custom", 0.25, 0.75) == (0.25f, 0.75f));
        Check("dragged but never placed: Pulse's default corner", Place("Custom") == (1f, 0f));
    }

    static void SettingsAreRepaired()
    {
        var type = T("Pulse.Models.AppSettings");
        var s = Activator.CreateInstance(type)!;
        Check("off by default", (bool)type.GetProperty("InGameOverlayEnabled")!.GetValue(s)! == false);
        Check("its own look off by default", (bool)type.GetProperty("InGameOwnLook")!.GetValue(s)! == false);
        Check("its own readings off by default", (bool)type.GetProperty("InGameOwnTiles")!.GetValue(s)! == false);
        Check("slightly see-through by default", (double)type.GetProperty("InGameOpacity")!.GetValue(s)! == 0.85);

        type.GetProperty("InGameLayout")!.SetValue(s, "Diagonal");
        type.GetProperty("InGameOpacity")!.SetValue(s, double.NaN);
        type.GetProperty("InGameAnchorX")!.SetValue(s, 7.0);
        type.GetProperty("InGameScale")!.SetValue(s, 0.0);
        type.GetProperty("InGameTileIds")!.SetValue(s, new List<string> { "fps", "", "fps", "gpu_temp" });
        type.GetProperty("InGameExcludedGames")!.SetValue(s, new List<string> { "Game.exe", "game.EXE", " " });
        type.GetMethod("Sanitise", Any)!.Invoke(s, null);

        Check("an unknown layout becomes the list", (string)type.GetProperty("InGameLayout")!.GetValue(s)! == "List");
        Check("an unreadable opacity becomes the default", (double)type.GetProperty("InGameOpacity")!.GetValue(s)! == 0.85);
        Check("a place off the screen becomes the default", (double)type.GetProperty("InGameAnchorX")!.GetValue(s)! == 0.0);
        Check("a size of nothing becomes normal", (double)type.GetProperty("InGameScale")!.GetValue(s)! == 1.0);
        Check("readings: blanks and repeats dropped, order kept",
              string.Join(",", (List<string>)type.GetProperty("InGameTileIds")!.GetValue(s)!) == "fps,gpu_temp");
        Check("games turned off for: one each, without case",
              ((List<string>)type.GetProperty("InGameExcludedGames")!.GetValue(s)!).Count == 1);
    }
}
