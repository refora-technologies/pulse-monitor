using System.Runtime.InteropServices;

namespace Pulse.Services;

/// <summary>
/// Which process started which, taken once, to describe how a process drawing frames relates
/// to the program in front without naming either.
/// </summary>
/// <remarks>
/// For the frame capture's missing-frame-rate line (FpsService.WatchForMissingFrames), which
/// runs at most a few times in a session, so one snapshot of the process list each time is
/// cheap. Read from the Toolhelp snapshot rather than by opening each process, which needs no
/// access to the processes themselves: a game protected by anti-cheat can refuse to be opened
/// and is exactly the case worth describing.
///
/// Names are read only to recognise the desktop compositor, which presents all the time and is
/// never the game, and to tell a program's own helper (a copy of itself it started) from another
/// program. They live only as long as the snapshot and never leave this class.
/// </remarks>
internal sealed class ProcessFamily
{
    private readonly Dictionary<uint, uint> _parentOf = new();
    private readonly HashSet<uint> _compositors = new();
    private readonly Dictionary<uint, string> _exeOf = new();

    public static ProcessFamily Snapshot()
    {
        var family = new ProcessFamily();
        IntPtr snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return family;

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snap, ref entry); more; more = Process32NextW(snap, ref entry))
            {
                family._parentOf[entry.ProcessId] = entry.ParentProcessId;
                family._exeOf[entry.ProcessId] = entry.ExeFile ?? "";
                if (string.Equals(entry.ExeFile, "dwm.exe", StringComparison.OrdinalIgnoreCase))
                    family._compositors.Add(entry.ProcessId);
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return family;
    }

    /// Windows' desktop compositor, or Pulse itself: both draw all the time and neither is a game.
    public bool IsCompositor(uint pid) => _compositors.Contains(pid) || pid == (uint)Environment.ProcessId;

    /// Whether a process is tied to the one in front: started by it, started it, or started by
    /// the same program. How a game drawing from a second process looks.
    public bool IsRelated(uint pid, uint front)
    {
        if (!_parentOf.TryGetValue(pid, out var parent)) return false;
        _parentOf.TryGetValue(front, out var frontParent);
        return parent == front || pid == frontParent || (parent != 0 && parent == frontParent);
    }

    /// <summary>
    /// Whether a process is a helper the program in front started from its own program file:
    /// how browsers and the apps built on them (Electron, Chromium) draw, from a separate
    /// graphics process. Not a game's frame rate going missing.
    /// </summary>
    /// Seen 26 Nov 2026 in the owner's log: every recent "no frame rate" line was such an app
    /// in front, drawing through its own helper.
    public bool IsOwnHelper(uint pid, uint front) =>
        _parentOf.TryGetValue(pid, out var parent) && parent == front
        && _exeOf.TryGetValue(pid, out var exe) && _exeOf.TryGetValue(front, out var frontExe)
        && exe.Length > 0 && string.Equals(exe, frontExe, StringComparison.OrdinalIgnoreCase);

    /// How a process relates to the one in front, in words for the log.
    public string Relation(uint pid, uint front)
    {
        if (!_parentOf.TryGetValue(pid, out var parent)) return "gone by now";
        _parentOf.TryGetValue(front, out var frontParent);

        if (parent == front)                         return "started by the program in front";
        if (pid == frontParent)                      return "the program that started the one in front";
        if (parent != 0 && parent == frontParent)    return "started by the same program as the one in front";
        return "not related to the program in front";
    }

    private const uint SnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint   Size;
        public uint   Usage;
        public uint   ProcessId;
        public IntPtr DefaultHeapId;
        public uint   ModuleId;
        public uint   Threads;
        public uint   ParentProcessId;
        public int    PriorityClassBase;
        public uint   Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
