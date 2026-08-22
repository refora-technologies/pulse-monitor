using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Pulse.Services;

/// <summary>
/// Captures the frame rate of whatever process currently owns the foreground window, via
/// a bundled PresentMon subprocess. PresentMon runs once, system-wide (no --process_name /
/// --process_id filter), and this class filters its stream client-side by whichever PID is
/// currently foreground — so switching focus between a game and any other app just changes
/// which rows we pay attention to, without restarting the capture.
/// </summary>
public class FpsService : IDisposable
{
    private static FpsService? _instance;
    public static FpsService Instance => _instance ??= new FpsService();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly string PresentMonPath = Path.Combine(
        AppContext.BaseDirectory, "Resources", "PresentMon", "PresentMon-2.5.1-x64.exe");

    /// A frame time and when we saw it, so the window can be measured in time rather than
    /// in frames — a fixed frame count covers a different span at 30fps than at 240fps.
    private readonly record struct FrameSample(double Ms, long At);

    /// Averaging window, and how long without frames before the reading is considered dead.
    private const int FrameWindowMs = 1000;
    private const int StaleAfterMs  = 2000;

    private const int MaxRestarts = 5;

    private readonly object _lock = new();

    /// Frames kept per swap chain. A game with a launcher, a video layer or an overlay
    /// presents on several chains at once under one PID; averaging them together produced a
    /// number that matched none of them. The busiest chain is the one being played.
    private readonly Dictionary<string, Queue<FrameSample>> _bySwapChain = new();

    private readonly DispatcherTimer _targetTimer;

    private Process? _process;
    private int  _headerProcessIdIndex = -1;
    private int  _headerFrameTimeIndex = -1;
    private int  _headerSwapChainIndex = -1;
    private uint _foregroundPid;
    private bool _captureWanted;
    private bool _stopping;
    private int  _restartCount;

    public float? CurrentFps { get; private set; }

    private FpsService()
    {
        // Runs independently of the hardware polling interval so a focus change (e.g.
        // alt-tabbing into or out of a game) is picked up quickly and consistently.
        _targetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _targetTimer.Tick += (_, _) =>
        {
            RefreshForegroundTarget();
            ExpireIfStale();      // frames stopping is silent; nothing else would notice
        };

        ApplyCaptureState();
        SettingsService.Instance.SettingsChanged += (_, _) => ApplyCaptureState();
    }

    /// <summary>
    /// Starts or stops frame capture to match whether the FPS tile is actually shown.
    ///
    /// PresentMon captures system-wide, so every present from every process arrives on
    /// stdout and gets parsed. Running that when nobody is looking at an FPS number is
    /// pure waste — a whole extra process plus continuous line parsing — so capture is
    /// tied to the tile being enabled.
    /// </summary>
    private void ApplyCaptureState()
    {
        bool wanted = SettingsService.Instance.Settings.ActiveTileIds.Contains("fps");

        _captureWanted = wanted;

        if (wanted && _process is null)
        {
            _restartCount = 0;
            StartCapture();
            _targetTimer.Start();
        }
        else if (!wanted && _process is not null)
        {
            StopCapture();
            _targetTimer.Stop();
        }
    }

    private void StopCapture()
    {
        _stopping = true;   // suppresses the restart that our own Kill would otherwise trigger
        try
        {
            if (_process is not null)
            {
                _process.Exited -= OnCaptureExited;
                try { _process.Kill(); }    catch { }
                try { _process.Dispose(); } catch { }
            }
        }
        finally
        {
            _process  = null;
            _stopping = false;
        }

        // Header indices belong to the stream we just ended.
        _headerProcessIdIndex = -1;
        _headerFrameTimeIndex = -1;
        _headerSwapChainIndex = -1;

        lock (_lock)
        {
            _bySwapChain.Clear();
            CurrentFps = null;
        }
    }

    /// <summary>
    /// PresentMon can exit on its own — another tool taking over the ETW session, a driver
    /// reset, or being killed. Previously _process stayed non-null so nothing ever restarted
    /// it, and FPS simply never came back until the tile was toggled off and on again.
    /// </summary>
    private void OnCaptureExited(object? sender, EventArgs e)
    {
        if (_stopping || !_captureWanted) return;
        if (_restartCount >= MaxRestarts) return;   // it is not coming back; stop trying

        _restartCount++;

        _ = Task.Run(async () =>
        {
            // Backs off so a consistently failing PresentMon cannot spin.
            await Task.Delay(1000 * _restartCount);

            if (_stopping || !_captureWanted) return;

            try { _process?.Dispose(); } catch { }
            _process = null;

            _headerProcessIdIndex = -1;
            _headerFrameTimeIndex = -1;
            _headerSwapChainIndex = -1;

            StartCapture();
        });
    }

    private void StartCapture()
    {
        if (!File.Exists(PresentMonPath)) return;

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = PresentMonPath,
                    // No --process_name/--process_id: captures every process system-wide.
                    // Pulse already runs elevated, so this child inherits that automatically.
                    Arguments              = "--output_stdout --no_console_stats --stop_existing_session",
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += OnLine;
            _process.Exited             += OnCaptureExited;
            _process.Start();
            _process.BeginOutputReadLine();
        }
        catch
        {
            // PresentMon missing, blocked, or capture unavailable — CurrentFps just stays
            // null and the FPS tile shows "--", same as any other unreadable sensor.
            _process = null;
        }
    }

    private void OnLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        var fields = e.Data.Split(',');

        if (_headerProcessIdIndex < 0)
        {
            // First line is the CSV header. Columns are located by name rather than a
            // fixed index, since PresentMon's exact schema has shifted across versions.
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Equals("ProcessID", StringComparison.OrdinalIgnoreCase))
                    _headerProcessIdIndex = i;
                else if (fields[i].Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase))
                    _headerSwapChainIndex = i;
                else if (fields[i].Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase))
                    _headerFrameTimeIndex = i;
            }
            if (_headerFrameTimeIndex < 0)
            {
                // Fall back to present-to-present timing if display-change timing isn't
                // in this PresentMon build.
                for (int i = 0; i < fields.Length; i++)
                    if (fields[i].Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase))
                        _headerFrameTimeIndex = i;
            }
            return;
        }

        if (_headerProcessIdIndex < 0 || _headerFrameTimeIndex < 0) return;
        if (fields.Length <= Math.Max(_headerProcessIdIndex, _headerFrameTimeIndex)) return;


        // Invariant culture, not the machine's. PresentMon always writes a dot decimal
        // separator, so parsing under a comma-decimal locale (de-DE, fr-FR, pt-BR and many
        // others) either fails outright or reads "16.667" as sixteen thousand — FPS would
        // be blank or absurd for a large share of users.
        if (!uint.TryParse(fields[_headerProcessIdIndex], NumberStyles.Integer,
                           CultureInfo.InvariantCulture, out var pid) || pid != _foregroundPid) return;
        if (!double.TryParse(fields[_headerFrameTimeIndex], NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var ms) || ms <= 0) return;

        var swapChain = _headerSwapChainIndex >= 0 && fields.Length > _headerSwapChainIndex
            ? fields[_headerSwapChainIndex]
            : "";

        lock (_lock)
        {
            if (!_bySwapChain.TryGetValue(swapChain, out var samples))
            {
                samples = new Queue<FrameSample>();
                _bySwapChain[swapChain] = samples;
            }

            samples.Enqueue(new FrameSample(ms, Environment.TickCount64));
            Recompute();
        }
    }

    /// <summary>
    /// Recalculates FPS from the busiest swap chain inside the time window. Caller holds
    /// <see cref="_lock"/>.
    /// </summary>
    private void Recompute()
    {
        long now = Environment.TickCount64;

        Queue<FrameSample>? busiest = null;
        List<string>? empty = null;

        foreach (var (key, samples) in _bySwapChain)
        {
            while (samples.Count > 0 && now - samples.Peek().At > FrameWindowMs)
                samples.Dequeue();

            if (samples.Count == 0)
            {
                (empty ??= new List<string>()).Add(key);
                continue;
            }

            if (samples.Count > (busiest?.Count ?? 0)) busiest = samples;
        }

        // Chains come and go as menus, videos and overlays open and close.
        if (empty != null) foreach (var key in empty) _bySwapChain.Remove(key);

        // Two samples minimum: a single frame time is noise, not a frame rate.
        CurrentFps = busiest is { Count: >= 2 }
            ? (float)(1000.0 / busiest.Average(s => s.Ms))
            : null;
    }

    /// <summary>
    /// Clears the reading when frames stop arriving. Without this the last value stayed on
    /// screen indefinitely whenever a game paused, a renderer hung, PresentMon died or the
    /// window stopped presenting — showing a confident frame rate for something frozen.
    /// </summary>
    private void ExpireIfStale()
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;

            bool anyRecent = _bySwapChain.Values
                .Any(q => q.Count > 0 && now - q.Last().At <= StaleAfterMs);

            if (!anyRecent)
            {
                _bySwapChain.Clear();
                CurrentFps = null;
            }
            else
            {
                Recompute();
            }
        }
    }

    private void RefreshForegroundTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _foregroundPid) return;

        _foregroundPid = pid;
        lock (_lock)
        {
            _bySwapChain.Clear();
            CurrentFps = null;
        }
    }

    public void Dispose()
    {
        _targetTimer.Stop();
        StopCapture();
    }
}
