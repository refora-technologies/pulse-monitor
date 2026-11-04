using System.Windows;
using System.Windows.Input;

namespace Pulse.Views;

/// <summary>
/// The note shown when the in-game overlay is turned on: what it does, that it stays out of
/// games with anti-cheat, and that it can be turned off. It is only turned on from here.
/// </summary>
public partial class InGameNoteWindow : Window
{
    /// True when the player chose to turn it on.
    public bool Accepted { get; private set; }

    public InGameNoteWindow()
    {
        InitializeComponent();
        DialogMotion.Attach(this);

        // No title bar, so dragging anywhere moves it, as the update dialog does.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };

        Loaded += (_, _) => BtnTurnOn.Focus();
    }

    private void BtnTurnOn_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
