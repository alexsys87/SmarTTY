using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class DoubleBufferedListView : ListView
{

    public DoubleBufferedListView()
    {
        InitializeComponent();
        SetStyle(ControlStyles.OptimizedDoubleBuffer, value: true);
    }
}
