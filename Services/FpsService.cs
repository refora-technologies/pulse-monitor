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

    // --- Kill PresentMon along with Pulse, however Pulse dies ------------------------
    //
    // StopCapture only runs during an orderly shutdown. When Pulse is terminated instead —
    // Restart Manager closing it so an installer can replace its files, the uninstaller
    // closing it, a crash, or Task Manager — the capture process was simply left running.
    // It then held Resources\PresentMon\PresentMon-2.5.1-x64.exe open, which made installs
    // fail with "try again" and left the Resources folder behind after uninstalling.
    //
    // A job object with KILL_ON_JOB_CLOSE hands the problem to Windows: when the last handle
    // to the job goes away, which happens automatically when Pulse's process object is
    // destroyed, every process in the job is terminated. No cooperation from Pulse required.

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

    private static readonly IntPtr CaptureJob = CreateKillOnCloseJob();

    /// Deliberately never closed: the handle living until the process ends is exactly what
    /// makes the job tear its children down when Pulse does.
    private static IntPtr CreateKillOnCloseJob()
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
            return IntPtr.Zero;   // capture still works, it just will not self-clean
        }
    }

    private static readonly string PresentMonPath = Path.Combine(
        AppContext.BaseDirectory, "Resources", "PresentMon", "PresentMon-2.5.1-x64.exe");

    /// A frame time and when we saw it, so the window can be measured in time rather than
    /// in frames — a fixed frame count covers a different span at 30fps than at 240fps.
    private readonly record struct FrameSample(double Ms, long At);

    /// Averaging window, and how long without frames before the reading is considered dead.
    private const int FrameWindowMs = 1000;
    private const int StaleAfterMs  = 2000;

    /// <summary>
    /// Window the 1% low is measured over, and the guard rails around it.
    ///
    /// 1% of a one-second window would be well under a single frame, so the low needs its
    /// own much longer history. Sixty seconds matches what people expect from a live
    /// overlay, the sample cap keeps memory bounded at very high frame rates, and the
    /// minimum stops a number appearing before there is enough data for "the slowest 1%"
    /// to mean anything.
    /// </summary>
    private const int LowWindowMs   = 60_000;
    private const int LowMaxSamples = 20_000;
    private const int LowMinSamples = 200;

    private const int MaxRestarts = 5;

    private readonly object _lock = new();

    /// Frames kept per swap chain. A game with a launcher, a video layer or an overlay
    /// presents on several chains at once under one PID; averaging them together produced a
    /// number that matched none of them. The busiest chain is the one being played.
    private readonly Dictionary<string, Queue<FrameSample>> _bySwapChain = new();

    /// Long history of the busiest chain's frames, used for the 1% low.
    private readonly Queue<FrameSample> _lowSamples = new();
    private string _dominantChain = "";

    private readonly DispatcherTimer _targetTimer;

    private Process? _process;
    private int  _headerProcessIdIndex = -1;
    private int  _headerFrameTimeIndex = -1;
    private int  _headerSwapChainIndex = -1;
    private uint _foregroundPid;
    private bool _captureWanted;
    private bool _lowWanted;
    private bool _stopping;
    private int  _restartCount;

    public float? CurrentFps { get; private set; }

    /// <summary>
    /// The average frame rate of the slowest 1% of recent frames — NVIDIA's definition, and
    /// deliberately not the 1st percentile. Averaging the worst frames keeps a single deep
    /// stutter visible, where a percentile would report only the value at the boundary and
    /// hide it. Null until there are enough samples to mean anything.
    /// </summary>
    public float? OnePercentLowFps { get; private set; }

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
        var active  = SettingsService.Instance.Settings.ActiveTileIds;
        bool lowOn  = active.Contains("fps_1low");
        bool wanted = active.Contains("fps") || lowOn;

        // The 1% low keeps up to a minute of frames; nobody pays for that unless the tile
        // showing it is actually on.
        if (_lowWanted && !lowOn)
        {
            lock (_lock)
            {
                _lowSamples.Clear();
                OnePercentLowFps = null;
            }
        }
        _lowWanted     = lowOn;
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
                    //
                    // --v1_metrics pins the CSV schema. PresentMon 2.x can emit either the
                    // 1.x or 2.x metric set and the column names differ between them, so
                    // relying on whichever happens to be the default would mean a future
                    // PresentMon silently renaming the columns we look for — and FPS just
                    // quietly stopping.
                    Arguments              = "--output_stdout --no_console_stats --stop_existing_session --v1_metrics",
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += OnLine;
            _process.Exited             += OnCaptureExited;
            _process.Start();

            // Immediately after Start, so the window where an abrupt end to Pulse could
            // strand this process is as small as possible.
            if (CaptureJob != IntPtr.Zero)
            {
                try
                {
                    if (!AssignProcessToJobObject(CaptureJob, _process.Handle))
                        LogService.Warn(nameof(FpsService), "Frame capture could not be tied to Pulse's lifetime.");
                }
                catch (Exception ex)
                {
                    LogService.Error(nameof(FpsService), "Could not tie frame capture to Pulse's lifetime", ex);
                }
            }

            _process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            // PresentMon missing, blocked, or capture unavailable — CurrentFps just stays
            // null and the FPS tile shows "--", same as any other unreadable sensor. Worth
            // a line in the log though: "FPS shows nothing" is otherwise indistinguishable
            // from a game that simply is not presenting.
            LogService.Error(nameof(FpsService), "Could not start frame capture", ex);
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
            int presents = -1, displayChange = -1;

            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Equals("ProcessID", StringComparison.OrdinalIgnoreCase))
                    _headerProcessIdIndex = i;
                else if (fields[i].Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase))
                    _headerSwapChainIndex = i;
                else if (fields[i].Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase))
                    presents = i;
                else if (fields[i].Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase))
                    displayChange = i;
            }

            // Presents, not display changes.
            //
            // MsBetweenDisplayChange measures the gap between frames the monitor actually
            // showed, so it is capped by the refresh rate: on a 60Hz panel it can never
            // report above 60 however fast the game is really running. MsBetweenPresents
            // measures what the GPU produced, which is the number every other overlay calls
            // FPS. Preferring display changes gave Pulse an invisible ceiling at the refresh
            // rate while NVIDIA's overlay sat well above it on the same scene.
            _headerFrameTimeIndex = presents >= 0 ? presents : displayChange;
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

            var sample = new FrameSample(ms, Environment.TickCount64);
            samples.Enqueue(sample);
            Recompute();

            // Only the chain actually being played feeds the 1% low, so a menu or video
            // layer presenting slowly alongside the game cannot masquerade as stutter.
            if (_lowWanted && swapChain == _dominantChain)
            {
                _lowSamples.Enqueue(sample);

                while (_lowSamples.Count > LowMaxSamples ||
                       (_lowSamples.Count > 0 && sample.At - _lowSamples.Peek().At > LowWindowMs))
                {
                    _lowSamples.Dequeue();
                }
            }
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
        string busiestKey = "";
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

            if (samples.Count > (busiest?.Count ?? 0))
            {
                busiest    = samples;
                busiestKey = key;
            }
        }

        // Switching chains means the long history belongs to something else now.
        if (busiestKey != _dominantChain)
        {
            _dominantChain = busiestKey;
            _lowSamples.Clear();
            OnePercentLowFps = null;
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
                _lowSamples.Clear();
                _dominantChain   = "";
                CurrentFps       = null;
                OnePercentLowFps = null;
                return;
            }

            Recompute();
            RecomputeOnePercentLow(now);
        }
    }

    /// <summary>
    /// Averages the slowest 1% of frames in the window.
    ///
    /// Run from the half-second timer rather than per frame: it sorts the whole window, and
    /// doing that on every present at 240fps would cost far more than the metric is worth.
    /// Caller holds <see cref="_lock"/>.
    /// </summary>
    private void RecomputeOnePercentLow(long now)
    {
        if (!_lowWanted)
        {
            OnePercentLowFps = null;
            return;
        }

        while (_lowSamples.Count > 0 && now - _lowSamples.Peek().At > LowWindowMs)
            _lowSamples.Dequeue();

        if (_lowSamples.Count < LowMinSamples)
        {
            OnePercentLowFps = null;   // "--" rather than a figure built from too little data
            return;
        }

        var times = new double[_lowSamples.Count];
        int next = 0;
        foreach (var sample in _lowSamples) times[next++] = sample.Ms;
        Array.Sort(times);

        // Slowest frames are the longest ones, so take from the top of the sorted array.
        int worst = Math.Max(1, times.Length / 100);
        double total = 0;
        for (int i = times.Length - worst; i < times.Length; i++) total += times[i];

        double averageMs = total / worst;
        OnePercentLowFps = averageMs > 0 ? (float)(1000.0 / averageMs) : null;
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
            // Everything collected belonged to the app we just left, the minute of history
            // behind the 1% low included — otherwise alt-tabbing out of a game blanked FPS
            // but left the game's 1% low sitting there next to it.
            _bySwapChain.Clear();
            _lowSamples.Clear();
            _dominantChain   = "";
            CurrentFps       = null;
            OnePercentLowFps = null;
        }
    }

    public void Dispose()
    {
        _targetTimer.Stop();
        StopCapture();
    }
}
