using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Pulse.Services;

public class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0, 0);
    public string TagName { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string? InstallerUrl { get; init; }
    public string? InstallerName { get; init; }
    public string Notes { get; init; } = "";

    public string DisplayVersion => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
}

public class UpdateService
{
    private const string Owner = "refora-technologies";
    private const string Repo  = "pulse-monitor";
    private const string ReleasesPage = "https://github.com/refora-technologies/pulse-monitor/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.Add("User-Agent", "PulseMonitor");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
        }
    }

    public static string CurrentVersionLabel =>
        $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// Returns the latest release if it is newer than the running build, otherwise null.
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        var latest = await FetchLatestAsync();
        if (latest == null) return null;
        return latest.Version > CurrentVersion ? latest : null;
    }

    private static async Task<UpdateInfo?> FetchLatestAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var json = await Http.GetStringAsync(url);
            var root = JObject.Parse(json);

            var tag = root.Value<string>("tag_name") ?? "";
            if (!TryParseVersion(tag, out var version)) return null;

            string? installerUrl = null, installerName = null;
            if (root["assets"] is JArray assets)
            {
                var asset = assets.FirstOrDefault(a =>
                    (a.Value<string>("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                installerUrl  = asset?.Value<string>("browser_download_url");
                installerName = asset?.Value<string>("name");
            }

            return new UpdateInfo
            {
                Version      = version,
                TagName      = tag,
                ReleaseUrl   = root.Value<string>("html_url") ?? ReleasesPage,
                InstallerUrl = installerUrl,
                InstallerName = installerName,
                Notes        = root.Value<string>("body") ?? "",
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var trimmed = tag.TrimStart('v', 'V', ' ');
        var core = new string(trimmed.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (core.Length == 0) return false;
        var parts = core.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
        version = new Version(major, minor, build);
        return true;
    }

    /// Downloads the installer and launches it, then signals the caller to exit.
    /// Falls back to opening the release page when no installer asset is attached.
    public static async Task<bool> DownloadAndRunAsync(UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl))
        {
            OpenReleasePage(info);
            return false;
        }

        try
        {
            var fileName = string.IsNullOrEmpty(info.InstallerName)
                ? $"PulseSetup-{info.TagName}.exe"
                : info.InstallerName;
            var target = Path.Combine(Path.GetTempPath(), fileName);

            var bytes = await Http.GetByteArrayAsync(info.InstallerUrl);
            await File.WriteAllBytesAsync(target, bytes);

            Process.Start(new ProcessStartInfo
            {
                FileName        = target,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            OpenReleasePage(info);
            return false;
        }
    }

    public static void OpenReleasePage(UpdateInfo? info)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = info?.ReleaseUrl is { Length: > 0 } u ? u : ReleasesPage,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
