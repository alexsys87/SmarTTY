using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class HostKeyMismatchForm 
{
	private IContainer components = null;

	private Label lblHostNameDesc;

	private Label lblHostName;

	private PictureBox picAbort;

	private Label lblMainDesc;

	private Button btnContinue;

	private Button btnAbort;

	private Label lblDetailedDesc;

	private Label lblTypeChange;

	protected override void Dispose(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		ComponentResourceManager resources = new ComponentResourceManager(typeof(HostKeyMismatchForm));
		lblHostNameDesc = new Label();
		lblHostName = new Label();
		picAbort = new PictureBox();
		lblMainDesc = new Label();
		btnContinue = new Button();
		btnAbort = new Button();
		lblDetailedDesc = new Label();
		lblTypeChange = new Label();
		((ISupportInitialize)picAbort).BeginInit();
		SuspendLayout();
		lblHostNameDesc.AutoSize = true;
		lblHostNameDesc.Location = new Point(98, 79);
		lblHostNameDesc.Name = "lblHostNameDesc";
		lblHostNameDesc.Size = new Size(61, 13);
		lblHostNameDesc.TabIndex = 0;
		lblHostNameDesc.Text = "Host name:";
		lblHostName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblHostName.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		lblHostName.Location = new Point(165, 79);
		lblHostName.Name = "lblHostName";
		lblHostName.Size = new Size(407, 13);
		lblHostName.TabIndex = 0;
		lblHostName.Text = "localhost";
		picAbort.Image = (Image)resources.GetObject("picAbort.Image");
		picAbort.Location = new Point(12, 37);
		picAbort.Name = "picAbort";
		picAbort.Size = new Size(80, 80);
		picAbort.SizeMode = PictureBoxSizeMode.StretchImage;
		picAbort.TabIndex = 5;
		picAbort.TabStop = false;
		lblMainDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblMainDesc.Font = new Font("Microsoft Sans Serif", 14.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		lblMainDesc.Location = new Point(12, 9);
		lblMainDesc.Name = "lblMainDesc";
		lblMainDesc.Size = new Size(560, 25);
		lblMainDesc.TabIndex = 0;
		lblMainDesc.Text = "Host key mismatch";
		lblMainDesc.TextAlign = ContentAlignment.MiddleCenter;
		btnContinue.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnContinue.DialogResult = DialogResult.OK;
		btnContinue.Location = new Point(298, 121);
		btnContinue.Name = "btnContinue";
		btnContinue.Size = new Size(274, 38);
		btnContinue.TabIndex = 2;
		btnContinue.Text = "Remember the new key and &continue connecting.";
		btnContinue.UseVisualStyleBackColor = true;
		btnContinue.Click += btnContinue_Click;
		btnAbort.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		btnAbort.DialogResult = DialogResult.Cancel;
		btnAbort.Image = (Image)resources.GetObject("btnAbort.Image");
		btnAbort.ImageAlign = ContentAlignment.MiddleRight;
		btnAbort.Location = new Point(12, 121);
		btnAbort.Name = "btnAbort";
		btnAbort.Size = new Size(278, 38);
		btnAbort.TabIndex = 1;
		btnAbort.Text = "&Abort connection";
		btnAbort.TextImageRelation = TextImageRelation.ImageBeforeText;
		btnAbort.UseVisualStyleBackColor = true;
		lblDetailedDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblDetailedDesc.Location = new Point(98, 42);
		lblDetailedDesc.Name = "lblDetailedDesc";
		lblDetailedDesc.Size = new Size(474, 37);
		lblDetailedDesc.TabIndex = 6;
		lblDetailedDesc.Text = "SmarTTY has detected that the public key of the server has changed. This can be an indication that someone is eavesdropping on you right now. Please choose whether to continue connecting.";
		lblTypeChange.AutoSize = true;
		lblTypeChange.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		lblTypeChange.Location = new Point(98, 96);
		lblTypeChange.Name = "lblTypeChange";
		lblTypeChange.Size = new Size(0, 13);
		lblTypeChange.TabIndex = 0;
		CancelButton = btnAbort;
		ClientSize = new Size(584, 171);
		Controls.Add(lblDetailedDesc);
		Controls.Add(btnAbort);
		Controls.Add(btnContinue);
		Controls.Add(picAbort);
		Controls.Add(lblHostName);
		Controls.Add(lblMainDesc);
		Controls.Add(lblTypeChange);
		Controls.Add(lblHostNameDesc);
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		Name = "HostKeyMismatchForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Host Key Error";
		((ISupportInitialize)picAbort).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
