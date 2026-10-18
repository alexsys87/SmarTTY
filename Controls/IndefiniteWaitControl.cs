using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class IndefiniteWaitControl : DpiAwareControl
{

    public string DescriptionText
    {
        get
        {
            return lblDescription.Text;
        }
        set
        {
            if (value != null)
            {
                lblDescription.Text = value;
            }
        }
    }

    public bool CancelVisible
    {
        get
        {
            return btnCancel.Visible;
        }
        set
        {
            btnCancel.Visible = value;
        }
    }

    public event EventHandler CancelRequested;

    private void RaiseCancelRequested()
    {
        if (CancelRequested != null)
        {
            CancelRequested(this, null);
        }
    }

    private void btnCancel_Click(object sender, EventArgs e)
    {
        RaiseCancelRequested();
    }

    public IndefiniteWaitControl()
    {
        InitializeComponent();
    }

    private void IndefiniteWaitControl_Paint(object sender, PaintEventArgs e)
    {
        Rectangle clientRectangle = ClientRectangle;
        clientRectangle.Width--;
        clientRectangle.Height--;
        Pen pen = new Pen(SystemColors.ActiveBorder);
        e.Graphics.DrawRectangle(pen, clientRectangle);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}
