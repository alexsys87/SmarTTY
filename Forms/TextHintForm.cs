using SmarTTY.Completion;
using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class TextHintForm : Form, IHintView
{

    protected override bool ShowWithoutActivation => true;

    public string SignatureText
    {
        get
        {
            return textHintControl1.SignatureText;
        }
        set
        {
            textHintControl1.SignatureText = value;
        }
    }

    public bool ShowDescription
    {
        get
        {
            return textHintControl1.ShowDescription;
        }
        set
        {
            textHintControl1.ShowDescription = value;
        }
    }

    public string Description
    {
        get
        {
            return textHintControl1.Description;
        }
        set
        {
            textHintControl1.Description = value;
        }
    }

    public TextHintForm()
    {
        InitializeComponent();
        textHintControl1.Visible = true;
    }

    public void ShowBeside(Control control, int yOffset, bool isVertical)
    {
        Rectangle bounds = control.RectangleToScreen(control.DisplayRectangle);
        if (isVertical)
        {
            int xPos = bounds.Right + 5;
            if (xPos + Width > SystemInformation.VirtualScreen.Width)
            {
                xPos = bounds.Left - 5 - Width;
            }
            int yPos = bounds.Top + yOffset;
            if (yPos + Height > bounds.Bottom)
            {
                yPos = bounds.Bottom - Height;
            }
            if (yPos < bounds.Top)
            {
                yPos = bounds.Top;
            }
            Top = yPos;
            Left = xPos;
        }
        else
        {
            int topPos = bounds.Top - 5 - Height;
            if (topPos < 0)
            {
                topPos = bounds.Bottom + 5;
            }
            Top = topPos;
            Left = bounds.Left + yOffset;
        }
        this.ShowPopup();
    }

    public void ShowAt(ICompletionHost completionHost, bool isVertical)
    {
        Reposition(completionHost, isVertical);
        this.ShowPopup();
    }

    public void Dismiss()
    {
        Close();
    }

    public void ShowPopup()
    {
        SetBounds(0, 0, textHintControl1.Width, textHintControl1.Height, BoundsSpecified.Size);
        Show();
        SetBounds(0, 0, textHintControl1.Width, textHintControl1.Height, BoundsSpecified.Size);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
    }

    public void Reposition(ICompletionHost completionHost, bool isVertical)
    {
        int yPos = ((!isVertical) ? (completionHost.CaretScreenBounds.Bottom + 5) : (completionHost.CaretScreenBounds.Top - 5 - Height));
        if (yPos < 0)
        {
            yPos = completionHost.CaretScreenBounds.Bottom + 5;
        }
        else if (yPos > SystemInformation.VirtualScreen.Height - Height)
        {
            yPos = completionHost.CaretScreenBounds.Top - 5 - Height;
        }
        Top = yPos;
        Left = completionHost.CaretScreenBounds.Left;
        Width = textHintControl1.Width;
        Height = textHintControl1.Height;
    }

    public void SetActiveParameter(int parameterIndex)
    {
        textHintControl1.SetActiveParameter(parameterIndex);
    }

    public void SetSignature(ISignatureInfo signatureInfo)
    {
        textHintControl1.SetSignature(signatureInfo);
    }

    private void textHintControl1_Resize(object sender, EventArgs e)
    {
        SetBounds(0, 0, textHintControl1.Width, textHintControl1.Height, BoundsSpecified.Size);
    }

    [SpecialName]
    private bool GetVisible()
    {
        return Visible;
    }

    bool IHintView.Visible => this.GetVisible();
}
