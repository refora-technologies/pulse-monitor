using System.Windows;
using Pulse.Services;
using Pulse.ViewModels;
using Pulse.Views;

using WinApplication = System.Windows.Application;

namespace Pulse;

public partial class App : WinApplication
{
    private static Mutex? _mutex;

    private MainWindow?    _mainWindow;
    private OverlayWindow? _overlayWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _overlayToggleItem;

    /// <summary>Whether the overlay is currently visible.</summary>
    public bool IsOverlayVisible => _overlayWindow != null && _overlayWindow.IsLoaded && _overlayWindow.IsVisible;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Global\\PulseMonitor_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Initialise singletons (starts hardware polling)
        _ = HardwareService.Instance;
        _ = OverlayViewModel.Instance;
        _ = SettingsViewModel.Instance;

        SetupTrayIcon();

        ShowOverlay();

        if (!e.Args.Contains("--startup"))
            ShowControlPanel();

        CheckForUpdatesOnStartup();
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

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Control Panel", null, (_, _) => ShowControlPanel());
        menu.Items.Add(_overlayToggleItem);
        menu.Items.Add("-");
        menu.Items.Add("Exit Pulse", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick     += (_, _) => ShowControlPanel();
    }

    private void ShowControlPanel()
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

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        HardwareService.Instance.Dispose();
        Dispatcher.Invoke(() => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        try { HardwareService.Instance.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
