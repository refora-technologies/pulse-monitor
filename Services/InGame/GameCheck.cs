using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Pulse.Services.InGame;

/// <summary>
/// Whether the in-game overlay may go into a program, and if not, why, in words for the status
/// line.
/// </summary>
internal enum Verdict
{
    Allowed,
    NotAGame,        // a browser, chat app, launcher, Windows itself, Pulse...
    StoreApp,        // runs in Windows' app sandbox: not supported until tested
    Excluded,        // the player turned it off for this game
    AntiCheat,       // never: the player's account comes first
    ThirtyTwoBit,    // only 64-bit programs in this release
    NoDirectX,       // does not use DirectX 11 or 12 (yet)
    NotOpenable,     // protected, or gone
}

internal readonly record struct GameInfo(int Pid, string Name, string Path, Verdict Verdict, string Detail);

/// <summary>
/// The rules for which programs the in-game overlay goes into (_the design's section 2_):
/// only the program in front, only once it draws with DirectX 11 or 12, only 64-bit, never one on
/// the not-a-game list, never one with anti-cheat, never one the player turned it off for, never
/// one Windows will not let Pulse look at.
/// </summary>
/// <remarks>
/// Looking is all this does: the program's path, the names of the libraries it has loaded, and
/// the files in its folder. Nothing here reads or changes the program's memory.
/// </remarks>
internal static class GameCheck
{
    /// <summary>
    /// Programs that draw with DirectX but are not games. Executable names without ".exe".
    /// </summary>
    internal static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows itself
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellHost",
        "TextInputHost", "LockApp", "SystemSettings", "ApplicationFrameHost", "Taskmgr", "mmc", "notepad", "mspaint",
        "SnippingTool", "ScreenClippingHost", "PhotosApp", "Microsoft.Photos", "WindowsTerminal", "conhost", "cmd", "powershell", "pwsh",
        "msedgewebview2", "Widgets", "WidgetService", "GameBar", "GameBarFTServer", "XboxPcApp",
        // browsers
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "arc", "zen", "librewolf", "waterfox", "thorium",
        // chat, calls, mail
        "Discord", "DiscordPTB", "DiscordCanary", "slack", "Teams", "ms-teams", "Telegram", "WhatsApp", "Signal", "Zoom", "Skype", "Element",
        "olk", "outlook", "thunderbird",
        // video, music, streaming, recording
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "PotPlayerMini64", "PotPlayerMini", "mpv", "wmplayer", "Microsoft.Media.Player", "Spotify",
        "obs64", "obs32", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core", "NVIDIA Share", "NVIDIA Overlay", "nvcontainer",
        "RTSS", "MSIAfterburner", "SteelSeriesGG", "SteelSeriesGGClient", "Medal", "Outplayed",
        // office and creative work
        "WINWORD", "EXCEL", "POWERPNT", "ONENOTE", "MSACCESS", "Acrobat", "AcroRd32", "Photoshop", "Illustrator", "AfterFX", "Premiere Pro",
        "Resolve", "blender", "figma", "Notion", "Obsidian",
        // development
        "Code", "devenv", "rider64", "idea64", "webstorm64", "pycharm64", "clion64", "studio64", "Cursor", "windsurf",
        // game launchers and stores: the game they start is the game
        "steam", "steamwebhelper", "EpicGamesLauncher", "EADesktop", "Origin", "Battle.net", "UbisoftConnect", "upc", "GalaxyClient",
        "RiotClientServices", "RiotClientUx", "Playnite.DesktopApp", "Playnite.FullscreenApp", "XboxApp",
        // this one
        "Pulse",
    };

    /// <summary>
    /// Games whose anti-cheat lives inside the game itself or in a platform, so there may be no
    /// file to find in the game's folder. Executable names without ".exe".
    /// </summary>
    internal static readonly HashSet<string> AntiCheatGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT-Win64-Shipping", "VALORANT", "LeagueClient", "League of Legends",           // Vanguard
        "cs2", "csgo", "dota2", "tf_win64", "hl2", "left4dead2", "project8",                   // VAC
        "RobloxPlayerBeta", "Roblox",                                                          // Hyperion
        "cod", "ModernWarfare", "BlackOps6", "cod24-cod",                                      // Ricochet
        "r5apex", "r5apex_dx12", "FortniteClient-Win64-Shipping", "TslGame", "RainbowSix",     // EAC / BattlEye titles
        "GenshinImpact", "YuanShen", "StarRail", "ZenlessZoneZero",                            // mhyprot
        "Overwatch", "Diablo IV", "Warzone",                                                   // Blizzard/Activision
        "BloodStrike", "Marvel-Win64-Shipping",                                                // NetEase: no files found by name
    };

    /// <summary>
    /// Pieces of file, folder and library names that mean anti-cheat, compared without case. Long
    /// enough not to turn up inside ordinary names; anti-cheat that leaves no such trace is in
    /// AntiCheatGames instead.
    /// </summary>
    internal static readonly string[] AntiCheatMarks =
    {
        "anticheat", "anti-cheat", "easyanticheat", "eac_launcher", "start_protected_game", "battleye", "beclient", "beservice",
        "ace-base", "ace-core", "sguard", "gameguard", "npgg", "xigncode", "x3.xem", "mhyprot", "faceit", "vanguard",
        "punkbuster", "pbcl", "pbsv", "eaanticheat", "javelin", "ricochet", "nprotect", "hackshield", "neac", "tenprotect", "tp3helper",
        "denuvo-anticheat", "equ8", "wellbia",
    };

    /// <summary>
    /// The verdict for the program with this process id.
    /// </summary>
    public static GameInfo Check(int pid, IReadOnlyCollection<string> excluded)
    {
        Process process;
        string name, path;
        try
        {
            process = Process.GetProcessById(pid);
            name = process.ProcessName;
            path = process.MainModule?.FileName ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GameInfo(pid, "", "", Verdict.NotOpenable, "Windows does not let Pulse look at it");
        }

        using (process)
        {
            if (pid == Environment.ProcessId || NotGames.Contains(name))
                return new GameInfo(pid, name, path, Verdict.NotAGame, "not a game");

            if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                return new GameInfo(pid, name, path, Verdict.StoreApp, "Microsoft Store and Xbox app games are not supported yet");

            var exe = System.IO.Path.GetFileName(path);
            if (excluded.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase)))
                return new GameInfo(pid, name, path, Verdict.Excluded, "turned off for this game");

            if (IsElectronApp(path))
                return new GameInfo(pid, name, path, Verdict.NotAGame, "not a game");

            var antiCheat = FindAntiCheat(name, path, LoadedModuleNames(process));
            if (antiCheat != null)
                return new GameInfo(pid, name, path, Verdict.AntiCheat, antiCheat);

            if (!Is64Bit(process))
                return new GameInfo(pid, name, path, Verdict.ThirtyTwoBit, "32-bit games are not supported yet");

            var modules = LoadedModuleNames(process);
            bool dxgi = modules.Contains("dxgi.dll"), d3d = modules.Contains("d3d11.dll") || modules.Contains("d3d12.dll");
            if (!dxgi || !d3d)
                return new GameInfo(pid, name, path, Verdict.NoDirectX, "it does not use DirectX 11 or 12");

            return new GameInfo(pid, name, path, Verdict.Allowed, "");
        }
    }

    /// <summary>
    /// The anti-cheat this game appears to use, or null. When unsure the answer is "yes": a game
    /// wrongly kept on the window overlay costs nothing, an account cannot be given back.
    /// </summary>
    internal static string? FindAntiCheat(string processName, string exePath, IReadOnlySet<string> modules)
    {
        if (AntiCheatGames.Contains(processName)) return $"{processName} uses anti-cheat";

        foreach (var module in modules)
            foreach (var mark in AntiCheatMarks)
                if (module.Contains(mark, StringComparison.OrdinalIgnoreCase))
                    return $"anti-cheat is loaded in it ({module})";

        // The game's folder, and up to three above it: engines put the executable in
        // Binaries\Win64 while the anti-cheat sits at the game's root.
        var dir = System.IO.Path.GetDirectoryName(exePath);
        for (int up = 0; up < 4 && !string.IsNullOrEmpty(dir); up++)
        {
            var found = MarkIn(dir);
            if (found != null) return $"anti-cheat files are in its folder ({found})";
            var parent = Directory.GetParent(dir);
            // Not past a library root: Steam's "common" folder holds every other game too.
            if (parent == null || parent.Name.Equals("common", StringComparison.OrdinalIgnoreCase)) break;
            dir = parent.FullName;
        }
        return null;
    }

    private static string? MarkIn(string dir)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var entryName = System.IO.Path.GetFileName(entry);
                foreach (var mark in AntiCheatMarks)
                    if (entryName.Contains(mark, StringComparison.OrdinalIgnoreCase)) return entryName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// Electron apps draw with DirectX through Chromium; their folder always holds resources\app.asar,
    /// which games do not.
    private static bool IsElectronApp(string exePath)
    {
        var dir = System.IO.Path.GetDirectoryName(exePath);
        return dir != null && File.Exists(System.IO.Path.Combine(dir, "resources", "app.asar"));
    }

    internal static IReadOnlySet<string> LoadedModuleNames(Process process)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (ProcessModule m in process.Modules) names.Add(m.ModuleName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return names;
    }

    private static bool Is64Bit(Process process)
    {
        try
        {
            if (IsWow64Process2(process.Handle, out ushort machine, out _))
                return machine == 0;  // IMAGE_FILE_MACHINE_UNKNOWN: not running under WOW64
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
