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
            // Housekeeping only. Both remove leftovers from previous runs, so failing leaves
            // a stale folder on disk and changes nothing about this one.
            try { UpdateService.CleanupStaleDownloads(); } catch { }   // leftovers only
            try { CleanupStaleExtractDirectories();     } catch { }   // leftovers only

            // The same idea, for something an earlier Pulse left outside its own folders: one
            // trace session per launch, named after the process id, never released. Nothing
            // that ships now knows those names, so upgrading alone would leave frame capture
            // broken machine wide on exactly the machines that suffered it. Handles its own
            // failures and says nothing when there is nothing to clear.
            TraceSessionCleanup.Run();

            try
            {
                // Settings.json can disagree with reality — the installer's "start with
                // Windows" tickbox creates the task without going through us, and a task
                // left by another install may point at an exe that no longer exists.
                // Saving is marshalled back because SettingsChanged subscribers touch
                // bound collections.
                if (SettingsService.Instance.ReconcileStartupTask())
                {
                    Dispatcher.Invoke(() =>
                    {
                        SettingsService.Instance.Save();

                        // The panel cached the old value on the way up, so without this it goes
                        // on showing what settings.json said before the check. That is only
                        // ever wrong on a machine where the two disagreed, which is exactly the
                        // machine whose owner is asking why startup does not work.
                        SettingsViewModel.Instance.RefreshStartWithWindows();
                    });
                }
            }
            catch (Exception ex)
            {
                // Pulse runs perfectly well without this, but the toggle in the panel may now
                // disagree with what Windows will actually do at logon, and that is exactly
                // the sort of thing someone reports as "startup does not work" with nothing
                // in the log to go on.
                Services.LogService.Error(nameof(App),
                    "Could not reconcile the startup task; the setting may not match reality", ex);
            }
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

        // After the view models, because a shortcut arriving before they exist would build one
        // from the message pump. Registered here rather than lazily so a combination claimed by
        // another program is reported at startup, not the first time the key is pressed.
        HotkeyService.Instance.Pressed += (_, action) => RunShortcut(action);
        HotkeyService.Instance.Apply();

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
        catch (Exception ex)
        {
            // Silently not checking for updates is worse than not checking: nobody would ever
            // find out. Deliberately not shown to the user, since a failed background check on
            // startup is not something to interrupt anyone about.
            Services.LogService.Error(nameof(App), "The startup update check failed", ex);
        }
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
    /// Carries out a keyboard shortcut.
    /// </summary>
    /// <remarks>
    /// Driven straight from the hotkey message on purpose. Windows grants foreground rights to
    /// the process it delivers a hotkey to, and only for that moment — so activating the panel
    /// from here brings it genuinely to the front, where the same call made a moment later
    /// would only flash it in the taskbar.
    /// </remarks>
    private void RunShortcut(Models.ShortcutAction action)
    {
        switch (action)
        {
            case Models.ShortcutAction.ToggleOverlay:
                ToggleOverlay();
                break;

            case Models.ShortcutAction.ToggleControlPanel:
                ToggleControlPanel();
                break;

            case Models.ShortcutAction.ToggleCompactMode:
                // Deliberately does not show the overlay. A key labelled "switch compact mode"
                // that also unhides things is doing two jobs, and the second one is the one
                // nobody asked for. Switching it while hidden simply means it is already in
                // the chosen mode next time it appears.
                SettingsViewModel.Instance.IsCompactMode = !SettingsViewModel.Instance.IsCompactMode;
                break;
        }
    }

    /// <summary>
    /// Opens the panel, brings it forward, or hides it.
    /// </summary>
    /// <remarks>
    /// Three states rather than two. A plain show/hide feels broken when the panel is open but
    /// buried behind a game: the key appears to do nothing, because it is hiding a window the
    /// user cannot see. Buried means bring it forward; in front means put it away.
    /// </remarks>
    private void ToggleControlPanel()
    {
        if (_mainWindow is not { IsLoaded: true } || !_mainWindow.IsVisible)
        {
            ShowControlPanel();
            return;
        }

        if (_mainWindow.IsActive)
        {
            if (SettingsViewModel.Instance.MinimizeToTray) _mainWindow.Hide();
            else _mainWindow.WindowState = WindowState.Minimized;
            return;
        }

        ShowControlPanel();
    }

    /// <summary>
    /// Puts the application icon on a window, and carries on without one if it cannot.
    /// </summary>
    /// <remarks>
    /// Set here rather than in the markup, where it was <c>Icon="pack://..."</c>. That form goes
    /// through ImageSourceConverter while the XAML is being parsed, which decodes the file there
    /// and then, and a failure to decode is not survivable: it surfaces as a XamlParseException
    /// that takes the window with it, and the window is being built during startup, so it takes
    /// Pulse with it too.
    ///
    /// That is not a hypothetical. A user's log shows Pulse launched by the scheduled task at
    /// logon, running for about a second, and dying with
    ///
    ///     XamlParseException: Provide value on 'TypeConverterMarkupExtension' threw an exception
    ///
    /// nine times across two versions, always within a second or two of starting and always
    /// before the overlay was placed. At that point the only converted values in our markup that
    /// can fail are this icon and the bundled font, and the font is resolved lazily at render
    /// time while an icon is decoded immediately. Logon is also exactly when the imaging
    /// components that decode depends on may not yet be ready, which fits a failure that comes
    /// and goes with no pattern anyone could find.
    ///
    /// A window without its icon is a cosmetic loss, and the overlay has no title bar to show
    /// one on at all. Neither is worth losing the application over.
    /// </remarks>
    public static void ApplyIcon(Window window)
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Resources/Icons/pulse.ico"));
            if (resource is null) return;

            using var stream = resource.Stream;

            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count > 0) window.Icon = decoder.Frames[0];
        }
        catch (Exception ex)
        {
            // Said out loud, because if this is what was killing Pulse at logon then this line
            // is the proof, and it now costs an icon instead of the application.
            LogService.Warn(nameof(App),
                $"The window icon could not be loaded: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True once Pulse is genuinely quitting, so windows know to close rather than hide.
    /// Without it, honouring "minimize to tray" in OnClosing would cancel the close that
    /// Shutdown itself performs, and Exit would do nothing.
    /// </summary>
    public static bool IsExiting { get; private set; }

    /// <summary>
    /// The one way out, for every control that means "close Pulse".
    /// </summary>
    /// <remarks>
    /// The tray menu went through here and the panel's close button called Shutdown directly,
    /// which skipped flushing a setting changed a moment earlier and released the global
    /// hotkeys later than it needed to. Alt+F4 did neither and closed only the window: the
    /// overlay is a window too, so WPF had no reason to exit and Pulse carried on running with
    /// no panel. Three controls that all mean the same thing did three different things.
    /// </remarks>
    public void RequestExit() => ExitApp();

    private void ExitApp()
    {
        IsExiting = true;
        // A last chance to write a setting adjusted seconds ago. Exit must not be blocked by
        // it failing, and the panel already reports a save that cannot be written.
        try { ViewModels.SettingsViewModel.Instance.FlushPendingSave(); } catch { }
        _trayIcon?.Dispose();
        HardwareService.Instance.Dispose();
        FpsService.Instance.Dispose();

        // Released before the process ends, not left to it. Windows holds a registered
        // combination for as long as the handle lives, so an orderly exit that skipped this
        // would keep the keys taken from every other application a moment longer than needed.
        HotkeyService.Instance.Dispose();
        Dispatcher.Invoke(() => Shutdown());
    }

    /// <summary>
    /// Records unhandled failures instead of losing them, and survives the ones it can.
    ///
    /// Note that this cannot catch everything: a fault inside a driver corrupts the process
    /// state, and the runtime terminates without running managed handlers at all. Those are
    /// what the session marker and the sensor host are for.
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
        {
            Services.LogService.Error("Crash", "Unhandled exception on the UI thread", args.Exception);

            // Kept alive. This used to log and then let the process end, which for a tray
            // application means the overlay and the tray icon both vanish with no window ever
            // having been open to explain why. A failed button press or a binding that threw
            // while rendering is not a reason to take the whole thing down, and the readings
            // are produced in another process entirely, so they are unaffected either way.
            args.Handled = ShouldKeepRunningAfter();
        };

        // Faults on background tasks nobody awaited. Silent until now, and they are exactly
        // the kind that make an app "just stop working" with no explanation.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.LogService.Error("Crash", "Unobserved background task failure", args.Exception);
            args.SetObserved();
        };
    }

    /// Recent UI thread faults, for deciding whether carrying on is still sensible.
    private readonly Queue<long> _uiFaults = new();

    private const int  UiFaultLimit    = 5;
    private const long UiFaultWindowMs = 10_000;

    /// <summary>
    /// Whether to swallow a UI thread exception and carry on.
    ///
    /// Surviving one fault is right; surviving an endless stream of them is not. A fault that
    /// repeats every time the dispatcher runs — a broken template, a binding that throws on
    /// every layout pass — would otherwise spin forever, writing the log entry each time,
    /// with a window on screen that cannot draw and no way to tell anything is wrong. Past a
    /// handful in ten seconds it is better to let Pulse close, having said why in the log.
    /// </summary>
    private bool ShouldKeepRunningAfter()
    {
        long now = Environment.TickCount64;

        _uiFaults.Enqueue(now);
        while (_uiFaults.Count > 0 && now - _uiFaults.Peek() > UiFaultWindowMs) _uiFaults.Dequeue();

        if (_uiFaults.Count <= UiFaultLimit) return true;

        Services.LogService.Warn("Crash",
            $"{_uiFaults.Count} interface failures within {UiFaultWindowMs / 1000} seconds; "
            + "Pulse is closing rather than carrying on in a broken state.");
        return false;
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
            try { HotkeyService.Instance.Dispose(); } catch { }
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
