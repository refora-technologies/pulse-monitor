using System.IO;
using System.Reflection;

namespace Pulse.Services;

/// <summary>
/// Where Pulse keeps its settings and logs: %APPDATA%\Refora\Pulse for Pulse itself, and a
/// folder of its own for anything else that loads Pulse's code.
/// </summary>
/// <remarks>
/// <para>
/// The anything else is the test suites, which load Pulse.dll to test the real code. They used
/// to write into the real folder, copying the real files aside first and putting them back
/// after, while a real Pulse was running and writing there too: test lines landed in the
/// real log twice on 4 Nov 2026 and again on 18 Nov. Now the question never arises, for every
/// suite, including ones not written yet.
/// </para>
/// <para>
/// Decided by which program is running, not by an environment variable or a setting. Pulse runs
/// as administrator, and anything a normal program can set that moves where an administrator
/// program writes is a way to make it write somewhere it should not.
/// </para>
/// </remarks>
internal static class DataFolder
{
    /// True inside Pulse.exe, the app and its sensor host alike.
    public static readonly bool IsPulse =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "Pulse", StringComparison.Ordinal);

    public static readonly string Root = IsPulse
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Refora", "Pulse")
        : Path.Combine(Path.GetTempPath(), "Pulse-tests", Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown");
}
