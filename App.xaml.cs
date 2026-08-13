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
