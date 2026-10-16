using SmarTTY.Completion;
using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SmarTTY.Controls;

internal partial class TextHintControl : Control, IHintView
{
    private delegate void DisposeCallback();

    private int _descriptionHeight;

    private bool _showDescription;

    public bool ShowDescription
    {
        get
        {
            return _showDescription;
        }
        set
        {
            label2.Visible = value;
            _showDescription = value;
            if (!value)
            {
                label1.Height = Height - 2 * label1.Top;
            }
            else
            {
                label1.Height = Height - 2 * label1.Top - _descriptionHeight;
            }
        }
    }

    public string Description
    {
        get
        {
            return label2.Text;
        }
        set
        {
            label2.Text = value;
            label2.Visible = true;
        }
    }

    public string SignatureText
    {
        get
        {
            return label1.Text;
        }
        set
        {
            label1.Text = value;
            RecalculateSize();
        }
    }

    public TextHintControl()
    {
        InitializeComponent();
        _descriptionHeight = label2.Height;
    }

    private void RecalculateSize()
    {
        SizeF sizeF = label1.MeasureSignature();
        SizeF sizeF2 = sizeF;
        if (_showDescription)
        {
            SizeF sizeF3 = Graphics.FromHwnd(Handle).MeasureString(label2.Text, label2.Font);
            sizeF3.Width += 30f;
            if (sizeF2.Width < sizeF3.Width)
            {
                sizeF2.Width = sizeF3.Width;
            }
            sizeF2.Height += _descriptionHeight;
        }
        int num = 2;
        if (!IsWindows())
        {
            num = 10;
        }
        SetBounds(0, 0, (int)(sizeF2.Width + (float)(num * 2)), (int)(sizeF2.Height + (float)(num * 2)), BoundsSpecified.Size);
        label1.Left = num;
        label1.Top = num;
        label1.Width = (int)(sizeF2.Width + 1f);
        label1.Height = (int)(sizeF.Height + 1f);
        label2.Left = num;
        label2.Top = label1.Bottom;
        label2.Width = (int)sizeF2.Width;
        label2.Height = Height - label2.Top - num;
    }

    public void SetActiveParameter(int value2)
    {
        if (label1.ActiveIndex != value2)
        {
            label1.ActiveIndex = value2;
        }
    }

    public void SetSignature(ISignatureInfo signatureInfo)
    {
        label1.Signature = signatureInfo;
        RecalculateSize();
    }

    public void Dismiss()
    {
        Hide();
        Parent.BeginInvoke(new DisposeCallback(Dispose));
        Parent = null;
    }

    private bool IsWindows()
    {
        PlatformID platform = Environment.OSVersion.Platform;
        if (platform == PlatformID.Unix || platform == PlatformID.MacOSX)
        {
            return false;
        }
        return true;
    }

    public void Reposition(ICompletionHost completionHost, bool flag)
    {
        Point point = Parent.PointToScreen(new Point(0, 0));
        Rectangle rectangle = completionHost.CaretScreenBounds;
        rectangle.Offset(-point.X, -point.Y);
        int num = ((!flag) ? (rectangle.Bottom + 5) : (rectangle.Top - 5 - Height));
        if (num < 0)
        {
            num = rectangle.Bottom + 5;
        }
        else if (num > SystemInformation.VirtualScreen.Height - Height)
        {
            num = rectangle.Top - 5 - Height;
        }
        Top = num;
        Left = rectangle.Left;
    }

    public void ShowAt(ICompletionHost completionHost, bool flag)
    {
        Reposition(completionHost, flag);
        Show();
        this.label2.Visible = this._showDescription;
    }

    [SpecialName]
    private bool GetVisible()
    {
        return Visible;
    }

    bool IHintView.Visible => this.GetVisible();
}
