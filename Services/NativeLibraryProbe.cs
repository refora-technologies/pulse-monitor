using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Works out why a native library would not load, at the moment it would not load.
/// </summary>
/// <remarks>
/// Pulse ships self-contained and single-file, so the native WPF libraries are unpacked beside
/// the executable's bundle and loaded from there. On some machines one of them is briefly
/// unreadable during logon, which throws while the first window is still being built and leaves
/// Pulse running with no overlay.
///
/// The exception .NET raises for this says <c>Dll was not found.</c> and nothing else. No file
/// name, no error code, no indication whether the file is missing, locked, or unreadable. Four
/// completely different causes arrive looking identical, and every one of them needs a different
/// answer, so this walks the libraries itself and writes down what Windows actually says.
///
/// Everything here is best effort and nothing is allowed to throw. It runs on a failure path
/// during startup, where a second exception would be worse than no information.
/// </remarks>
internal static class NativeLibraryProbe
{
    /// The libraries that travel with a self-contained WPF build. Named rather than enumerated
    /// so the report says so explicitly when one of them is missing entirely.
    private static readonly string[] Expected =
    {
        "wpfgfx_cor3.dll",
        "PresentationNative_cor3.dll",
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "vcruntime140_cor3.dll",
    };

    /// One report per source. The overlay and the shortcut listener fail separately and their
    /// states can differ, so both are worth having, but a retry loop must not write twenty.
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    /// Maps the file without running its entry point. We are asking whether the image can be
    /// read, not asking it to initialise, and initialising graphics libraries on a failure path
    /// during startup is not a risk worth taking for a diagnostic.
    private const uint LoadLibraryAsDataFile = 0x00000002;

    public static void Record(string source, Exception error)
    {
        try
        {
            lock (Reported)
            {
                if (!Reported.Add(source)) return;
            }

            var report = new StringBuilder();
            report.Append("Native library probe after ").Append(error.GetType().Name);
            report.Append(". Machine up ").Append(Format(TimeSpan.FromMilliseconds(Environment.TickCount64)));
            report.Append(", Pulse up ").Append(Format(DateTime.Now - LogService.SessionStart)).Append('.');

            var directory = NativeDirectory();
            if (directory == null)
            {
                report.Append(" The native library directory could not be located.");
                LogService.Warn(nameof(NativeLibraryProbe), report.ToString());
                return;
            }

            // Not redacted here: every message goes through LogService.Redact on the way out,
            // so the user's account name never reaches the file.
            report.Append(" Directory ").Append(directory).Append(':');

            foreach (var name in Expected)
                report.Append(' ').Append(Describe(Path.Combine(directory, name), name)).Append(';');

            LogService.Warn(nameof(NativeLibraryProbe), report.ToString());
        }
        catch (Exception ex)
        {
            // The probe failing tells us almost nothing, but silently failing tells us less.
            try { LogService.Warn(nameof(NativeLibraryProbe), $"The probe itself failed: {ex.GetType().Name}"); }
            catch { }   // the log is the only way to report anything, and it just refused
        }
    }

    /// <summary>
    /// What Windows says about one library: whether it is there, whether we can read it, and
    /// whether its image can be mapped. The Win32 error is the part that matters.
    /// </summary>
    /// <remarks>
    /// 2 (file not found) means something removed it. 32 (sharing violation) means another
    /// process is holding it open, which is what a scanner reaching a file first looks like.
    /// 5 (access denied) points at permissions or policy. Those are three different fixes.
    /// </remarks>
    private static string Describe(string path, string name)
    {
        if (!File.Exists(path)) return $"{name}=MISSING";

        var state = new StringBuilder(name).Append('=');

        try
        {
            var info = new FileInfo(path);
            state.Append(info.Length / 1024).Append("KB");
            state.Append(" age=").Append(Format(DateTime.UtcNow - info.LastWriteTimeUtc));
        }
        catch (Exception ex)
        {
            state.Append("stat:").Append(ex.GetType().Name);
        }

        // Sharing set as wide as possible: a failure here means somebody else has asked for
        // exclusive access, not that we are competing with ourselves.
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            state.Append(" read=ok");
        }
        catch (Exception ex)
        {
            // Taken from the HRESULT rather than GetLastWin32Error, which is not meaningful
            // once a managed exception has been built over it. 0x8007xxxx carries the Win32
            // code in its low word, so 0x80070020 is 32, a sharing violation.
            state.Append(" read=FAILED(").Append(ex.HResult & 0xFFFF)
                 .Append(',').Append(ex.GetType().Name).Append(')');
        }

        var module = LoadLibraryExW(path, IntPtr.Zero, LoadLibraryAsDataFile);
        if (module == IntPtr.Zero) state.Append(" map=FAILED(").Append(Marshal.GetLastWin32Error()).Append(')');
        else { state.Append(" map=ok"); FreeLibrary(module); }

        return state.ToString();
    }

    /// <summary>
    /// Where the native libraries actually are for this build.
    /// </summary>
    /// <remarks>
    /// Asked of the process rather than assumed, because it differs between a single-file build
    /// that unpacks to %TEMP% and one that ships them beside the executable, and the whole point
    /// of this is to stop guessing. One of these is always loaded by the time any window is
    /// built, which is why deleting the folder from underneath a running Pulse fails.
    /// </remarks>
    internal static string? NativeDirectory()
    {
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var file = module.FileName;
                if (file == null) continue;

                foreach (var name in Expected)
                    if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                        return Path.GetDirectoryName(file);
            }
        }
        catch { /* module enumeration can fail; the fallback below still answers */ }

        // Nothing of ours is loaded yet. Fall back to the documented extraction location, newest
        // first, which is this build's if several are present.
        try
        {
            var root = Path.Combine(Path.GetTempPath(), ".net", "Pulse");
            if (!Directory.Exists(root)) return null;

            return new DirectoryInfo(root).GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Select(d => d.FullName)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// The same walk, for a diagnostic exported when nothing has gone wrong.
    /// </summary>
    /// <remarks>
    /// A failing machine's report is only worth so much on its own. Having the identical
    /// measurements from a machine that works is what makes a difference in them mean anything.
    /// </remarks>
    internal static IReadOnlyList<string> Snapshot()
    {
        var lines = new List<string>();
        try
        {
            var directory = NativeDirectory();
            if (directory == null) return new[] { "(the native library directory could not be located)" };

            lines.Add(directory);
            foreach (var name in Expected)
                lines.Add(Describe(Path.Combine(directory, name), name));
        }
        catch (Exception ex)
        {
            lines.Add($"(could not be read: {ex.GetType().Name})");
        }
        return lines;
    }

    internal static string MachineUptime() => Format(TimeSpan.FromMilliseconds(Environment.TickCount64));

    private static string Format(TimeSpan span) =>
        span.TotalDays    >= 1 ? $"{span.TotalDays:F1}d"
      : span.TotalHours   >= 1 ? $"{span.TotalHours:F1}h"
      : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F1}m"
                               : $"{span.TotalSeconds:F1}s";
}
