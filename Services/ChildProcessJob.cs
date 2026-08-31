using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Services;

/// <summary>
/// Ties every process Pulse starts to Pulse's own lifetime.
///
/// Pulse runs two children: the frame capture and the sensor host. Neither has any reason to
/// outlive us, and both would be invisible if they did — no window, no tray icon, just a
/// process holding a driver handle open and, in the capture's case, locking the very file the
/// next installer needs to replace.
///
/// A Windows job object with KILL_ON_JOB_CLOSE is the only mechanism that survives Pulse being
/// killed rather than closed, which is precisely the case worth defending against: an orderly
/// shutdown could tidy up on its own, an access violation or a Task Scheduler termination
/// cannot. When the last handle to the job closes — and the process ending closes it, whatever
/// the reason — Windows terminates everything in the job. No cooperation from us required.
/// </summary>
internal static class ChildProcessJob
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private const int  JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    /// Deliberately never closed: the handle living until the process ends is exactly what
    /// makes the job tear its children down when Pulse does.
    private static readonly IntPtr Job = Create();

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int size = Marshal.SizeOf(limits);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    return IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return job;
        }
        catch
        {
            return IntPtr.Zero;   // the children still work, they just will not self-clean
        }
    }

    /// <summary>
    /// Puts a freshly started process under Pulse's lifetime. Call immediately after Start, so
    /// the window in which an abrupt end to Pulse could strand it is as small as possible.
    ///
    /// Returns false if it could not be done, which is worth a line in the log but is never
    /// worth failing over — a stranded child is a tidiness problem, not a broken feature.
    /// </summary>
    public static bool Adopt(Process process)
    {
        if (Job == IntPtr.Zero) return false;

        try   { return AssignProcessToJobObject(Job, process.Handle); }
        catch { return false; }
    }
}
