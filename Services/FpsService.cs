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
    /// </summary>
    private readonly record struct FrameSample(double Ms, long At);

    /// Averaging window, and how long without frames before the reading is considered dead.
    private const int FrameWindowMs = 1000;
    private const int StaleAfterMs  = 2000;

    /// <summary>
    /// How much history the 1% low is measured over: the last <see cref="LowMaxSamples"/>
    /// frames, and never more than <see cref="LowWindowMs"/> of them.
    /// </summary>
    /// <remarks>
    /// A count and a duration together, because either alone gets one end of the range wrong.
    ///
    /// This used to be sixty seconds flat, and the complaint was that a single stutter stayed
    /// in the number for a full minute after the game had recovered. Counting frames instead
    /// fixes that where it hurts most, since a fast machine fills the buffer quickly:
    ///
    ///     240 fps    8.3 s        100 fps   20.0 s
    ///     143 fps   14.0 s         60 fps   33.3 s -> capped to 30
    ///                              30 fps   66.7 s -> capped to 30
    ///
    /// But frames alone would have made it worse for the machines that stutter most: at 30fps
    /// two thousand frames is sixty-seven seconds, longer than the window being complained
    /// about. Hence the cap. Every frame rate now recovers faster than it used to, and none
    /// recovers slower.
    ///
    /// Two thousand is also about the smallest buffer the statistic survives. The 99th
    /// percentile is a rank, so the number is set by the twenty-first worst frame in the
    /// buffer; at a thousand frames it would be the eleventh, and one hitch would swing the
    /// tile and then vanish from it seconds later.
    ///
    /// A round two thousand rather than 2,048, which is what this was first written as out of
    /// habit. Nothing here indexes by powers of two, the statistic is identical either way, and
    /// the tile tells the user "the last 2,000 frames". A tooltip that is nearly true is the
    /// same small dishonesty as saying megabits are eight times megabytes.
    /// </remarks>
    private const int LowWindowMs   = 30_000;
    private const int LowMaxSamples = 2_000;

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
    /// capture, so it is the one thing that still belongs on the local clock. That clock is
    /// AwakeClock, so a machine that slept is not mistaken for a capture that went quiet.
    private long _lastFrameArrival;

    private int _headerTimeIndex = -1;

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

    /// <summary>
    /// Raised a few times a second once frame capture is running, whether or not the numbers
    /// changed. Subscribers read CurrentFps and OnePercentLowFps.
    ///
    /// Raised on the UI thread, since the timer behind it is a DispatcherTimer.
    /// </summary>
    public event EventHandler? Updated;

    public float? CurrentFps { get; private set; }

    /// <summary>
    /// The 1% low: the frame rate at the 99th percentile of recent frame times.
    /// </summary>
    /// <remarks>
    /// The percentile method, which is what RTSS, MSI Afterburner and CapFrameX report and what
    /// people mean when they compare 1% lows. Frame times are sorted and the one on the
    /// boundary is converted to a rate; the 1st percentile of frame *rates* is the same thing
    /// as the 99th percentile of frame *times*, which is why the arithmetic stays in
    /// milliseconds throughout and converts exactly once, at the end.
    ///
    /// Pulse used to offer a second reading alongside this, the average of everything past the
    /// boundary, on the grounds that a single deep stutter moves an average and does not move a
    /// boundary. That was true but not worth a second tile: two rows whose names differed by
    /// three characters, and no way for anyone to tell which one to believe. One number, the
    /// one the rest of the world computes.
    ///
    /// Null until there are enough samples for a percentile to mean anything.
    /// </remarks>
    public float? OnePercentLowFps { get; private set; }

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
            ReportSilentCapture();
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

        bool lowOn       = active.Contains("fps_1low");
        bool wanted      = active.Contains("fps") || lowOn;

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
        // Anything already waiting to restart belongs to the capture being ended here.
        //
        // Only the exit handler and the retry used to move this, so turning the tile off and
        // on again during a restart backoff left the pending work looking current. It then
        // disposed whatever _process had become, which by that point was the capture that had
        // just been started, and launched another beside it. Two PresentMons, the second
        // stopping the first's trace session, and the first left running and deaf.
        _captureGeneration++;

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

        ResetStream();
    }

    /// <summary>
    /// Forgets everything that belonged to one run of PresentMon.
    /// </summary>
    /// <remarks>
    /// All of it together, which is the part that was wrong. Stopping cleared the per-chain
    /// frames and the live rate; restarting cleared three of the four column positions. What
    /// neither cleared was the long history behind the 1% low, the chain chosen to feed it, or
    /// the position of the time column.
    ///
    /// That matters because PresentMon's timestamps are relative to the start of its own
    /// capture. A capture that restarts quickly therefore begins numbering again from about
    /// zero, and those frames were appended behind samples timed from the previous run. Nothing
    /// downstream can make sense of that: the newest frame in the queue is then older than the
    /// oldest, so the windows measured in time either expire everything or nothing, and the 1%
    /// low sat blank until two thousand frames had pushed the old ones out.
    /// </remarks>
    private void ResetStream()
    {
        _headerProcessIdIndex = -1;
        _headerFrameTimeIndex = -1;
        _headerSwapChainIndex = -1;
        _headerTimeIndex      = -1;

        lock (_lock)
        {
            _bySwapChain.Clear();
            _lowSamples.Clear();
            _dominantChain      = "";
            _chainCandidate     = "";
            _chainCandidateWins = 0;
            _lastFrameArrival   = 0;
            CurrentFps          = null;
            OnePercentLowFps    = null;
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
        var lived = TimeSpan.FromMilliseconds(AwakeClock.Milliseconds - _captureStartedAt);
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
            if (_disposed || _stopping || !_captureWanted || generation != _captureGeneration) return;

            try { _process?.Dispose(); } catch { }
            _process = null;

            ResetStream();

            StartCapture();
        });
    }

    /// <summary>
    /// Says so, once, when a capture has been running for a while and heard nothing at all.
    /// </summary>
    /// <remarks>
    /// This is the line that was missing. PresentMon can start, report no error, stay alive and
    /// deliver not one row, which is what a starved ETW session looks like from outside. It
    /// happened here for an entire afternoon and produced no log entry of any kind, so a report
    /// of "the frame rate shows nothing" came with a log that said nothing either.
    ///
    /// Warn rather than error, and once rather than repeatedly: a machine sitting on an empty
    /// desktop with nothing presenting is also silent, and that is not a fault.
    /// </remarks>
    private void ReportSilentCapture()
    {
        if (_reportedSilence || _process is null || !_captureWanted) return;

        // Measured from the last row, falling back to the start of the capture when none has
        // ever arrived.
        //
        // It used to ask only whether any row had ever been seen, which meant the check
        // switched itself off permanently the moment the first one did. A session that is
        // starved after it has been working looks exactly like the case this exists for and
        // was the one shape of it that could never be reported. Still said once per capture,
        // because a machine sitting on an empty desktop is also silent and that is not a fault
        // worth repeating in the log.
        long last  = Interlocked.Read(ref _lastRowAt);
        long since = AwakeClock.Milliseconds - (last == 0 ? _captureStartedAt : last);

        if (since < SilentCaptureAfter.TotalMilliseconds) return;

        _reportedSilence = true;

        LogService.Warn(nameof(FpsService),
            $"Frame capture has produced no data in {since / 1000}s, though it is "
            + (last == 0 ? "still running and has produced none at all. " : "still running. ")
            + "Either nothing on this machine is presenting frames, or the trace session "
            + "is being starved of events. Abandoned trace sessions from other tools are the usual cause; "
            + $"'logman query -ets' lists them and ours is called {TraceSessionName}.");
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

    /// <summary>
    /// The name of the ETW trace session PresentMon runs under.
    /// </summary>
    /// <remarks>
    /// Constant, and deliberately not "PresentMon". Constant so that a session left behind by a
    /// previous run is stopped by the next one rather than accumulating, and not the default so
    /// that stopping it can never reach another tool's capture. See the note where it is used.
    /// </remarks>
    internal const string TraceSessionName = "PulseMonitor";

    /// <summary>
    /// How long a capture may run without a single row arriving before that is worth saying.
    /// </summary>
    /// <remarks>
    /// PresentMon can start cleanly, stay running, and deliver nothing at all: that is what a
    /// starved ETW session looks like from outside, and it produced no log entry of any kind
    /// while frame capture was dead for an entire afternoon. Not an error, because a machine
    /// with nothing presenting also produces no rows, but it is the single most useful line
    /// anyone could have when reporting that the frame rate reads "--".
    /// </remarks>
    private static readonly TimeSpan SilentCaptureAfter = TimeSpan.FromSeconds(90);

    /// Rows parsed from PresentMon since this capture started, counting every process rather
    /// than only the foreground one, so this measures the capture rather than the target.
    private long _rowsSeen;

    /// When the last row arrived, on AwakeClock. Separate from a frame's own timestamp, which
    /// is relative to the capture and says nothing about whether one is still flowing.
    private long _lastRowAt;

    private bool _reportedSilence;

    private void StartCapture()
    {
        // Nothing starts a capture after Dispose. A retry can already be waiting when Pulse is
        // shutting down, and starting PresentMon on the way out leaves a process behind.
        if (_disposed) return;

        // This capture is now the current one, so anything still waiting on the old one stands
        // down. Written here as well as in StopCapture because a restart arrives through this
        // method without passing through that one.
        _captureGeneration++;

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
                    // --session_name is what makes --stop_existing_session safe, and the name
                    // has to be both ours alone and the same every time. Both halves matter,
                    // and getting either wrong has already caused a bug.
                    //
                    // Unnamed, the session is called "PresentMon", which every other tool
                    // built on it also uses. --stop_existing_session then stopped a capture
                    // belonging to CapFrameX, OCAT, Intel's own Graphics Software, or someone
                    // running PresentMon by hand.
                    //
                    // Named per process id, nothing is ever stopped, because the name is new
                    // every launch. An ETW session outlives the process that made it and has
                    // to be closed explicitly, and this one is killed rather than asked to
                    // stop, so every run of Pulse abandoned a session that stayed subscribed
                    // to the graphics providers with nobody reading it. They accumulated
                    // without limit. Six of them was enough to make Windows drop around
                    // 136,000 events per capture, which broke frame capture for the whole
                    // machine: our own readings went blank and so did NVIDIA's overlay, while
                    // tools that inject into the game rather than listening to ETW carried on
                    // working. Fixing one interoperability bug had created a worse one.
                    //
                    // A constant name of our own restores the self-cleaning without touching
                    // anyone else: a leaked session is stopped by the next launch, so at most
                    // one can exist at a time. Pulse is single instance by mutex, so a fixed
                    // name never collides with itself.
                    Arguments              = "--output_stdout --no_console_stats --v1_metrics "
                                           + $"--session_name {TraceSessionName} --stop_existing_session",
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

            _captureStartedAt = AwakeClock.Milliseconds;
            _rowsSeen         = 0;
            _lastRowAt        = 0;
            _reportedSilence  = false;

            // Said out loud, because until now a successful start logged nothing at all. A
            // report of "the frame rate shows nothing" arrived with a log that could not
            // distinguish never started, started and died, or started and heard silence.
            LogService.Info(nameof(FpsService),
                $"Frame capture started (pid {_process.Id}, trace session {TraceSessionName}).");

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
        if (_disposed || _stopping || !_captureWanted) return;

        int generation = ++_captureGeneration;
        _restartCount++;

        _ = Task.Run(async () =>
        {
            await Task.Delay(BackoffFor(_restartCount));
            if (_disposed || _stopping || !_captureWanted || generation != _captureGeneration) return;
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

            if (_headerTimeIndex < 0)
                LogService.Warn(nameof(FpsService),
                    "PresentMon did not report a TimeInSeconds column; frame windows will use arrival time.");

            return;
        }

        if (_headerProcessIdIndex < 0 || _headerFrameTimeIndex < 0) return;
        if (fields.Length <= Math.Max(_headerProcessIdIndex, _headerFrameTimeIndex)) return;

        // Counted before the foreground filter, so this says whether the capture is receiving
        // anything at all rather than whether the app being watched is presenting.
        Interlocked.Increment(ref _rowsSeen);
        Interlocked.Exchange(ref _lastRowAt, AwakeClock.Milliseconds);


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
            // Checked again, inside the lock this time.
            //
            // The test above happens on the reader thread while the foreground target is
            // changed on the interface thread, and changing it clears these queues. A frame
            // that passed the test a moment earlier could therefore be added after the clear,
            // putting one app's frames into the history that had just been emptied for
            // another. Rare, small, and wrong: the readings are meant to belong to one app.
            if (pid != _foregroundPid) return;

            if (!_bySwapChain.TryGetValue(swapChain, out var samples))
            {
                samples = new Queue<FrameSample>();
                _bySwapChain[swapChain] = samples;
            }

            var sample = new FrameSample(ms, at);
            samples.Enqueue(sample);

            // Nothing is recalculated here any more. Recompute averaged the whole window on
            // every single frame, which at high frame rates is quadratic work for a number
            // nobody can read that fast. The timer does it instead, a few times a second.
            _lastFrameArrival = AwakeClock.Milliseconds;

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
            CurrentFps = null;
            return;
        }

        double total = 0;
        foreach (var sample in chain) total += sample.Ms;

        double mean = total / chain.Count;
        CurrentFps = mean > 0 ? (float)(1000.0 / mean) : null;
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
        OnePercentLowFps = null;
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
                          && AwakeClock.Milliseconds - _lastFrameArrival <= StaleAfterMs;

            if (!anyRecent)
            {
                _bySwapChain.Clear();
                _lowSamples.Clear();
                _dominantChain      = "";
                _chainCandidate     = "";
                _chainCandidateWins = 0;
                CurrentFps          = null;
                OnePercentLowFps    = null;
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
            OnePercentLowFps = null;
            return;
        }

        while (_lowSamples.Count > 0 && now - _lowSamples.Peek().At > LowWindowMs)
            _lowSamples.Dequeue();

        // Enough frames and enough time. A count alone is not a duration: two hundred frames
        // is nearly seven seconds at 30fps and under one at 240, so this used to appear almost
        // at once on a fast machine while describing a moment rather than a window.
        long span = _lowSamples.Count > 0 ? now - _lowSamples.Peek().At : 0;

        // Or a full buffer, whatever time it took to fill.
        //
        // The span test on its own had a ceiling nobody could see. This keeps two thousand
        // frames, and three seconds of them means about 667 frames per second; above that the
        // retained history is never three seconds long, so the tile read "--" for as long as
        // the game ran however steady it was. Seven hundred frames a second is not an exotic
        // number in an esports title on strong hardware, and it is exactly the audience most
        // likely to be looking at this tile.
        //
        // The span was only ever standing in for "enough evidence", and a full buffer is that
        // by construction: two thousand frames is more than the percentile needs whether they
        // arrived over two seconds or twenty.
        bool enough = _lowSamples.Count >= LowMaxSamples
                   || (_lowSamples.Count >= LowMinSamples && span >= LowMinSpanMs);

        if (!enough)
        {
            // "--" rather than a figure built from too little data.
            OnePercentLowFps = null;
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
            // Sorted shortest frame time to longest, so the stutters are at the end. Frame
            // times, never per-frame rates: computing a rate for each frame and then taking the
            // lowest 1% of those is a different and wrong calculation, and it is the mistake
            // this metric is most often implemented with.
            int next = 0;
            foreach (var sample in _lowSamples) times[next++] = sample.Ms;
            Array.Sort(times, 0, count);

            // Nearest rank: the 99th percentile is the value at rank ceil(0.99 * n), which is
            // one lower as a zero-based index. Truncating 0.99 * n instead, as some
            // implementations do, picks the next sample along; the two agree except when the
            // frame count is an exact multiple of a hundred, and then by a single sample.
            int boundary = Math.Clamp((int)Math.Ceiling(0.99 * count) - 1, 0, count - 1);
            double boundaryMs = times[boundary];

            // Converted to a rate exactly once, here at the end.
            OnePercentLowFps = boundaryMs > 0 ? (float)(1000.0 / boundaryMs) : null;
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
                OnePercentLowFps = null;
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
            OnePercentLowFps = null;
        }
    }

    public void Dispose()
    {
        // Permanent, and set before anything else. A restart can already be counting down when
        // Pulse is asked to close, and the checks it makes on waking are about whether capture
        // is still wanted rather than about whether this object is still alive. Without these
        // two lines that restart could start PresentMon after the last thing that would ever
        // have stopped it had already run.
        _disposed      = true;
        _captureWanted = false;

        _targetTimer.Stop();
        StopCapture();
    }

    private bool _disposed;
}
