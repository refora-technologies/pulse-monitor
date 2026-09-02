using System.Windows.Input;
using Pulse.Models;
using Pulse.Services;

using Shortcut = Pulse.Models.Shortcut;

namespace Pulse.ViewModels;

/// <summary>
/// One row of the shortcuts card: what it does, what it is bound to, and what went wrong.
/// </summary>
public class ShortcutRowViewModel : BaseViewModel
{
    public ShortcutAction Action { get; }
    public string Label => Shortcut.Describe(Action);

    public ShortcutRowViewModel(ShortcutAction action)
    {
        Action = action;
        _shortcut = SettingsService.Instance.Settings.ShortcutFor(action);
    }

    private Shortcut _shortcut;
    public Shortcut Binding
    {
        get => _shortcut;
        set
        {
            _shortcut = value;
            OnPropertyChanged(nameof(Binding));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(IsSet));
        }
    }

    /// <summary>
    /// What the chip reads.
    /// </summary>
    /// "Not set" rather than an empty chip: a blank control looks like something failed to
    /// load, and this is a deliberate state the user can choose.
    public string DisplayText => IsCapturing
        ? "Press a combination…  Esc to cancel"
        : _shortcut.IsSet ? _shortcut.ToString() : "Not set";

    public bool IsSet => _shortcut.IsSet;

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (!Set(ref _isCapturing, value)) return;
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    /// <summary>
    /// Why this shortcut is not working, shown under the row. Empty when it is fine.
    /// </summary>
    /// <remarks>
    /// The single most important part of the feature. A shortcut that silently does nothing is
    /// indistinguishable from a broken application, and the usual cause — another program got
    /// the combination first — is invisible unless we say so.
    /// </remarks>
    private string _problem = "";
    public string Problem
    {
        get => _problem;
        set
        {
            if (Set(ref _problem, value)) OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem.Length > 0;
}
