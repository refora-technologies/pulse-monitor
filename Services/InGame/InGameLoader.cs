using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services.InGame;

/// <summary>
/// Puts the in-game overlay into a game Pulse has already checked (GameCheck), and reads back
/// what it says about itself.
/// </summary>
/// <remarks>
/// <para>
/// The standard way Windows offers: the path of pulse_ingame.dll is written into the game, and the
/// game is asked to load it with LoadLibraryW on a thread of its own. Nothing else of the game's is
/// read or written. The overlay cannot be taken out again (unloading code from a running game is
/// not safe), so it stays until the game closes and simply stops drawing when told to.
/// </para>
/// <para>
/// Pulse runs as administrator, which is what lets it do this for games started by the person
/// signed in; a game protected by Windows refuses, and is left alone.
/// </para>
/// </remarks>
internal static class InGameLoader
{
    public const string DllName = "pulse_ingame.dll";

    /// The overlay as installed: beside Pulse.exe, in "ingame", with its fonts.
    public static string DllPath => Path.Combine(AppContext.BaseDirectory, "ingame", DllName);

    /// <summary>
    /// Loads the overlay into the game. Returns null on success, or what went wrong.
    /// </summary>
    public static string? Load(int pid)
    {
        var dll = DllPath;
        if (!File.Exists(dll)) return "the in-game part is missing from Pulse's folder";

        IntPtr process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                                     PROCESS_VM_WRITE | PROCESS_VM_READ, false, pid);
        if (process == IntPtr.Zero) return $"Windows would not let Pulse open it ({Marshal.GetLastWin32Error()})";

        IntPtr remote = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(dll + "\0");
            remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero || !WriteProcessMemory(process, remote, bytes, (UIntPtr)bytes.Length, out _))
                return $"could not pass it the path ({Marshal.GetLastWin32Error()})";

            // kernel32 is mapped at the same address in every 64-bit process on a boot, so Pulse's own
            // LoadLibraryW address is the game's. GameCheck has already made sure the game is 64-bit.
            IntPtr loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero) return $"could not start the load ({Marshal.GetLastWin32Error()})";
            try
            {
                if (WaitForSingleObject(thread, 10_000) != 0) return "the game did not finish loading it in 10 seconds";
                GetExitCodeThread(thread, out uint loaded);
                return loaded != 0 ? null : "the game refused to load it";
            }
            finally { CloseHandle(thread); }
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Whether the overlay is already in this game, from the game's list of loaded libraries.
    /// </summary>
    public static bool IsLoaded(Process process) => GameCheck.LoadedModuleNames(process).Contains(DllName);

    /// What the overlay in a game says it is doing (InGame/src/pulse_ingame_shared.h, Status).
    public enum State : uint { Starting = 0, Drawing = 1, Hidden = 2, NotDirectX = 3, SwitchedOff = 4, Unknown = 99 }

    /// <summary>
    /// The overlay's own report from inside the game, and how old it is. The game writes this, so
    /// it is only ever a hint: it decides whether to hide the window overlay and what the status
    /// line says, nothing else.
    /// </summary>
    public static (State State, ulong AgeMs)? ReadStatus(int pid)
    {
        IntPtr mapping = OpenFileMappingW(FILE_MAP_READ, false, $@"Local\PulseIngameStatus-{pid}");
        if (mapping == IntPtr.Zero) return null;
        try
        {
            IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, (UIntPtr)16);
            if (view == IntPtr.Zero) return null;
            try
            {
                if ((uint)Marshal.ReadInt32(view, 0) != 0x50535453) return null;  // "PSTS"
                uint state = (uint)Marshal.ReadInt32(view, 4);
                ulong at = (ulong)Marshal.ReadInt64(view, 8);
                ulong now = GetTickCount64();
                var known = state <= 4 ? (State)state : State.Unknown;
                return (known, now >= at ? now - at : 0);
            }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(mapping); }
    }

    private const uint PROCESS_CREATE_THREAD = 0x0002, PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_OPERATION = 0x0008,
                       PROCESS_VM_WRITE = 0x0020, PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04, FILE_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr parameter, uint flags, out uint id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeThread(IntPtr thread, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
}
