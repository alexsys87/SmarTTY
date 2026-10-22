using System;

namespace SmarTTY.Forms;

public partial class TransferErrorForm : DpiAwareForm
{

    public TransferErrorForm(Exception exception, string text)
    {
        InitializeComponent();
        FormIcons.Apply(this, "transfer-error");
        this.txtErrors.Text = text;
        if (exception != null)
        {
            this.lblIntro.Text = "Directory transfer failed with the following error: " + exception.Message;
        }
    }
}
