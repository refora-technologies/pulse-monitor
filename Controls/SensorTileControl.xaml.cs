// Explicit alias — avoids WPF vs WinForms UserControl collision
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Pulse.Controls;

public partial class SensorTileControl : WpfUserControl
{
    public SensorTileControl()
    {
        InitializeComponent();
    }
}
