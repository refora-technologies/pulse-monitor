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
    Unrecognised,    // draws with DirectX but shows nothing that says "game"; the player can say it is one
}

internal readonly record struct GameInfo(int Pid, string Name, string Path, Verdict Verdict, string Detail);

/// <summary>
/// The rules for which programs the in-game overlay goes into (_the design's section 2_):
/// only the program in front, only once it draws with DirectX 11 or 12, only 64-bit, never one
/// with anti-cheat, never one the player turned it off for, never one Windows will not let Pulse
/// look at, and only one that looks like a game.
/// </summary>
/// <remarks>
/// "Looks like a game" is decided from what games have and other programs do not (GameSignal):
/// a store's game library loaded, a game engine, a game library folder, or the whole screen
/// without a title bar. Asking that, rather than keeping a list of every program that is not a
/// game, means a new chat app or editor is left alone without anyone adding it anywhere (owner,
/// 4 Nov). The not-a-game list stays as a backstop for programs known to look like games.
/// </remarks>
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
    public static GameInfo Check(int pid, IReadOnlyCollection<string> excluded) =>
        Check(pid, excluded, Array.Empty<string>(), IntPtr.Zero);

    /// <param name="included">Games the player said are games, from "Show Pulse in this game".</param>
    /// <param name="window">The program's window in front, to see whether it fills the screen.</param>
    public static GameInfo Check(int pid, IReadOnlyCollection<string> excluded, IReadOnlyCollection<string> included, IntPtr window)
    {
        Process process;
        string name, path;
        try
        {
            process = Process.GetProcessById(pid);
            path = process.MainModule?.FileName ?? "";
            name = DisplayName(path, process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GameInfo(pid, "", "", Verdict.NotOpenable, "Windows does not let Pulse look at it");
        }

        using (process)
        {
            if (pid == Environment.ProcessId || NotGames.Contains(process.ProcessName))
                return new GameInfo(pid, name, path, Verdict.NotAGame, "not a game");

            var exe = System.IO.Path.GetFileName(path);
            if (excluded.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase)))
                return new GameInfo(pid, name, path, Verdict.Excluded, "turned off for this game");

            if (IsElectronApp(path))
                return new GameInfo(pid, name, path, Verdict.NotAGame, "not a game");

            // A program that does not draw with DirectX 11 or 12 is not judged any further: an
            // archiver or a chat app is not a game, and calling it one with anti-cheat (WinRAR
            // was, 4 Nov) only confuses. One that draws with Vulkan is a game the overlay cannot
            // go into yet, and is said so.
            var modules = LoadedModuleNames(process);
            bool dxgi = modules.Contains("dxgi.dll"), d3d = modules.Contains("d3d11.dll") || modules.Contains("d3d12.dll");
            if (!dxgi || !d3d)
                return modules.Contains("vulkan-1.dll")
                    ? new GameInfo(pid, name, path, Verdict.NoDirectX, "Vulkan games are not supported yet")
                    : new GameInfo(pid, name, path, Verdict.NoDirectX, "it does not use DirectX 11 or 12");

            // After the checks above, so a store app that is not a game (the Claude app is one,
            // and an Electron app) is never spoken of as a store game.
            if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                return new GameInfo(pid, name, path, Verdict.StoreApp, "Microsoft Store and Xbox app games are not supported yet");

            var antiCheat = FindAntiCheat(process.ProcessName, path, modules);
            if (antiCheat != null)
                return new GameInfo(pid, name, path, Verdict.AntiCheat, antiCheat);

            if (!Is64Bit(process))
                return new GameInfo(pid, name, path, Verdict.ThirtyTwoBit, "32-bit games are not supported yet");

            // Last, so a game with anti-cheat is always called that, and never offered "show
            // Pulse in this game".
            bool chosen = included.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase));
            var signal = chosen ? "the player said it is a game" : GameSignal(process.ProcessName, path, modules, CoversItsScreen(window));
            if (signal == null)
                return new GameInfo(pid, name, path, Verdict.Unrecognised, "it does not look like a game");

            return new GameInfo(pid, name, path, Verdict.Allowed, signal);
        }
    }

    /// <summary>
    /// The game's name as its makers wrote it into the executable ("007 First Light" rather
    /// than "007FirstLight"), for the status line. Engine and stub names say nothing about the
    /// game, so those fall back to the process name.
    /// </summary>
    internal static string DisplayName(string path, string processName)
    {
        try
        {
            if (path.Length > 0)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                foreach (var candidate in new[] { info.ProductName, info.FileDescription })
                {
                    var text = candidate?.Trim() ?? "";
                    if (text.Length is >= 2 and <= 40 && !GenericNames.Any(g => text.Contains(g, StringComparison.OrdinalIgnoreCase)))
                        return text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }  // unreadable version details: the process name is the fallback
        return processName;
    }

    private static readonly string[] GenericNames =
        { "Bootstrap", "Unreal", "UnrealGame", "UE4", "UE5", "Unity", "Microsoft", "Windows", "Launcher", "Game.exe", "Shipping" };

    /// <summary>
    /// What says a program is a game, or null when nothing does.
    /// </summary>
    /// <remarks>
    /// Each of these is something games have and other programs do not: a store's or a game
    /// engine's library loaded, an Unreal game's executable name, a game library folder, or the
    /// whole screen without a title bar. Windowed games almost always have one of the first four.
    /// </remarks>
    internal static string? GameSignal(string processName, string exePath, IReadOnlySet<string> modules, bool coversScreen)
    {
        foreach (var marker in GameLibraries)
            if (modules.Contains(marker)) return $"it loads {marker}";

        if (processName.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))
            return "an Unreal Engine game";

        var dir = System.IO.Path.GetDirectoryName(exePath) ?? "";
        foreach (var part in dir.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
            if (GameFolders.Contains(part)) return $"it is installed in a game library ({part})";

        return coversScreen ? "it fills the screen" : null;
    }

    /// Libraries only games load: stores' game services and game engines.
    internal static readonly string[] GameLibraries =
    {
        "steam_api64.dll", "GameOverlayRenderer64.dll",                 // Steam
        "EOSSDK-Win64-Shipping.dll", "EOSOVH-Win64-Shipping.dll",       // Epic Online Services
        "Galaxy64.dll",                                                 // GOG
        "XCurl.dll", "PartyWin.dll", "libHttpClient.GDK.dll",           // Xbox and the Microsoft Game Development Kit
        "UnityPlayer.dll", "GameAssembly.dll",                          // Unity
        "nvngx_dlss.dll", "sl.interposer.dll", "amd_fidelityfx_dx12.dll", "libxess.dll",  // upscalers for games
        "dstorage.dll", "bink2w64.dll", "fmod.dll", "fmodstudio.dll", "PhysX3_x64.dll", "PhysX_64.dll",
    };

    /// Folders games are installed in, by name, anywhere in the path.
    internal static readonly HashSet<string> GameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamapps", "Epic Games", "GOG Games", "GOG Galaxy", "XboxGames", "Ubisoft Game Launcher",
        "EA Games", "Origin Games", "Riot Games", "Battle.net", "Amazon Games", "Games",
    };

    /// Whether a window fills its screen with no title bar: full screen or borderless, as games are.
    internal static bool CoversItsScreen(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var r)) return false;
        if ((GetWindowLongW(window, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private const int GWL_STYLE = -16;
    private const uint WS_CAPTION = 0x00C00000, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    /// <summary>
    /// The anti-cheat this game appears to use, or null. When unsure the answer is "yes": a game
    /// wrongly kept on the window overlay costs nothing, an account cannot be given back.
    /// </summary>
    internal static string? FindAntiCheat(string processName, string exePath, IReadOnlySet<string> modules)
    {
        if (AntiCheatGames.Contains(processName)) return "it uses anti-cheat";

        foreach (var module in modules)
            foreach (var mark in AntiCheatMarks)
                if (module.Contains(mark, StringComparison.OrdinalIgnoreCase))
                    return $"anti-cheat is loaded in it ({module})";

        // The game's folder, and up to three above it: engines put the executable in
        // Binaries\Win64 while the anti-cheat sits at the game's root. Never into a folder shared
        // with other programs: Program Files holds Riot Vanguard on any PC with Valorant, and
        // reaching it marked WinRAR, and would have marked any game installed there, as anti-cheat.
        // The program's own folder is always looked in, wherever it is.
        var dir = System.IO.Path.GetDirectoryName(exePath);
        for (int up = 0; up < 4 && !string.IsNullOrEmpty(dir) && (up == 0 || !IsSharedFolder(dir)); up++)
        {
            var found = MarkIn(dir);
            if (found != null) return $"anti-cheat files are in its folder ({found})";
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    /// <summary>
    /// Folders that hold many programs or games side by side, where a file belongs to no game in
    /// particular: drive roots, Windows' program and user folders, and the stores' libraries.
    /// </summary>
    internal static bool IsSharedFolder(string dir)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir));
        if (System.IO.Path.GetPathRoot(full)?.TrimEnd('\\', '/').Equals(full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) == true)
            return true;   // a drive root

        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                        Environment.SpecialFolder.Windows, Environment.SpecialFolder.UserProfile,
                                        Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                                        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var path = Environment.GetFolderPath(special);
            if (path.Length > 0 && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(path), full, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var name = System.IO.Path.GetFileName(full);
        return SharedFolderNames.Contains(name);
    }

    /// Library and collection folders by name, wherever they are.
    private static readonly HashSet<string> SharedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "common",            // Steam: steamapps\common
        "steamapps", "SteamLibrary", "Steam",
        "Epic Games", "GOG Games", "GOG Galaxy", "XboxGames", "WindowsApps",
        "EA Games", "Origin Games", "Ubisoft Game Launcher", "games",   // Ubisoft keeps them in ...\games
        "Riot Games", "Battle.net", "Amazon Games", "Program Files", "Program Files (x86)",
        "Users", "Downloads", "Desktop", "Documents", "Games",
    };

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }  // a folder that cannot be listed has nothing to find in it
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }  // protected or gone: an empty list, and the checks after this decide
        return names;
    }

    private static bool Is64Bit(Process process)
    {
        try
        {
            if (IsWow64Process2(process.Handle, out ushort machine, out _))
                return machine == 0;  // IMAGE_FILE_MACHINE_UNKNOWN: not running under WOW64
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }  // cannot be asked: treated as 32-bit, so it is left alone
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
