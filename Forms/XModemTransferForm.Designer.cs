using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class XModemTransferForm : DpiAwareForm
{
    private IContainer components = null;
    private ProgressBar progressBar1;
    private Label lblStatus;
    private Label lblInfo;
    private Button btnCancel;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        progressBar1 = new ProgressBar();
        lblStatus = new Label();
        lblInfo = new Label();
        btnCancel = new Button();
        SuspendLayout();

        // progressBar1
        progressBar1.Location = new Point(12, 40);
        progressBar1.Name = "progressBar1";
        progressBar1.Size = new Size(360, 23);
        progressBar1.TabIndex = 0;

        // lblStatus
        lblStatus.AutoSize = true;
        lblStatus.Location = new Point(12, 15);
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(110, 13);
        lblStatus.TabIndex = 1;
        lblStatus.Text = "Starting XMODEM transfer...";

        // lblInfo
        lblInfo.AutoSize = true;
        lblInfo.Location = new Point(12, 72);
        lblInfo.Name = "lblInfo";
        lblInfo.Size = new Size(130, 13);
        lblInfo.TabIndex = 2;
        lblInfo.Text = "Transferred: 0 B / Errors: 0";

        // btnCancel
        btnCancel.Location = new Point(297, 67);
        btnCancel.Name = "btnCancel";
        btnCancel.Size = new Size(75, 23);
        btnCancel.TabIndex = 3;
        btnCancel.Text = "Cancel";
        btnCancel.UseVisualStyleBackColor = true;
        btnCancel.Click += btnCancel_Click;

        // XModemTransferForm
        ClientSize = new Size(384, 101);
        Controls.Add(btnCancel);
        Controls.Add(lblInfo);
        Controls.Add(lblStatus);
        Controls.Add(progressBar1);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "XModemTransferForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "XMODEM Transfer";
        ResumeLayout(false);
        PerformLayout();
    }
}
