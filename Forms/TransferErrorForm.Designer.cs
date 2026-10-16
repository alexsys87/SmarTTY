using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class TransferErrorForm 
{	private IContainer components = null;

	private Label lblIntro;

	private Button btnClose;

	private TextBox txtErrors;

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
		lblIntro = new Label();
		btnClose = new Button();
		txtErrors = new TextBox();
		SuspendLayout();
		lblIntro.AutoSize = true;
		lblIntro.Location = new Point(12, 9);
		lblIntro.Name = "lblIntro";
		lblIntro.Size = new Size(367, 13);
		lblIntro.TabIndex = 0;
		lblIntro.Text = "The following warnings were encountered during directory transfer operation:";
		btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnClose.DialogResult = DialogResult.Cancel;
		btnClose.Location = new Point(597, 347);
		btnClose.Name = "btnClose";
		btnClose.Size = new Size(75, 23);
		btnClose.TabIndex = 2;
		btnClose.Text = "Close";
		btnClose.UseVisualStyleBackColor = true;
		txtErrors.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		txtErrors.Location = new Point(12, 25);
		txtErrors.Multiline = true;
		txtErrors.Name = "txtErrors";
		txtErrors.ReadOnly = true;
		txtErrors.ScrollBars = ScrollBars.Both;
		txtErrors.Size = new Size(660, 316);
		txtErrors.TabIndex = 1;
		AutoScaleDimensions = new SizeF(6f, 13f);
		ClientSize = new Size(684, 382);
		Controls.Add(txtErrors);
		Controls.Add(btnClose);
		Controls.Add(lblIntro);
		MinimizeBox = false;
		MinimumSize = new Size(400, 130);
		Name = "TransferErrorForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Errors During Directory Transfer";
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
