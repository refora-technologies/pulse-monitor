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

    /// The connection stopped delivering data for long enough that it is not coming back.
    Stalled,

    /// The user asked to stop.
    Cancelled,
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

    /// <summary>
    /// How long the download may deliver nothing at all before it is given up on.
    ///
    /// Deliberately a stall timeout rather than a limit on the whole transfer. The installer is
    /// seventy megabytes and some people are on very slow connections, so any total deadline
    /// long enough to be fair is far too long to be useful. What is never legitimate is a
    /// connection that has stopped sending anything, which is what a half open socket looks
    /// like: without this, that left the progress bar frozen at some percentage with no timeout
    /// and no way out but killing Pulse.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout           = TimeSpan.FromSeconds(30),
            ResponseDrainTimeout     = Timeout.InfiniteTimeSpan,
        };

        // No overall timeout, for the reason given above. The stall watchdog in the read loop
        // is what bounds this instead, and cancellation is what lets the user out.
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
                // Our installer by name, before falling back to whatever executable comes
                // first. A release carrying a second .exe — a portable build, a helper tool —
                // would otherwise be picked by whichever GitHub happened to list first, and
                // Pulse would download it and try to run it as an installer. The checksum
                // check would refuse it, so this is the difference between a confusing failed
                // update and a working one, rather than between safe and unsafe.
                static bool IsExe(JToken a) =>
                    (a.Value<string>("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                var asset = assets.FirstOrDefault(a => IsExe(a)
                                && (a.Value<string>("name") ?? "")
                                    .StartsWith("PulseSetup", StringComparison.OrdinalIgnoreCase))
                         ?? assets.FirstOrDefault(IsExe);
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
        catch (Exception ex)
        {
            // Distinguishes "no network" from "you are up to date" after the fact.
            LogService.Error(nameof(UpdateService), "Update check failed", ex);
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
    public static async Task<UpdateDownloadStatus> DownloadAndRunAsync(
        UpdateInfo info, IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl))
            return UpdateDownloadStatus.DownloadFailed;

        if (cancellation.IsCancellationRequested) return UpdateDownloadStatus.Cancelled;

        // Fetched here rather than at check time — no point spending bandwidth on the
        // installer if we won't be able to verify it anyway, and this way the checksum
        // asset is only ever requested when an install is actually happening.
        string? expectedSha256 = null;
        if (!string.IsNullOrEmpty(info.ChecksumUrl))
        {
            try
            {
                var checksumContent = await ApiHttp.GetStringAsync(info.ChecksumUrl, cancellation);
                expectedSha256 = ParseSha256(checksumContent);
            }
            catch (Exception ex)
            {
                // Left null so we fail closed below, but recorded: this is why an update
                // refuses to install while the release page clearly has one.
                LogService.Error(nameof(UpdateService), "Could not fetch the update checksum", ex);
            }
        }

        // Cancelling during the checksum fetch is cancelling, not a missing checksum. The catch
        // above swallows every failure alike, so without this someone who pressed cancel here
        // was told the update could not be verified, which sounds like something is wrong with
        // the release rather than something they just did.
        if (cancellation.IsCancellationRequested) return UpdateDownloadStatus.Cancelled;

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
        catch (Exception ex)
        {
            LogService.Error(nameof(UpdateService), "Could not create a secure download folder", ex);
            return UpdateDownloadStatus.LocationNotSecurable;
        }

        // Fires when nothing has arrived for StallTimeout, and is pushed back on every chunk
        // that does. A slow connection therefore downloads for as long as it needs; only one
        // that has stopped entirely is abandoned.
        using var stall = new CancellationTokenSource(StallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stall.Token);

        try
        {
            // Download — streams explicitly closed before we hash/launch the exe
            await using (var src = await DownloadHttp.GetStreamAsync(info.InstallerUrl, linked.Token))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write,
                                                  FileShare.None, 65536, useAsync: true))
            {
                var total  = info.InstallerSize;
                var buffer = new byte[65536];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, linked.Token)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    received += read;

                    // Progress resets the watchdog. Done after the write so a disk that has
                    // stopped accepting data counts as a stall too.
                    stall.CancelAfter(StallTimeout);

                    if (total > 0)
                        progress?.Report(Math.Min(99, (int)(received * 100 / total)));
                }
                await dst.FlushAsync(CancellationToken.None);
            }
        }
        // Filtered on the token rather than the exception type, deliberately. Cancelling a read
        // on an HTTP response stream does not reliably surface as OperationCanceledException:
        // it can arrive as an IOException or an HttpRequestException wrapping one, depending on
        // where the socket was when it was torn down. Matching on the type alone reported a
        // cancelled download as a failed one.
        catch (Exception) when (linked.IsCancellationRequested)
        {
            TryDelete(target);

            // Which of the two cancelled matters to the user: one is their own doing, the
            // other is a connection that died without saying so.
            if (cancellation.IsCancellationRequested) return UpdateDownloadStatus.Cancelled;

            LogService.Warn(nameof(UpdateService),
                $"The update download stopped receiving data for {StallTimeout.TotalSeconds:F0}s and was abandoned.");
            return UpdateDownloadStatus.Stalled;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(UpdateService), "Downloading the update failed", ex);
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
        catch (Exception ex)
        {
            LogService.Error(nameof(UpdateService), "Verifying the downloaded update failed", ex);
            TryDelete(target);
            return UpdateDownloadStatus.DownloadFailed;
        }

        progress?.Report(100);

        // Verified but not yet launched. Someone who pressed cancel while the hash was being
        // computed should not have an installer open on them a moment later.
        if (cancellation.IsCancellationRequested)
        {
            TryDelete(target);
            return UpdateDownloadStatus.Cancelled;
        }

        try
        {
            // Launch installer elevated; the Inno Setup CloseApplications=yes will
            // close Pulse automatically before installing, so we just wait briefly
            // and then shut down ourselves to avoid a duplicate-close conflict.
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            await Task.Delay(1500);
            return UpdateDownloadStatus.Success;
        }
        catch (Exception ex)
        {
            LogService.Error(nameof(UpdateService), "Could not launch the downloaded installer", ex);
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
            // The URL comes from the GitHub API rather than from us, and UseShellExecute hands
            // whatever it is given to Windows to open however Windows sees fit. That is fine
            // for a web address and not fine for anything else: a scheme naming a local
            // program, or a path to one, would be launched just as willingly. GitHub is not
            // expected to serve either, but "the server we trust would never" is not a reason
            // to pass an unchecked string to ShellExecute, and checking it costs one line.
            var target = info?.ReleaseUrl is { Length: > 0 } u && IsWebAddress(u) ? u : ReleasesPage;

            Process.Start(new ProcessStartInfo
            {
                FileName        = target,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    /// Whether a string is an ordinary http or https address, and nothing more interesting.
    private static bool IsWebAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
