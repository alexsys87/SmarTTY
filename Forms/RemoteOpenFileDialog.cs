using SmarTTY.IO;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

[ToolboxBitmap(typeof(OpenFileDialog))]
public partial class RemoteOpenFileDialog : Component
{

    [Category("Behaviour")]
    public string InitialDirectory { get; set; }

    [Category("Behaviour")]
    public string HostName { get; set; }

    [Category("Behaviour")]
    public string FileName { get; set; }

    public event RemoteCommandStarter CommandRequested;

    public RemoteOpenFileDialog()
        : this(null)
    {
    }

    public RemoteOpenFileDialog(IContainer container)
    {
        container?.Add(this);
        InitializeComponent();
    }

    public string ShowDialog()
    {
        string text = RemoteOpenFileForm.Browse(CommandRequested, FileName, InitialDirectory, HostName);
        if (text != null)
        {
            FileName = text;
        }
        return text;
    }
}
