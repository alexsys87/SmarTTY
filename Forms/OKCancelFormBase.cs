using System;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class OKCancelFormBase : FormBase
{

    public string OkButtonText
    {
        get
        {
            return btnOK.Text;
        }
        set
        {
            btnOK.Text = value;
        }
    }

    protected bool OkButtonEnabled
    {
        get
        {
            return btnOK.Enabled;
        }
        set
        {
            btnOK.Enabled = value;
        }
    }

    public OKCancelFormBase()
    {
        InitializeComponent();
    }

    protected Panel GetButtonPanel()
    {
        return panel1;
    }

    protected virtual void btnOK_Click(object sender, EventArgs e)
    {
    }
}
