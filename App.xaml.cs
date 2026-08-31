using System.IO;
using System.Windows;
using Pulse.Services;
using Pulse.ViewModels;
using Pulse.Views;

using WinApplication = System.Windows.Application;

namespace Pulse;

public partial class App : WinApplication
{
    private const string MutexName  = "Global\\PulseMonitor_SingleInstance";
    private const string ShowUiName = "Global\\PulseMonitor_ShowUI";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showUiSignal;

    /// False in a second instance that was rejected before anything was set up. OnExit
    /// checks this before touching the lazy singletons.
    private bool _initialized;

    private MainWindow?    _mainWindow;
    private OverlayWindow? _overlayWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _overlayToggleItem;

    /// <summary>Whether the overlay is currently visible.</summary>
    public bool IsOverlayVisible => _overlayWindow != null && _overlayWindow.IsLoaded && _overlayWindow.IsVisible;

    /// The live overlay, for the settings panel's X/Y position controls. Null before the
    /// overlay has been created or after it has been closed.
    public OverlayWindow? Overlay => _overlayWindow is { IsLoaded: true } ? _overlayWindow : null;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Registering the startup task is a job, not a launch. The installer calls this so
        // there is exactly one definition of that task rather than the installer and the
        // settings toggle each building their own schtasks command line — which is how three
        // harmful defaults went unnoticed in it for so long.
        //
        // Handled before the single-instance mutex: this has to work while Pulse is already
        // running, which is exactly the case during an upgrade.
        if (e.Args.Contains("--install-startup-task"))
        {
            Environment.ExitCode = Services.StartupTask.Install(Environment.ProcessPath ?? "") ? 0 : 1;
            Shutdown();
            return;
        }

        if (e.Args.Contains("--remove-startup-task"))
        {
            Environment.ExitCode = Services.StartupTask.Remove() ? 0 : 1;
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Pulse is already running. Ask the live instance to bring its control panel
            // forward instead of vanishing without a trace — silently doing nothing is what
            // made it impossible to tell which build was running when several were installed.
            SignalRunningInstance();
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _initialized = true;

        // Before anything else can fail. This also reports on the previous run if it never
        // shut down, which is the only way a death that reaches no managed code — a driver
        // fault, a forced termination — leaves any trace at all.
        InstallCrashReporting();
        Services.LogService.BeginSession(Services.UpdateService.CurrentVersionLabel);

        StartShowUiListener();

        // Housekeeping and startup-task reconciliation, off the startup path: between them
        // these touch the disk and shell out to schtasks twice, and none of it needs to
        // finish before the overlay appears.
        Task.Run(() =>
        {
            try { UpdateService.CleanupStaleDownloads(); } catch { }
            try { CleanupStaleExtractDirectories();     } catch { }

            try
            {
                // Settings.json can disagree with reality — the installer's "start with
                // Windows" tickbox creates the task without going through us, and a task
                // left by another install may point at an exe that no longer exists.
                // Saving is marshalled back because SettingsChanged subscribers touch
                // bound collections.
                if (SettingsService.Instance.ReconcileStartupTask())
                    Dispatcher.Invoke(SettingsService.Instance.Save);
            }
            catch { }
        });

        // Initialise singletons (starts hardware polling).
        //
        // Frame capture first, and on this thread deliberately. It owns a DispatcherTimer,
        // which belongs to whichever thread builds it, and the readings that fill in the FPS
        // tiles arrive on a background thread with no message loop of its own.
        _ = FpsService.Instance;
        _ = HardwareService.Instance;
        _ = OverlayViewModel.Instance;
        _ = SettingsViewModel.Instance;

        SetupTrayIcon();

        ShowOverlay();

        if (!e.Args.Contains("--startup"))
            ShowControlPanel();

        CheckForUpdatesOnStartup();
    }

    /// <summary>
    /// Removes .NET single-file extraction folders left behind by previous builds.
    ///
    /// Pulse ships with IncludeNativeLibrariesForSelfExtract, so each build unpacks its
    /// native libraries into %TEMP%\.net\Pulse\&lt;content-hash&gt;\. The hash changes every
    /// release, so these accumulate — and to anyone searching their disk for "Pulse" they
    /// look exactly like several installations, which is what prompted this.
    ///
    /// The folder in use is protected twice over: its libraries are loaded and therefore
    /// locked, and it was written at launch so the age check skips it anyway.
    /// </summary>
    private static void CleanupStaleExtractDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), ".net", "Pulse");
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(12);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                Directory.Delete(dir, recursive: true);
            }
            catch { }   // still in use, or not ours to remove — leave it be
        }
    }

    /// Nudges the already-running Pulse to show itself. Best effort: if the signal cannot be
    /// opened we simply exit as before, which is no worse than the old behaviour.
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowUiName, out var handle))
                using (handle) handle.Set();
        }
        catch { }
    }

    /// Waits for a later launch to signal us, then surfaces the control panel. Runs on a
    /// background thread so it can never hold up shutdown.
    private void StartShowUiListener()
    {
        try
        {
            _showUiSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowUiName);
        }
        catch
        {
            return;   // no signal available; second launches just exit quietly
        }

        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showUiSignal.WaitOne()) return;
                }
                catch
                {
                    return;   // handle disposed during shutdown
                }

                try { ShowControlPanel(); } catch { return; }
            }
        })
        {
            IsBackground = true,
            Name         = "Pulse show-UI listener",
        }.Start();
    }

    private async void CheckForUpdatesOnStartup()
    {
        try
        {
            await SettingsViewModel.Instance.CheckForUpdatesAsync(false);
            if (SettingsViewModel.Instance.IsUpdateAvailable)
            {
                _trayIcon?.ShowBalloonTip(6000, "Pulse update available",
                    $"{SettingsViewModel.Instance.BannerVersion} is ready to download. Open the control panel to update.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch { }
    }

    public void ShowOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded)
                _overlayWindow = new OverlayWindow();
            _overlayWindow.Show();
            _overlayWindow.Topmost = true;
            UpdateMainWindowButton();
            UpdateTrayMenu();
        });
    }

    public void HideOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            _overlayWindow?.Hide();
            UpdateMainWindowButton();
            UpdateTrayMenu();
        });
    }

    private void ToggleOverlay()
    {
        if (IsOverlayVisible) HideOverlay(); else ShowOverlay();
    }

    private void UpdateTrayMenu()
    {
        if (_overlayToggleItem != null)
            _overlayToggleItem.Text = IsOverlayVisible ? "Hide Overlay" : "Show Overlay";
    }

    private void UpdateMainWindowButton()
    {
        if (_mainWindow != null && _mainWindow.IsLoaded)
            _mainWindow.UpdateOverlayButton();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text    = "Pulse — Refora Technologies",
            Visible = true,
        };

        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/Icons/pulse.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
            if (streamInfo != null)
            {
                _trayIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
            }
            else
            {
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        _overlayToggleItem = new System.Windows.Forms.ToolStripMenuItem("Hide Overlay", null,
            (_, _) => ToggleOverlay());

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer        = new TrayMenuRenderer(),
            BackColor       = System.Drawing.ColorTranslator.FromHtml("#16132A"),
            ForeColor       = System.Drawing.ColorTranslator.FromHtml("#E5E2F4"),
            Font            = new System.Drawing.Font("Segoe UI", 9F),
            ShowImageMargin = false,
        };
        menu.Items.Add("Open Control Panel", null, (_, _) => ShowControlPanel());
        menu.Items.Add(_overlayToggleItem);
        menu.Items.Add("-");
        menu.Items.Add("Exit Pulse", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick     += (_, _) => ShowControlPanel();
    }

    public void ShowControlPanel()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow == null || !_mainWindow.IsLoaded)
                _mainWindow = new MainWindow();
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    /// <summary>
    /// True once Pulse is genuinely quitting, so windows know to close rather than hide.
    /// Without it, honouring "minimize to tray" in OnClosing would cancel the close that
    /// Shutdown itself performs, and Exit would do nothing.
    /// </summary>
    public static bool IsExiting { get; private set; }

    private void ExitApp()
    {
        IsExiting = true;
        try { ViewModels.SettingsViewModel.Instance.FlushPendingSave(); } catch { }
        _trayIcon?.Dispose();
        HardwareService.Instance.Dispose();
        FpsService.Instance.Dispose();
        Dispatcher.Invoke(() => Shutdown());
    }

    /// <summary>
    /// Records unhandled failures instead of losing them.
    ///
    /// None of this keeps Pulse alive; an unhandled exception ends the process either way.
    /// It only means the reason is written down first. Note that it cannot catch everything:
    /// a fault inside a driver corrupts the process state, and the runtime terminates without
    /// running managed handlers at all. Those are what the session marker is for.
    /// </summary>
    private void InstallCrashReporting()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var error = args.ExceptionObject as Exception;
            if (error != null) Services.LogService.Error("Crash", "Unhandled exception; Pulse is closing", error);
            else               Services.LogService.Warn ("Crash", "Unhandled non-exception failure; Pulse is closing");
        };

        DispatcherUnhandledException += (_, args) =>
            Services.LogService.Error("Crash", "Unhandled exception on the UI thread", args.Exception);

        // Faults on background tasks nobody awaited. Silent until now, and they are exactly
        // the kind that make an app "just stop working" with no explanation.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.LogService.Error("Crash", "Unobserved background task failure", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        // Only touch the services if this instance actually started them. They are lazy
        // singletons, so a rejected second instance would otherwise *construct* them here on
        // its way out — and FpsService's constructor launches PresentMon with
        // --stop_existing_session, silently killing the running instance's frame capture.
        if (_initialized)
        {
            try { HardwareService.Instance.Dispose(); } catch { }
            try { FpsService.Instance.Dispose(); } catch { }
        }

        try { _showUiSignal?.Dispose(); _showUiSignal = null; } catch { }

        // Last, so anything that fails on the way out is recorded before we say we left
        // cleanly. Missing this marker is what tells the next run that we did not.
        if (_initialized) Services.LogService.EndSession();
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); _mutex = null; } catch { }
        base.OnExit(e);
    }
}

/// Matches the tray menu to Pulse's dark violet theme — WinForms' ContextMenuStrip has
/// no XAML-style templating, so this is the ToolStripRenderer equivalent of the WPF
/// ContextMenu style used for the overlay's own right-click menu.
internal sealed class TrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
{
    public TrayMenuRenderer() : base(new TrayMenuColors()) { }

    protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected
            ? System.Drawing.ColorTranslator.FromHtml("#C4B5FD")  // VioletText
            : System.Drawing.ColorTranslator.FromHtml("#E5E2F4"); // TextPrimary
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(System.Windows.Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = e.Item.Bounds;
        using var pen = new System.Drawing.Pen(System.Drawing.ColorTranslator.FromHtml("#221E3C"));
        e.Graphics.DrawLine(pen, bounds.Left + 8, bounds.Height / 2, bounds.Right - 8, bounds.Height / 2);
    }

    protected override void OnRenderToolStripBorder(System.Windows.Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new System.Drawing.Pen(System.Drawing.ColorTranslator.FromHtml("#221E3C"));
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }
}

internal sealed class TrayMenuColors : System.Windows.Forms.ProfessionalColorTable
{
    private static readonly System.Drawing.Color Bg    = System.Drawing.ColorTranslator.FromHtml("#16132A");
    private static readonly System.Drawing.Color Hover = System.Drawing.ColorTranslator.FromHtml("#1E1A38");
    private static readonly System.Drawing.Color Border = System.Drawing.ColorTranslator.FromHtml("#221E3C");

    public override System.Drawing.Color ToolStripDropDownBackground     => Bg;
    public override System.Drawing.Color ImageMarginGradientBegin        => Bg;
    public override System.Drawing.Color ImageMarginGradientMiddle       => Bg;
    public override System.Drawing.Color ImageMarginGradientEnd          => Bg;
    public override System.Drawing.Color MenuItemSelected                => Hover;
    public override System.Drawing.Color MenuItemSelectedGradientBegin   => Hover;
    public override System.Drawing.Color MenuItemSelectedGradientEnd     => Hover;
    public override System.Drawing.Color MenuItemBorder                  => Hover;
    public override System.Drawing.Color MenuBorder                      => Border;
    public override System.Drawing.Color SeparatorDark                   => Border;
    public override System.Drawing.Color SeparatorLight                  => Border;
}
