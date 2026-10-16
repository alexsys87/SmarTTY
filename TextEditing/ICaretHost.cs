using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.TextEditing;

public interface ICaretHost
{
    Rectangle CaretBounds { get; set; }

    Control HostControl { get; }
}
