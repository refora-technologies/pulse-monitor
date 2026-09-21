using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>
/// Registers Pulse's shortcuts with Windows, so they work while another application has focus.
///
/// This is what makes them useful and what makes them worth being careful with. A global hotkey
/// is delivered to us and never reaches the application the user is actually in, so a
/// combination we take is a combination that stops working everywhere until Pulse closes. The
/// rules about which ones are acceptable live in <see cref="Shortcut"/>; this class only deals
/// with Windows.
///
/// Running elevated helps here, and is why the shortcuts keep working over an elevated window:
/// a hotkey registered by a normal process is not delivered while something running as
/// administrator is in front.
/// </summary>
public class HotkeyService : IDisposable
{
    private static readonly Lazy<HotkeyService> LazyInstance =
        new(() => new HotkeyService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static HotkeyService Instance => LazyInstance.Value;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WmHotkey = 0x0312;

    private const uint ModAlt      = 0x0001;
    private const uint ModControl  = 0x0002;
    private const uint ModShift    = 0x0004;
    private const uint ModWin      = 0x0008;

    /// Without this, holding the keys down repeats the message at the keyboard's repeat rate,
    /// which for a show/hide binding means the overlay strobes until the key is released.
    private const uint ModNoRepeat = 0x4000;

    /// A window that exists only to receive messages. Message-only, so it is never shown, never
    /// appears in the taskbar and costs nothing to keep.
    ///
    /// A window rather than a thread registration on purpose. Registering against the thread
    /// delivers WM_HOTKEY as a thread message with no window, which only reaches WPF through a
    /// dispatcher filter — workable, but it depends on how the message loop is being pumped at
    /// the time. A window handle is delivered the same way regardless.
    private HwndSource? _sink;

    /// The handle of the message window, exposed for tests rather than for callers.
    internal IntPtr Handle => _sink?.Handle ?? IntPtr.Zero;

    /// Which action each registered id belongs to. Ids are ours to choose and only have to be
    /// unique within this window.
    private readonly Dictionary<int, ShortcutAction> _registered = new();

    /// Why a shortcut is not working, per action. Empty when all is well.
    public IReadOnlyDictionary<ShortcutAction, string> Failures => _failures;
    private readonly Dictionary<ShortcutAction, string> _failures = new();

    /// Raised on the interface thread when the user presses one of the combinations.
    public event EventHandler<ShortcutAction>? Pressed;

    /// Raised when a registration succeeds or fails, so the panel can show the reason.
    public event EventHandler? FailuresChanged;

    private bool _suspended;

    private HotkeyService() { }

    /// <summary>
    /// Makes the registered shortcuts match the settings.
    /// </summary>
    /// <remarks>
    /// Everything is unregistered and re-registered rather than worked out incrementally.
    /// There are three of them, this runs when a setting changes rather than continuously, and
    /// the alternative is a diff whose bugs would appear as a shortcut that quietly stops
    /// working — which is the exact failure this feature cannot afford.
    /// </remarks>
    public void Apply()
    {
        var settings = SettingsService.Instance.Settings;

        UnregisterAll();
        _failures.Clear();

        if (!settings.ShortcutsEnabled || _suspended)
        {
            StopSinkRetry();
            FailuresChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        EnsureSink();
        if (_sink == null)
        {
            foreach (var action in AllActions)
                if (settings.ShortcutFor(action).IsSet)
                    _failures[action] = "Shortcuts could not be set up on this system.";

            // Nearly always temporary, and it used to be permanent for the session. The
            // listener is a WPF window, so it needs the same native library the overlay does,
            // and at logon that library can be briefly unreadable — the same few seconds that
            // left a reporter with no overlay also left every shortcut dead until the next
            // restart. Same cause, same answer: ask again shortly.
            ScheduleSinkRetry();
            FailuresChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        StopSinkRetry();

        foreach (var action in AllActions)
        {
            var shortcut = settings.ShortcutFor(action);
            if (!shortcut.IsSet) continue;

            int id = (int)action + 1;

            if (RegisterHotKey(_sink.Handle, id, ToWin32(shortcut.Modifiers) | ModNoRepeat,
                               (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key)))
            {
                _registered[id] = action;
                continue;
            }

            // Almost always ERROR_HOTKEY_ALREADY_REGISTERED (1409): another application got
            // there first. Reported against the row rather than logged and forgotten, because
            // a shortcut that silently does nothing is the reason people distrust this feature.
            int error = Marshal.GetLastWin32Error();
            _failures[action] = error == 1409
                ? $"{shortcut} is already used by another program."
                : $"Windows would not accept {shortcut} (error {error}).";

            LogService.Warn(nameof(HotkeyService),
                $"Could not register {shortcut} for {action}: Win32 error {error}.");
        }

        // What the bindings actually were at the moment Windows was asked. Written on every
        // apply, which includes startup, so a diagnostic shows whether a combination survived
        // the last restart or came back as something else.
        LogService.Info(nameof(HotkeyService), "Shortcuts now: " + string.Join(", ",
            AllActions.Select(action =>
            {
                var shortcut = settings.ShortcutFor(action);
                var state = !shortcut.IsSet          ? "not set"
                          : _failures.ContainsKey(action) ? $"{shortcut} REFUSED"
                          : shortcut.ToString();
                return $"{action}={state}";
            })));

        FailuresChanged?.Invoke(this, EventArgs.Empty);
    }

    public static readonly ShortcutAction[] AllActions =
    {
        ShortcutAction.ToggleOverlay,
        ShortcutAction.ToggleControlPanel,
        ShortcutAction.ToggleCompactMode,
    };

    /// <summary>
    /// Releases the shortcuts while the user is recording a new one, and puts them back after.
    /// </summary>
    /// <remarks>
    /// Without this, pressing the combination a row already holds would fire the action instead
    /// of being recorded: asking to change the overlay shortcut would hide the overlay. Windows
    /// delivers the hotkey to us before the focused window ever sees the key, so the capture
    /// control cannot simply ignore it.
    /// </remarks>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        _suspended = false;
        Apply();
    }

    /// How many times the listener has failed to be created in the current run of attempts.
    private int _sinkRetries;

    private System.Windows.Threading.DispatcherTimer? _sinkRetryTimer;

    /// Matches the overlay's retry budget in <see cref="App"/>: a minute of asking, then stop.
    private const int MaxSinkRetries = 20;

    private void ScheduleSinkRetry()
    {
        if (_sinkRetries >= MaxSinkRetries)
        {
            StopSinkRetry();
            LogService.Warn(nameof(HotkeyService),
                $"The shortcut listener could not be created after {MaxSinkRetries} attempts.");
            return;
        }

        _sinkRetries++;

        _sinkRetryTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };

        _sinkRetryTimer.Tick -= OnSinkRetryTick;
        _sinkRetryTimer.Tick += OnSinkRetryTick;
        _sinkRetryTimer.Start();
    }

    private void OnSinkRetryTick(object? sender, EventArgs e) => Apply();

    private void StopSinkRetry()
    {
        _sinkRetries = 0;
        _sinkRetryTimer?.Stop();
    }

    private void EnsureSink()
    {
        if (_sink != null) return;

        try
        {
            // HWND_MESSAGE. A message-only window is invisible, is not enumerated, and does not
            // need Pulse to have any window open — the shortcuts have to work with the overlay
            // hidden and the panel closed, which is most of the time.
            var parameters = new HwndSourceParameters("PulseHotkeySink")
            {
                ParentWindow = new IntPtr(-3),
                Width = 0, Height = 0,
            };

            _sink = new HwndSource(parameters);
            _sink.AddHook(OnMessage);
        }
        catch (Exception ex)
        {
            // Only the first failure of a run. This is retried every few seconds, and twenty
            // copies of the same line would bury the one that explains what happened.
            if (_sinkRetries == 0)
                LogService.Error(nameof(HotkeyService), "Could not create the shortcut listener", ex);

            _sink = null;
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        if (!_registered.TryGetValue(wParam.ToInt32(), out var action)) return IntPtr.Zero;

        handled = true;

        // Raised rather than acted on here: this class knows about Windows, and nothing about
        // overlays or panels. It also means the whole feature can be tested without one.
        try { Pressed?.Invoke(this, action); }
        catch (Exception ex)
        {
            // A failing action must not take down the message pump, or every later press of
            // every shortcut would be lost too.
            LogService.Error(nameof(HotkeyService), $"The {action} shortcut failed", ex);
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        if (_sink == null) { _registered.Clear(); return; }

        foreach (var id in _registered.Keys)
            UnregisterHotKey(_sink.Handle, id);   // already gone is the outcome being asked for

        _registered.Clear();
    }

    private static uint ToWin32(ModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt))     value |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift))   value |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) value |= ModWin;
        return value;
    }

    public void Dispose()
    {
        StopSinkRetry();
        UnregisterAll();

        // Windows releases these when the process ends, but not before: an orderly exit that
        // leaves them registered would hold the combinations for as long as the handle lives.
        try { _sink?.Dispose(); } catch { }   // shutting down; nothing follows to inform
        _sink = null;
    }
}
