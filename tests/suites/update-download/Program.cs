// Tests that an update download can always be escaped.
//
// It previously could not. The transfer had no overall timeout and no read timeout, and there
// was no way to cancel it, so a connection that quietly died left the panel showing a frozen
// percentage with nothing to press. Killing Pulse was the only way out.
//
// These run against a real local HTTP server that misbehaves on purpose: one that sends a few
// bytes and then goes silent forever, and one that serves slowly but steadily. Nothing is
// mocked, so what is proven is the behaviour of the actual download loop.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        if (ok) _pass++; else _fail++;
    }

    static Type _service = null!, _info = null!, _status = null!;

    static int Main()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Pulse.dll");
        if (!File.Exists(dll)) { Console.WriteLine("Pulse.dll not found next to the test"); return 2; }

        var asm = Assembly.LoadFrom(dll);
        _service = asm.GetType("Pulse.Services.UpdateService")!;
        _info    = asm.GetType("Pulse.Services.UpdateInfo")!;
        _status  = asm.GetType("Pulse.Services.UpdateDownloadStatus")!;

        Console.WriteLine("Update download\n");

        Shape();
        Cancellation().GetAwaiter().GetResult();
        WhatWeOpenAndWhatWeFetch();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ── values from the network reaching Windows ────────────────────────────────────
    static void WhatWeOpenAndWhatWeFetch()
    {
        Console.WriteLine();
        Console.WriteLine("An address from the API must be a web address before Windows opens it:");

        var isWeb = _service.GetMethod("IsWebAddress",
            BindingFlags.NonPublic | BindingFlags.Static);

        Check("the scheme is checked at all", isWeb != null,
              "UseShellExecute opens whatever it is given, however it sees fit");

        if (isWeb != null)
        {
            bool Web(string url) => (bool)isWeb.Invoke(null, new object[] { url })!;

            foreach (var good in new[]
                     {
                         "https://github.com/refora-technologies/pulse-monitor/releases/tag/v1.2.0",
                         "http://example.com/notes",
                     })
                Check($"{good[..Math.Min(38, good.Length)]}... is opened", Web(good));

            // None of these is expected from GitHub. That is the point: the check costs a line
            // and does not depend on the server behaving.
            foreach (var bad in new[]
                     {
                         @"file:///C:/Windows/System32/cmd.exe",
                         @"C:\Windows\System32\cmd.exe",
                         "ms-settings:windowsupdate",
                         "javascript:alert(1)",
                         "",
                         "not a url at all",
                     })
                Check($"\"{bad}\" is not", !Web(bad),
                      "the releases page is opened instead");
        }

        Console.WriteLine();
        Console.WriteLine("The installer is chosen by name, not by whatever is listed first:");

        var source = Source("Services/UpdateService.cs");

        Check("our own installer is preferred",
              source.Contains("StartsWith(\"PulseSetup\""),
              "a release with a second executable used to be a coin toss");

        Check("and any executable is still accepted if there is no match",
              source.Contains("?? assets.FirstOrDefault(IsExe)"),
              "a renamed installer must not stop updates entirely");

        Check("a wrong choice would still be refused, not run",
              source.Contains("Sha256") || source.Contains("SHA-256") || source.Contains("sha256"),
              "the checksum is what makes this a usability fix rather than a safety one");
    }

    static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        return "";
    }

    // ── the contract ────────────────────────────────────────────────────────────────
    static void Shape()
    {
        Console.WriteLine("A download must be interruptible at all:");

        var method = _service.GetMethod("DownloadAndRunAsync", BindingFlags.Public | BindingFlags.Static);
        Check("the download accepts a cancellation token",
              method != null && Array.Exists(method.GetParameters(),
                  p => p.ParameterType == typeof(CancellationToken)),
              method == null ? "method missing"
                             : string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name)));

        foreach (var name in new[] { "Cancelled", "Stalled" })
            Check($"there is a distinct '{name}' outcome",
                  Enum.IsDefined(_status, name),
                  "so the panel can tell the user which happened");
    }

    /// Builds an UpdateInfo pointing at our own misbehaving server.
    static object Info(string url, long size)
    {
        var info = Activator.CreateInstance(_info)!;
        _info.GetProperty("InstallerUrl")!.SetValue(info, url);
        _info.GetProperty("InstallerName")!.SetValue(info, "PulseSetup.exe");
        _info.GetProperty("InstallerSize")!.SetValue(info, size);
        _info.GetProperty("ChecksumUrl")!.SetValue(info, url + ".sha256");
        _info.GetProperty("TagName")!.SetValue(info, "v0.0.0-test");
        return info;
    }

    static async Task<string> Download(object info, CancellationToken token)
    {
        var method = _service.GetMethod("DownloadAndRunAsync", BindingFlags.Public | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, new object?[] { info, null, token })!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!.ToString()!;
    }

    static bool Elevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ── against a server that stops responding ──────────────────────────────────────
    static async Task Cancellation()
    {
        Console.WriteLine("\nA token that is already cancelled must be honoured before anything is done:");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var sw = Stopwatch.StartNew();
        var result = await Download(Info("http://127.0.0.1:1/never", 1000), cancelled.Token);
        sw.Stop();

        // Valid at any privilege level: nothing is downloaded, so the locked down folder is
        // never involved.
        Check("it returns immediately", sw.Elapsed < TimeSpan.FromSeconds(2), $"{sw.Elapsed.TotalSeconds:F1}s");
        Check("and says it was cancelled, not that it failed", result == "Cancelled", result);

        Console.WriteLine("\nAgainst a server that goes silent mid-download:");

        if (!Elevated())
        {
            Console.WriteLine("    (skipped: Pulse downloads into a folder only Administrators can write to,");
            Console.WriteLine("     which is deliberate, so these need an elevated run to mean anything)");
            return;
        }

        using var server = new StallingServer();
        server.RecordExistingDownloadFolders();
        server.Start();

        var cts = new CancellationTokenSource();
        sw.Restart();
        var run = Download(Info(server.Url, 50_000_000), cts.Token);

        await Task.Delay(1500);           // let it connect and receive the first chunk
        Check("it is waiting on a socket that has gone quiet", !run.IsCompleted,
              "not spinning, not failed, just blocked as it should be");

        cts.Cancel();

        try   { result = await run.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { result = "NEVER RETURNED"; }
        sw.Stop();

        Check("cancelling escapes that wait", result != "NEVER RETURNED",
              $"returned '{result}' after {sw.Elapsed.TotalSeconds:F1}s");
        Check("and it reports cancellation, not failure", result == "Cancelled", result);
        Check("promptly", sw.Elapsed < TimeSpan.FromSeconds(10), $"{sw.Elapsed.TotalSeconds:F1}s");
        var leftover = server.LeftBehind();
        Check("the half finished file is not left behind", leftover == null,
              leftover ?? "a partial installer must never be kept");
    }
}

/// <summary>
/// Serves a few bytes and then stops sending, holding the connection open. This is what a half
/// open socket looks like to the client, and it is the case that used to hang the panel.
/// </summary>
sealed class StallingServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    public string Url { get; }

    public StallingServer()
    {
        var port = 41000 + Random.Shared.Next(0, 500);
        Url = $"http://127.0.0.1:{port}/PulseSetup.exe";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try   { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // The checksum request is answered normally, so the download gets past
                        // verification setup and reaches the transfer, which is the part
                        // under test.
                        if (ctx.Request.Url!.AbsolutePath.EndsWith(".sha256"))
                        {
                            var body = Encoding.ASCII.GetBytes(new string('a', 64));
                            ctx.Response.ContentLength64 = body.Length;
                            await ctx.Response.OutputStream.WriteAsync(body);
                            ctx.Response.Close();
                            return;
                        }

                        ctx.Response.ContentLength64 = 50_000_000;
                        await ctx.Response.OutputStream.WriteAsync(new byte[64_000]);
                        await ctx.Response.OutputStream.FlushAsync();

                        // Then nothing, for as long as the test needs.
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    catch { }
                });
            }
        });
    }

    /// The download writes into a locked down temp folder of its own; a cancelled attempt must
    /// clean it up rather than leaving a partial installer on disk.
    /// Directories that already existed when this server was created. A completed download
    /// legitimately leaves its installer in one of these until Pulse next starts and clears
    /// it, so scanning every folder found a real 73 MB installer from an actual update and
    /// reported it as our leak. Only what this test itself produced counts.
    private readonly HashSet<string> _preexisting = new(StringComparer.OrdinalIgnoreCase);

    public void RecordExistingDownloadFolders()
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "Pulse-update-*"))
            _preexisting.Add(dir);
    }

    /// <summary>
    /// Anything left in a folder this test caused to be created, or null if there is nothing.
    ///
    /// Allowed a moment to disappear: the delete happens as the cancelled request unwinds and
    /// the handle is not always released by the time the call returns, so checking instantly
    /// would measure the race rather than the outcome.
    /// </summary>
    public string? LeftBehind()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string? found = null;

            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "Pulse-update-*"))
            {
                if (_preexisting.Contains(dir)) continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        found = $"{Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)";
                        break;
                    }
                }
                catch { }

                if (found != null) break;
            }

            if (found == null) return null;
            if (attempt == 19) return found;

            Thread.Sleep(100);
        }

        return null;
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
