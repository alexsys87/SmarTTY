
namespace SmarTTY.Forms;

public partial class MinimalisticModeNotificationForm : FormBase
{

    public bool DontShowAgain
    {
        get
        {
            return checkBox1.Checked;
        }
        set
        {
            checkBox1.Checked = value;
        }
    }

    public MinimalisticModeNotificationForm()
    {
        InitializeComponent();
    }
}
