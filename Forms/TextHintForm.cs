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

    public void ShowBeside(Control control, int value2, bool flag)
    {
        Rectangle rectangle = control.RectangleToScreen(control.DisplayRectangle);
        if (flag)
        {
            int num = rectangle.Right + 5;
            if (num + Width > SystemInformation.VirtualScreen.Width)
            {
                num = rectangle.Left - 5 - Width;
            }
            int num2 = rectangle.Top + value2;
            if (num2 + Height > rectangle.Bottom)
            {
                num2 = rectangle.Bottom - Height;
            }
            if (num2 < rectangle.Top)
            {
                num2 = rectangle.Top;
            }
            Top = num2;
            Left = num;
        }
        else
        {
            int num3 = rectangle.Top - 5 - Height;
            if (num3 < 0)
            {
                num3 = rectangle.Bottom + 5;
            }
            Top = num3;
            Left = rectangle.Left + value2;
        }
        this.ShowPopup();
    }

    public void ShowAt(ICompletionHost completionHost, bool flag)
    {
        Reposition(completionHost, flag);
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

    public void Reposition(ICompletionHost completionHost, bool flag)
    {
        int num = ((!flag) ? (completionHost.CaretScreenBounds.Bottom + 5) : (completionHost.CaretScreenBounds.Top - 5 - Height));
        if (num < 0)
        {
            num = completionHost.CaretScreenBounds.Bottom + 5;
        }
        else if (num > SystemInformation.VirtualScreen.Height - Height)
        {
            num = completionHost.CaretScreenBounds.Top - 5 - Height;
        }
        Top = num;
        Left = completionHost.CaretScreenBounds.Left;
        Width = textHintControl1.Width;
        Height = textHintControl1.Height;
    }

    public void SetActiveParameter(int value2)
    {
        textHintControl1.SetActiveParameter(value2);
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
