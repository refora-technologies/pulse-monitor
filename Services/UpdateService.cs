using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    public string? ChecksumUrl { get; init; }
    public string Notes { get; init; } = "";

    public string DisplayVersion => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
}

public enum UpdateDownloadStatus
{
    Success,
    DownloadFailed,
    VerificationFailed,
    VerificationUnavailable,

    /// The download location could not be secured against tampering, so we refused to run
    /// an installer from it. See CreateSecureDownloadDirectory.
    LocationNotSecurable,
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

    /// <summary>Success indicates the GitHub API call itself succeeded (distinct from "no update found"),
    /// so callers can tell "you're up to date" apart from "the check failed."</summary>
    public static async Task<(bool Success, UpdateInfo? Info)> CheckForUpdateAsync()
    {
        var (success, latest) = await FetchLatestAsync();
        if (!success) return (false, null);
        return (true, latest != null && latest.Version > CurrentVersion ? latest : null);
    }

    private static async Task<(bool Success, UpdateInfo? Info)> FetchLatestAsync()
    {
        try
        {
            var url  = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var json = await ApiHttp.GetStringAsync(url);
            var root = JObject.Parse(json);

            var tag = root.Value<string>("tag_name") ?? "";
            if (!TryParseVersion(tag, out var version)) return (true, null);

            string? installerUrl = null, installerName = null, checksumUrl = null;
            long    installerSize = 0;
            if (root["assets"] is JArray assets)
            {
                var asset = assets.FirstOrDefault(a =>
                    (a.Value<string>("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                installerUrl  = asset?.Value<string>("browser_download_url");
                installerName = asset?.Value<string>("name");
                installerSize = asset?.Value<long>("size") ?? 0L;

                if (installerName != null)
                {
                    var checksumAsset = assets.FirstOrDefault(a =>
                        string.Equals(a.Value<string>("name"), installerName + ".sha256",
                            StringComparison.OrdinalIgnoreCase));
                    checksumUrl = checksumAsset?.Value<string>("browser_download_url");
                }
            }

            // The checksum itself isn't fetched here — only its URL. Every check (including
            // the automatic one on every launch) used to download the .sha256 file
            // unconditionally, even when already on the latest version, which meant its
            // GitHub download count reflected "how many times someone checked" rather than
            // "how many times someone actually updated". It's now fetched in
            // DownloadAndRunAsync, which only runs when the user actually installs.
            return (true, new UpdateInfo
            {
                Version       = version,
                TagName       = tag,
                ReleaseUrl    = root.Value<string>("html_url") ?? ReleasesPage,
                InstallerUrl  = installerUrl,
                InstallerName = installerName,
                InstallerSize = installerSize,
                ChecksumUrl   = checksumUrl,
                Notes         = root.Value<string>("body") ?? "",
            });
        }
        catch
        {
            return (false, null);
        }
    }

    private static string? ParseSha256(string content)
    {
        var token = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
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

    /// Downloads the installer with progress, verifies its SHA-256 against the checksum
    /// published alongside the release, then launches it. Refuses to launch anything that
    /// isn't verified — we have no code-signing certificate, so the published hash is the
    /// only trust anchor we have.
    public static async Task<UpdateDownloadStatus> DownloadAndRunAsync(UpdateInfo info, IProgress<int>? progress = null)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl))
            return UpdateDownloadStatus.DownloadFailed;

        // Fetched here rather than at check time — no point spending bandwidth on the
        // installer if we won't be able to verify it anyway, and this way the checksum
        // asset is only ever requested when an install is actually happening.
        string? expectedSha256 = null;
        if (!string.IsNullOrEmpty(info.ChecksumUrl))
        {
            try
            {
                var checksumContent = await ApiHttp.GetStringAsync(info.ChecksumUrl);
                expectedSha256 = ParseSha256(checksumContent);
            }
            catch { /* left null — fails closed below */ }
        }

        if (string.IsNullOrEmpty(expectedSha256))
            return UpdateDownloadStatus.VerificationUnavailable;

        var fileName = string.IsNullOrEmpty(info.InstallerName)
            ? $"PulseSetup-{info.TagName}.exe"
            : info.InstallerName;

        string target;
        try
        {
            target = Path.Combine(CreateSecureDownloadDirectory(), fileName);
        }
        catch
        {
            return UpdateDownloadStatus.LocationNotSecurable;
        }

        try
        {
            // Download — streams explicitly closed before we hash/launch the exe
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
        }
        catch
        {
            TryDelete(target);
            return UpdateDownloadStatus.DownloadFailed;
        }

        try
        {
            await using var verifyStream = File.OpenRead(target);
            var hashBytes    = await SHA256.HashDataAsync(verifyStream);
            var actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                return UpdateDownloadStatus.VerificationFailed;
            }
        }
        catch
        {
            TryDelete(target);
            return UpdateDownloadStatus.DownloadFailed;
        }

        progress?.Report(100);

        try
        {
            // Launch installer elevated; the Inno Setup CloseApplications=yes will
            // close Pulse automatically before installing, so we just wait briefly
            // and then shut down ourselves to avoid a duplicate-close conflict.
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            await Task.Delay(1500);
            return UpdateDownloadStatus.Success;
        }
        catch
        {
            return UpdateDownloadStatus.DownloadFailed;
        }
    }

    private const string DownloadDirPrefix = "Pulse-update-";

    /// <summary>
    /// Creates a private directory to download the installer into.
    ///
    /// The plain temp directory is writable by the logged-on user, and Pulse runs elevated.
    /// Downloading there means anything else running as that (non-admin) user can swap the
    /// installer in the window between our hash check and Process.Start, and the replacement
    /// then inherits our elevation. Granting only Administrators and SYSTEM removes the
    /// window: an unprivileged process cannot write into the directory at all.
    ///
    /// Throws if the ACL cannot be applied. Callers fail closed rather than running an
    /// elevated installer out of a location they could not secure — the same stance as
    /// refusing an installer whose checksum will not verify.
    /// </summary>
    private static string CreateSecureDownloadDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), DownloadDirPrefix + Guid.NewGuid().ToString("N"));

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[] { admins, system })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var dir = new DirectoryInfo(path);
        dir.Create(security);

        // Ownership is set afterwards, deliberately. Putting the owner into the descriptor
        // passed to Create makes Create itself throw ("This security ID may not be assigned
        // as the owner of this object") when the process cannot assign it, which would take
        // the whole directory with it. As a separate step the failure is catchable, and the
        // DACL above — the part that actually keeps other users out — is already in place.
        //
        // It matters because an owner can always rewrite the DACL, so we would rather that
        // be Administrators than the logged-on user.
        try
        {
            var ownerInfo = dir.GetAccessControl(AccessControlSections.Owner);
            ownerInfo.SetOwner(admins);
            dir.SetAccessControl(ownerInfo);
        }
        catch { }

        return path;
    }

    /// <summary>
    /// Removes download directories left behind by earlier updates. Cleanup cannot happen at
    /// the end of an update because Pulse exits while the installer it launched is still
    /// running out of that directory, so it happens on the next launch instead.
    /// </summary>
    public static void CleanupStaleDownloads()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), DownloadDirPrefix + "*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }

            // Older builds downloaded straight into the temp root.
            TryDelete(Path.Combine(Path.GetTempPath(), "PulseSetup.exe"));
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
