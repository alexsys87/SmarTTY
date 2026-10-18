using SmarTTY.IO;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

[ToolboxBitmap(typeof(OpenFileDialog))]
public partial class RemoteFolderDialog : Component
{
    private bool _usePrefixedListing;

    protected string _unusedText;

    [DefaultValue(false)]
    public bool UsePrefixedListing
    {
        get
        {
            return _usePrefixedListing;
        }
        set
        {
            _usePrefixedListing = value;
        }
    }

    [DefaultValue(null)]
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string UnusedText
    {
        get
        {
            return _unusedText;
        }
        set
        {
            _unusedText = value;
        }
    }

    [Category("Behaviour")]
    public string InitialFolder { get; set; }

    [Category("Behaviour")]
    public string HostName { get; set; }

    public event RemoteCommandStarter CommandRequested;

    public RemoteFolderDialog()
        : this(null)
    {
    }

    public RemoteFolderDialog(IContainer container)
    {
        container?.Add(this);
        InitializeComponent();
    }

    public string ShowDialog()
    {
        string text = RemoteFolderBrowserForm.Browse(CommandRequested, _usePrefixedListing, InitialFolder, HostName);
        if (text != null)
        {
            InitialFolder = text;
        }
        return text;
    }
}
