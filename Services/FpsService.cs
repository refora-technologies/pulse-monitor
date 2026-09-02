using System.Buffers;
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
    /// Lazy rather than `??=`: that is not atomic, and these are reached from the polling
    /// thread and the UI thread at the same time during startup. Losing the race builds two
    /// instances, each with its own event subscribers, so notifications reach an object
    /// nobody is listening to.
    private static readonly Lazy<FpsService> LazyInstance =
        new(() => new FpsService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static FpsService Instance => LazyInstance.Value;

    /// <summary>
    /// Whether frame capture has been set up yet, so that readings arriving from elsewhere do
    /// not accidentally be the thing that sets it up.
    ///
    /// This matters because the constructor builds a DispatcherTimer, and a DispatcherTimer
    /// belongs to whichever thread created it. Sensor readings now arrive on a background
    /// thread reading a pipe, and that thread has no message loop — so if a reading were the
    /// first thing ever to touch this class, the timer would attach to a dispatcher that never
    /// runs and the foreground game would silently stop being tracked. App creates this on the
    /// UI thread during startup; everything else waits for that.
    /// </summary>
    public static bool IsStarted => LazyInstance.IsValueCreated;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Killing PresentMon along with Pulse, however Pulse dies, is handled by ChildProcessJob.
    // StopCapture only runs during an orderly shutdown; when Pulse is terminated instead — by
    // the Restart Manager so an installer can replace its files, by the uninstaller, by a
    // crash, or by Task Manager — the capture process used to be left running. It then held
    // Resources\PresentMon\PresentMon-2.5.1-x64.exe open, which made installs fail with "try
    // again" and left the Resources folder behind after uninstalling.

    private static readonly string PresentMonPath = Path.Combine(
        AppContext.BaseDirectory, "Resources", "PresentMon", "PresentMon-2.5.1-x64.exe");

    /// <summary>
    /// A frame time and when the frame happened, so a window can be measured in time rather
    /// than in frames: a fixed frame count covers a different span at 30fps than at 240fps.
    ///
    /// At is PresentMon's own timestamp for the frame, not the moment we read the line. Those
    /// are not the same thing. Output arrives in bursts, so a batch of frames read together
    /// were all recorded as having happened at once, which stretched or compressed both
    /// windows depending on how the reader happened to be scheduled.
    ///
    /// DisplayMs is the gap since the previous frame the monitor actually showed, which is a
    /// different measurement from Ms rather than a refinement of it. It is zero for a frame
    /// that was never displayed, which is normal and frequent: with the frame rate above the
    /// refresh rate most produced frames are replaced before they reach the panel.
    /// </summary>
    private readonly record struct FrameSample(double Ms, long At, double DisplayMs);

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

    /// <summary>
    /// A safety limit, not a window. It used to be 20,000, which is only sixty seconds' worth
    /// up to about 333fps: above that the cap silently became the real window, so a "60 second"
    /// 1% low was measuring 33 seconds at 600fps and 20 at 1000, with nothing saying so. Sized
    /// now for sixty seconds at 2000fps, so time decides the window at any frame rate a real
    /// machine produces and this only ever catches a stream that has gone wrong.
    /// </summary>
    private const int LowMaxSamples = 120_000;

    /// <summary>
    /// Before the 1% low means anything it needs both enough frames and enough time.
    ///
    /// A frame count alone is not a duration: 200 frames is nearly seven seconds at 30fps and
    /// under a second at 240, so the reading appeared almost immediately on a fast machine and
    /// described a moment rather than a minute.
    /// </summary>
    private const int LowMinSamples = 200;
    private const int LowMinSpanMs  = 3_000;

    /// A capture that survives this long is considered healthy and clears the restart count,
    /// so occasional interruptions over a long session never accumulate into a long backoff.
    private static readonly TimeSpan CaptureSettled = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();

    /// Frames kept per swap chain. A game with a launcher, a video layer or an overlay
    /// presents on several chains at once under one PID; averaging them together produced a
    /// number that matched none of them. The busiest chain is the one being played.
    private readonly Dictionary<string, Queue<FrameSample>> _bySwapChain = new();

    /// Long history of the busiest chain's frames, used for the 1% low.
    private readonly Queue<FrameSample> _lowSamples = new();
    private string _dominantChain = "";

    /// <summary>
    /// A chain has to stay ahead before it takes over, because switching throws away the
    /// minute of history behind the 1% low.
    ///
    /// The busiest chain was previously whichever had one more frame than the others at that
    /// instant, so in a game presenting on comparable chains the winner changed constantly and
    /// each change wiped the history. The 1% low could sit at "--" indefinitely while a
    /// perfectly steady game was running.
    /// </summary>
    private const int   ChainSwitchMargin = 4;   // consecutive wins needed
    private const float ChainSwitchLead   = 1.25f;

    private string _chainCandidate = "";
    private int    _chainCandidateWins;

    /// When the last frame reached us, as opposed to when it happened. Used only to notice
    /// that frames have stopped arriving, which is a property of the reader rather than the
    /// capture, so it is the one thing that still belongs on the local clock.
    private long _lastFrameArrival;

    private int _headerTimeIndex = -1;

    private readonly DispatcherTimer _targetTimer;

    private Process? _process;
    private int  _headerProcessIdIndex = -1;
    private int  _headerFrameTimeIndex = -1;
    private int  _headerDisplayIndex   = -1;
    private int  _headerSwapChainIndex = -1;
    private uint _foregroundPid;
    private bool _captureWanted;
    private bool _lowWanted;
    private bool _displayedWanted;
    private bool _stopping;
    private int  _restartCount;

    /// <summary>
    /// Raised a few times a second once frame capture is running, whether or not the numbers
    /// changed. Subscribers read CurrentFps and OnePercentLowFps.
    ///
    /// Raised on the UI thread, since the timer behind it is a DispatcherTimer.
    /// </summary>
    public event EventHandler? Updated;

    public float? CurrentFps { get; private set; }

    /// <summary>
    /// The average frame rate of the slowest 1% of recent frames — NVIDIA's definition, and
    /// deliberately not the 1st percentile. Averaging the worst frames keeps a single deep
    /// stutter visible, where a percentile would report only the value at the boundary and
    /// hide it. Null until there are enough samples to mean anything.
    /// </summary>
    public float? OnePercentLowFps { get; private set; }

    /// <summary>
    /// The other 1% low: the frame on the boundary rather than the average of everything past
    /// it, which is what RTSS, MSI Afterburner and CapFrameX report.
    ///
    /// Offered because people compare overlays side by side, and two tools disagreeing looks
    /// like one of them is broken when in fact they are measuring different things. This one
    /// always reads at least as high as <see cref="OnePercentLowFps"/> on the same frames, by
    /// construction: the boundary is the fastest of the frames the average is taken over.
    ///
    /// Computed from the same sorted window, so it costs essentially nothing.
    /// </summary>
    public float? OnePercentLowP1Fps { get; private set; }

    /// <summary>
    /// Frames that actually reached the monitor, as opposed to frames the graphics card
    /// produced.
    ///
    /// Capped by the refresh rate: on a 60Hz panel this cannot exceed 60 however fast the game
    /// is really running, and with V-Sync on it should sit at the refresh rate almost exactly.
    /// That ceiling is the reason it is a separate reading and not the main one — see the note
    /// in the header parser. Useful for seeing how much of what the card produces is being
    /// thrown away, and for confirming V-Sync or a frame cap is doing what it claims.
    /// </summary>
    public float? DisplayedFps { get; private set; }

    private FpsService()
    {
        // Runs independently of the hardware polling interval so a focus change (e.g.
        // alt-tabbing into or out of a game) is picked up quickly and consistently.
        // Also what publishes the readings, which is why it is faster than it needs to be for
        // the focus check alone. Frame rates used to reach the overlay only when a sensor
        // snapshot arrived, so choosing a five second polling rate for temperatures also made
        // the frame rate update every five seconds. They are unrelated measurements and one
        // should not set the pace of the other.
        _targetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _targetTimer.Tick += (_, _) =>
        {
            RefreshForegroundTarget();
            ExpireIfStale();      // frames stopping is silent; nothing else would notice
            Updated?.Invoke(this, EventArgs.Empty);
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
        var active = SettingsService.Instance.Settings.ActiveTileIds;

        // Both 1% lows are drawn from the same window of frames, so either one being on is
        // reason enough to keep it, and keeping it once serves both.
        bool lowOn       = active.Contains("fps_1low") || active.Contains("fps_1low_p1");
        bool displayedOn = active.Contains("fps_displayed");
        bool wanted      = active.Contains("fps") || lowOn || displayedOn;

        // The 1% low keeps up to a minute of frames; nobody pays for that unless a tile
        // showing it is actually on.
        if (_lowWanted && !lowOn)
        {
            lock (_lock)
            {
                _lowSamples.Clear();
                OnePercentLowFps   = null;
                OnePercentLowP1Fps = null;
            }
        }

        if (_displayedWanted && !displayedOn)
            lock (_lock) DisplayedFps = null;

        _lowWanted       = lowOn;
        _displayedWanted = displayedOn;
        _captureWanted   = wanted;

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
        _headerDisplayIndex   = -1;
        _headerSwapChainIndex = -1;

        lock (_lock)
        {
            _bySwapChain.Clear();
            CurrentFps   = null;
            DisplayedFps = null;
        }
    }

    /// <summary>
    /// PresentMon can exit on its own: another tool taking over the ETW session, a driver
    /// reset, or being killed. Previously _process stayed non-null so nothing ever restarted
    /// it, and FPS simply never came back until the tile was toggled off and on again.
    /// </summary>
    private void OnCaptureExited(object? sender, EventArgs e)
    {
        // Only the capture this handler belongs to. The restart is delayed, and without this
        // an exit that arrived while a newer capture was already running would tear down the
        // newer one: the delayed work disposed whatever _process happened to hold by then,
        // which is not necessarily the process that exited. Toggling the tile off and on
        // quickly was enough to leave two captures running, stopping each other.
        if (sender is not Process exited || !ReferenceEquals(exited, _process)) return;
        if (_stopping || !_captureWanted) return;

        // A capture that ran for a good while was working, so whatever ended it is a fresh
        // problem rather than a continuing one. Without this the backoff only ever grows.
        var lived = TimeSpan.FromMilliseconds(Environment.TickCount64 - _captureStartedAt);
        if (lived >= CaptureSettled) _restartCount = 0;

        int generation = ++_captureGeneration;
        _restartCount++;

        // The exit code is for the log line below and nothing else. A process disposed from
        // another thread refuses to give it up, which is not worth a second log entry about.
        int code = -1;
        try { code = exited.ExitCode; } catch { }   // for the log line below, nothing else

        LogService.Warn(nameof(FpsService),
            $"Frame capture stopped (exit code {code}); restart {_restartCount} in {BackoffFor(_restartCount).TotalSeconds:F0}s.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(BackoffFor(_restartCount));

            // Anything started since supersedes this restart.
            if (_stopping || !_captureWanted || generation != _captureGeneration) return;

            try { _process?.Dispose(); } catch { }
            _process = null;

            _headerProcessIdIndex = -1;
            _headerFrameTimeIndex = -1;
            _headerDisplayIndex   = -1;
            _headerSwapChainIndex = -1;

            StartCapture();
        });
    }

    /// <summary>
    /// How long to wait before trying again, capped.
    ///
    /// Never gives up. It used to stop after five attempts and never resume, so five unrelated
    /// interruptions across a long session left the FPS tiles permanently blank until Pulse was
    /// restarted, with nothing on screen to say why. A capture that has been healthy for a
    /// while resets the count, so an occasional hiccup does not accumulate into a long wait.
    /// </summary>
    private static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(attempt, 15));

    private int  _captureGeneration;
    private long _captureStartedAt;

    private void StartCapture()
    {
        if (!File.Exists(PresentMonPath))
        {
            LogService.Warn(nameof(FpsService),
                $"Frame capture is unavailable: {PresentMonPath} is missing.");
            return;
        }

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
                    // --session_name is what makes --stop_existing_session safe. PresentMon
                    // stops "a trace session with the same name", and with no name given that
                    // is the default one, which any other PresentMon based tool is also using.
                    // So starting frame capture silently killed a capture belonging to
                    // CapFrameX, OCAT, or someone's own PresentMon run. Named after our own
                    // process, the flag now only ever clears a session we abandoned ourselves.
                    Arguments              = "--output_stdout --no_console_stats --v1_metrics "
                                           + $"--session_name Pulse_{Environment.ProcessId} --stop_existing_session",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
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
            if (!ChildProcessJob.Adopt(_process))
                LogService.Warn(nameof(FpsService), "Frame capture could not be tied to Pulse's lifetime.");

            _captureStartedAt = Environment.TickCount64;

            // Read as well as redirected. PresentMon explains itself here when it refuses to
            // start, and none of that was reaching the log, so "FPS shows nothing" was
            // indistinguishable from a game that simply is not presenting.
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    LogService.Warn(nameof(FpsService), $"PresentMon: {e.Data.Trim()}");
            };

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            // PresentMon missing, blocked, or capture unavailable — CurrentFps just stays
            // null and the FPS tile shows "--", same as any other unreadable sensor. Worth
            // a line in the log though: "FPS shows nothing" is otherwise indistinguishable
            // from a game that simply is not presenting.
            LogService.Error(nameof(FpsService), "Could not start frame capture", ex);
            _process = null;
            ScheduleRetry();
        }
    }

    /// <summary>
    /// Tries again after a launch that never produced a process.
    ///
    /// Exited only fires for something that actually started, so a failed Process.Start left
    /// nothing to trigger a retry and frame capture stayed dead for the rest of the session.
    /// That is the case where PresentMon is momentarily locked, which happens right after an
    /// upgrade has replaced it.
    /// </summary>
    private void ScheduleRetry()
    {
        if (_stopping || !_captureWanted) return;

        int generation = ++_captureGeneration;
        _restartCount++;

        _ = Task.Run(async () =>
        {
            await Task.Delay(BackoffFor(_restartCount));
            if (_stopping || !_captureWanted || generation != _captureGeneration) return;
            StartCapture();
        });
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
                // "The time of the Present() call, in seconds, relative to when PresentMon
                // started recording." Optional: if a future schema drops or renames it we fall
                // back to the arrival time, which is what this used to do for every frame.
                else if (fields[i].Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase))
                    _headerTimeIndex = i;
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

            // Kept as well, rather than only as a stand-in for the above, because "what
            // reached the monitor" is a reading in its own right and now has its own tile.
            // If the two indices are the same there is only one measurement available, and
            // showing it twice under two names would be a lie about what we know.
            _headerDisplayIndex = displayChange != _headerFrameTimeIndex ? displayChange : -1;

            if (_displayedWanted && _headerDisplayIndex < 0)
                LogService.Warn(nameof(FpsService),
                    "PresentMon did not report a MsBetweenDisplayChange column; displayed frame rate is unavailable.");

            if (_headerTimeIndex < 0)
                LogService.Warn(nameof(FpsService),
                    "PresentMon did not report a TimeInSeconds column; frame windows will use arrival time.");

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
        // Finite, positive, and within reach of reality. "ms <= 0" alone is not enough:
        // double.TryParse accepts "NaN" and "Infinity", and every comparison against NaN is
        // false, so a single such row passed the guard, poisoned the average and put a
        // nonsense frame rate on screen. The upper bound catches a frame time no game
        // produces, ten seconds, which would otherwise drag the average down for a minute.
        if (!double.TryParse(fields[_headerFrameTimeIndex], NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var ms)
            || !double.IsFinite(ms) || ms <= 0 || ms > 10_000) return;

        var swapChain = _headerSwapChainIndex >= 0 && fields.Length > _headerSwapChainIndex
            ? fields[_headerSwapChainIndex]
            : "";

        // Deliberately not a reason to reject the row. A frame that never reached the monitor
        // reports zero here, and above the refresh rate most frames are exactly that, so
        // treating it like the bad frame time above would throw away the majority of a fast
        // game's frames and take the main frame rate down with them. Zero simply means this
        // frame does not contribute to the displayed rate.
        double displayMs = 0;
        if (_headerDisplayIndex >= 0 && fields.Length > _headerDisplayIndex
            && double.TryParse(fields[_headerDisplayIndex], NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var shown)
            && double.IsFinite(shown) && shown > 0 && shown <= 10_000)
        {
            displayMs = shown;
        }

        // When the frame happened, from PresentMon, falling back to now if it did not say.
        long at;
        if (_headerTimeIndex >= 0 && fields.Length > _headerTimeIndex
            && double.TryParse(fields[_headerTimeIndex], NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds))
        {
            at = (long)(seconds * 1000.0);
        }
        else
        {
            at = Environment.TickCount64;
        }

        lock (_lock)
        {
            if (!_bySwapChain.TryGetValue(swapChain, out var samples))
            {
                samples = new Queue<FrameSample>();
                _bySwapChain[swapChain] = samples;
            }

            var sample = new FrameSample(ms, at, displayMs);
            samples.Enqueue(sample);

            // Nothing is recalculated here any more. Recompute averaged the whole window on
            // every single frame, which at high frame rates is quadratic work for a number
            // nobody can read that fast. The timer does it instead, a few times a second.
            _lastFrameArrival = Environment.TickCount64;

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

    /// The newest frame time seen on any chain, which is "now" as far as the windows are
    /// concerned. Caller holds <see cref="_lock"/>.
    private long NewestFrameTime()
    {
        long newest = long.MinValue;

        foreach (var samples in _bySwapChain.Values)
            if (samples.Count > 0 && samples.Last().At > newest)
                newest = samples.Last().At;

        return newest == long.MinValue ? Environment.TickCount64 : newest;
    }

    /// <summary>
    /// Recalculates FPS from the busiest swap chain inside the time window. Caller holds
    /// <see cref="_lock"/>.
    /// </summary>
    /// <summary>
    /// Recalculates the current frame rate from the busiest swap chain. Caller holds
    /// <see cref="_lock"/>.
    /// </summary>
    private void Recompute()
    {
        long now = NewestFrameTime();

        Queue<FrameSample>? busiest = null;
        string busiestKey = "";
        int busiestCount = 0;
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

            if (samples.Count > busiestCount)
            {
                busiest      = samples;
                busiestKey   = key;
                busiestCount = samples.Count;
            }
        }

        // Chains come and go as menus, videos and overlays open and close.
        if (empty != null) foreach (var key in empty) _bySwapChain.Remove(key);

        UpdateDominantChain(busiestKey, busiestCount);

        // Averaged from a running total rather than by walking the queue, and only from the
        // chain we settled on. Two samples minimum: a single frame time is noise, not a rate.
        var chain = _dominantChain.Length > 0 && _bySwapChain.TryGetValue(_dominantChain, out var current)
            ? current
            : busiest;

        if (chain is not { Count: >= 2 })
        {
            CurrentFps   = null;
            DisplayedFps = null;
            return;
        }

        double total = 0;

        // Displayed frames are a subset of produced ones, counted separately in the same pass.
        // Averaging only the frames that were shown gives the mean gap between them, which is
        // the rate the monitor saw; including the zeros would report the rate of production
        // again under a different name.
        double shownTotal = 0;
        int    shownCount = 0;

        foreach (var sample in chain)
        {
            total += sample.Ms;

            if (sample.DisplayMs > 0)
            {
                shownTotal += sample.DisplayMs;
                shownCount++;
            }
        }

        double mean = total / chain.Count;
        CurrentFps = mean > 0 ? (float)(1000.0 / mean) : null;

        // Two shown frames minimum, for the same reason as above: one interval is not a rate.
        DisplayedFps = _displayedWanted && shownCount >= 2
            ? (float)(1000.0 / (shownTotal / shownCount))
            : null;
    }

    /// <summary>
    /// Decides which swap chain is the one being played, and resists changing its mind.
    ///
    /// A switch throws away the minute of history behind the 1% low, so it has to be worth it.
    /// The previous rule was simply whichever chain had the most frames at that instant, and a
    /// game presenting on comparable chains flipped between them constantly, wiping the history
    /// each time and leaving the 1% low permanently blank while nothing was actually wrong.
    ///
    /// A challenger now has to be clearly ahead, and stay ahead, before it takes over. Caller
    /// holds <see cref="_lock"/>.
    /// </summary>
    private void UpdateDominantChain(string busiestKey, int busiestCount)
    {
        if (busiestKey.Length == 0)
        {
            // Nothing is presenting at all. Not a switch, so the history is left alone: it
            // ages out on its own if frames really have stopped.
            return;
        }

        if (_dominantChain.Length == 0)
        {
            _dominantChain      = busiestKey;
            _chainCandidate     = "";
            _chainCandidateWins = 0;
            return;
        }

        if (busiestKey == _dominantChain)
        {
            _chainCandidate     = "";
            _chainCandidateWins = 0;
            return;
        }

        // The incumbent may have disappeared entirely, in which case there is nothing to
        // defend and no reason to wait.
        if (!_bySwapChain.TryGetValue(_dominantChain, out var incumbent) || incumbent.Count == 0)
        {
            SwitchChain(busiestKey);
            return;
        }

        // Ahead, but not by enough to be a different chain rather than a busy moment.
        if (busiestCount < incumbent.Count * ChainSwitchLead)
        {
            _chainCandidate     = "";
            _chainCandidateWins = 0;
            return;
        }

        if (busiestKey != _chainCandidate)
        {
            _chainCandidate     = busiestKey;
            _chainCandidateWins = 1;
            return;
        }

        if (++_chainCandidateWins >= ChainSwitchMargin) SwitchChain(busiestKey);
    }

    private void SwitchChain(string key)
    {
        _dominantChain      = key;
        _chainCandidate     = "";
        _chainCandidateWins = 0;

        // The long history belonged to something else.
        _lowSamples.Clear();
        OnePercentLowFps   = null;
        OnePercentLowP1Fps = null;
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
            // Judged on when frames last reached us, not on their own timestamps. Frames
            // stopping is a property of the capture, and their timestamps are relative to
            // when PresentMon started, so the two clocks are not comparable.
            bool anyRecent = _lastFrameArrival != 0
                          && Environment.TickCount64 - _lastFrameArrival <= StaleAfterMs;

            if (!anyRecent)
            {
                _bySwapChain.Clear();
                _lowSamples.Clear();
                _dominantChain      = "";
                _chainCandidate     = "";
                _chainCandidateWins = 0;
                CurrentFps          = null;
                DisplayedFps        = null;
                OnePercentLowFps    = null;
                OnePercentLowP1Fps  = null;
                return;
            }

            Recompute();
            RecomputeOnePercentLow(NewestFrameTime());
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
            OnePercentLowFps   = null;
            OnePercentLowP1Fps = null;
            return;
        }

        while (_lowSamples.Count > 0 && now - _lowSamples.Peek().At > LowWindowMs)
            _lowSamples.Dequeue();

        // Enough frames and enough time. A count alone is not a duration: two hundred frames
        // is nearly seven seconds at 30fps and under one at 240, so this used to appear almost
        // at once on a fast machine while describing a moment rather than a minute.
        long span = _lowSamples.Count > 0 ? now - _lowSamples.Peek().At : 0;

        if (_lowSamples.Count < LowMinSamples || span < LowMinSpanMs)
        {
            // "--" rather than a figure built from too little data.
            OnePercentLowFps   = null;
            OnePercentLowP1Fps = null;
            return;
        }

        int count = _lowSamples.Count;

        // Rented rather than allocated. Sixty seconds of frames is large enough to land on the
        // large object heap, and this runs several times a second for as long as a game is
        // open, so allocating it each time is a steady stream of collectable garbage for a
        // number that has not changed much.
        var times = ArrayPool<double>.Shared.Rent(count);

        try
        {
            int next = 0;
            foreach (var sample in _lowSamples) times[next++] = sample.Ms;
            Array.Sort(times, 0, count);

            // Rounded up, not down. Integer division took 0.8% at 250 frames and 0.67% at 299,
            // so the "1%" low was quietly a different proportion at every window size.
            int worst = Math.Max(1, (int)Math.Ceiling(count / 100.0));

            double total = 0;
            for (int i = count - worst; i < count; i++) total += times[i];

            double averageMs = total / worst;
            OnePercentLowFps = averageMs > 0 ? (float)(1000.0 / averageMs) : null;

            // The other convention, from the same sorted window: the frame time at the 99th
            // percentile rather than the mean of everything beyond it. Nearest rank, which is
            // what RTSS and CapFrameX use, so the two numbers can actually be compared.
            //
            // This index is never past the start of the block averaged above, so the boundary
            // frame time is always the fastest of the frames that average is taken over. That
            // is why P1 can only ever read the same or higher, never lower — a property of the
            // definitions rather than of any particular capture.
            int boundary = Math.Clamp((int)Math.Ceiling(0.99 * count) - 1, 0, count - 1);
            double boundaryMs = times[boundary];
            OnePercentLowP1Fps = boundaryMs > 0 ? (float)(1000.0 / boundaryMs) : null;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(times);
        }
    }

    /// <summary>
    /// Processes whose presents are not a frame rate anyone wants to see.
    ///
    /// Windows composites its own shell continuously, so with the desktop focused Pulse was
    /// reporting explorer's compositing as a frame rate and showing a confident 4 to 14 fps
    /// when nothing was running at all. Pulse itself is excluded for the same reason: the
    /// overlay presents, so clicking the control panel measured us measuring ourselves.
    ///
    /// Deliberately not a rule like "below fifteen fps means the desktop", which would have
    /// hidden genuinely struggling games, and deliberately not a fullscreen requirement,
    /// because borderless and windowed games are perfectly normal.
    /// </summary>
    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "ShellHost",
        "TextInputHost",
        "LockApp",
        "SystemSettings",
        "ApplicationFrameHost",
        "dwm",
    };

    private static bool IsWorthMeasuring(uint pid)
    {
        if (pid == 0) return false;
        if (pid == (uint)Environment.ProcessId) return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return !IgnoredProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            // Gone already, or not ours to inspect. Measuring something we cannot identify is
            // worse than measuring nothing.
            return false;
        }
    }

    private void RefreshForegroundTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);

        // Nothing worth measuring is focused, so stop measuring rather than reporting whatever
        // the shell happens to be doing.
        if (!IsWorthMeasuring(pid))
        {
            if (_foregroundPid == 0) return;

            _foregroundPid = 0;
            lock (_lock)
            {
                _bySwapChain.Clear();
                _lowSamples.Clear();
                _dominantChain   = "";
                CurrentFps         = null;
                DisplayedFps       = null;
                OnePercentLowFps   = null;
                OnePercentLowP1Fps = null;
            }
            return;
        }

        if (pid == _foregroundPid) return;

        _foregroundPid = pid;
        lock (_lock)
        {
            // Everything collected belonged to the app we just left, the minute of history
            // behind the 1% low included — otherwise alt-tabbing out of a game blanked FPS
            // but left the game's 1% low sitting there next to it.
            _bySwapChain.Clear();
            _lowSamples.Clear();
            _dominantChain   = "";
            CurrentFps         = null;
            DisplayedFps       = null;
            OnePercentLowFps   = null;
            OnePercentLowP1Fps = null;
        }
    }

    public void Dispose()
    {
        _targetTimer.Stop();
        StopCapture();
    }
}
