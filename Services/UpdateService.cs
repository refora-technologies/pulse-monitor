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
    public long InstallerSize { get; init; }
    public string Notes { get; init; } = "";

    public string DisplayVersion => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
}

public class UpdateService
{
    private const string Owner = "refora-technologies";
    private const string Repo  = "pulse-monitor";
    private const string ReleasesPage = "https://github.com/refora-technologies/pulse-monitor/releases";

    private static readonly HttpClient ApiHttp = CreateApiClient();
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "PulseMonitor");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout           = TimeSpan.FromSeconds(30),
            ResponseDrainTimeout     = Timeout.InfiniteTimeSpan,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("User-Agent", "PulseMonitor");
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
            var url  = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var json = await ApiHttp.GetStringAsync(url);
            var root = JObject.Parse(json);

            var tag = root.Value<string>("tag_name") ?? "";
            if (!TryParseVersion(tag, out var version)) return null;

            string? installerUrl = null, installerName = null;
            long    installerSize = 0;
            if (root["assets"] is JArray assets)
            {
                var asset = assets.FirstOrDefault(a =>
                    (a.Value<string>("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                installerUrl  = asset?.Value<string>("browser_download_url");
                installerName = asset?.Value<string>("name");
                installerSize = asset?.Value<long>("size") ?? 0L;
            }

            return new UpdateInfo
            {
                Version       = version,
                TagName       = tag,
                ReleaseUrl    = root.Value<string>("html_url") ?? ReleasesPage,
                InstallerUrl  = installerUrl,
                InstallerName = installerName,
                InstallerSize = installerSize,
                Notes         = root.Value<string>("body") ?? "",
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

    /// Downloads the installer with progress, then launches it. Returns false on failure.
    public static async Task<bool> DownloadAndRunAsync(UpdateInfo info, IProgress<int>? progress = null)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl))
            return false;

        try
        {
            var fileName = string.IsNullOrEmpty(info.InstallerName)
                ? $"PulseSetup-{info.TagName}.exe"
                : info.InstallerName;
            var target = Path.Combine(Path.GetTempPath(), fileName);

            // Download — streams explicitly closed before we launch the exe
            await using (var src = await DownloadHttp.GetStreamAsync(info.InstallerUrl))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write,
                                                  FileShare.None, 65536, useAsync: true))
            {
                var total  = info.InstallerSize;
                var buffer = new byte[65536];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total > 0)
                        progress?.Report(Math.Min(99, (int)(received * 100 / total)));
                }
                await dst.FlushAsync();
            }
            // Both streams fully closed here — file is complete and unlocked

            progress?.Report(100);

            // Launch installer elevated; the Inno Setup CloseApplications=yes will
            // close Pulse automatically before installing, so we just wait briefly
            // and then shut down ourselves to avoid a duplicate-close conflict.
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            await Task.Delay(1500);
            return true;
        }
        catch
        {
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
