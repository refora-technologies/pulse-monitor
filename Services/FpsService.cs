using System.Diagnostics;
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

    private readonly object _lock = new();
    private readonly Queue<double> _recentFrameTimesMs = new();
    private readonly DispatcherTimer _targetTimer;

    private Process? _process;
    private int  _headerProcessIdIndex = -1;
    private int  _headerFrameTimeIndex = -1;
    private uint _foregroundPid;

    public float? CurrentFps { get; private set; }

    private FpsService()
    {
        // Runs independently of the hardware polling interval so a focus change (e.g.
        // alt-tabbing into or out of a game) is picked up quickly and consistently.
        _targetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _targetTimer.Tick += (_, _) => RefreshForegroundTarget();

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

        if (wanted && _process is null)
        {
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
        try { _process?.Kill(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;

        // Header indices belong to the stream we just ended.
        _headerProcessIdIndex = -1;
        _headerFrameTimeIndex = -1;

        lock (_lock)
        {
            _recentFrameTimesMs.Clear();
            CurrentFps = null;
        }
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
            _process.OutputDataReceived += OnLine;
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

        if (!uint.TryParse(fields[_headerProcessIdIndex], out var pid) || pid != _foregroundPid) return;
        if (!double.TryParse(fields[_headerFrameTimeIndex], out var ms) || ms <= 0) return;

        lock (_lock)
        {
            _recentFrameTimesMs.Enqueue(ms);
            while (_recentFrameTimesMs.Count > 60) _recentFrameTimesMs.Dequeue();

            CurrentFps = (float)(1000.0 / _recentFrameTimesMs.Average());
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
            _recentFrameTimesMs.Clear();
            CurrentFps = null;
        }
    }

    public void Dispose()
    {
        _targetTimer.Stop();
        StopCapture();
    }
}
