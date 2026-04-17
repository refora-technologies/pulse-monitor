using System.Windows;
using Pulse.Services;
using Pulse.ViewModels;
using Pulse.Views;

using WinApplication = System.Windows.Application;

namespace Pulse;

public partial class App : WinApplication
{
    private MainWindow?    _mainWindow;
    private OverlayWindow? _overlayWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    /// <summary>Whether the overlay is currently visible.</summary>
    public bool IsOverlayVisible => _overlayWindow != null && _overlayWindow.IsLoaded && _overlayWindow.IsVisible;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialise singletons (starts hardware polling)
        _ = HardwareService.Instance;
        _ = OverlayViewModel.Instance;
        _ = SettingsViewModel.Instance;

        SetupTrayIcon();

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        ShowOverlay();
    }

    /// <summary>Show the single overlay instance.</summary>
    public void ShowOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded)
                _overlayWindow = new OverlayWindow();
            _overlayWindow.Show();
            _overlayWindow.Topmost = true;
            UpdateMainWindowButton();
        });
    }

    /// <summary>Hide the overlay.</summary>
    public void HideOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            _overlayWindow?.Hide();
            UpdateMainWindowButton();
        });
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
            var iconPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "Icons", "pulse.ico");
            _trayIcon.Icon = System.IO.File.Exists(iconPath)
                ? new System.Drawing.Icon(iconPath)
                : System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Control Panel", null, (_, _) => ShowControlPanel());
        menu.Items.Add("Show Overlay",        null, (_, _) => ShowOverlay());
        menu.Items.Add("Hide Overlay",        null, (_, _) => HideOverlay());
        menu.Items.Add("-");
        menu.Items.Add("Exit Pulse",          null, (_, _) => ExitApp());

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
        base.OnExit(e);
    }
}
