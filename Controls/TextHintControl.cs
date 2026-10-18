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
        SizeF signatureSize = label1.MeasureSignature();
        SizeF contentSize = signatureSize;
        if (_showDescription)
        {
            SizeF descriptionSize = Graphics.FromHwnd(Handle).MeasureString(label2.Text, label2.Font);
            descriptionSize.Width += 30f;
            if (contentSize.Width < descriptionSize.Width)
            {
                contentSize.Width = descriptionSize.Width;
            }
            contentSize.Height += _descriptionHeight;
        }
        int value = 2;
        if (!IsWindows())
        {
            value = 10;
        }
        SetBounds(0, 0, (int)(contentSize.Width + (float)(value * 2)), (int)(contentSize.Height + (float)(value * 2)), BoundsSpecified.Size);
        label1.Left = value;
        label1.Top = value;
        label1.Width = (int)(contentSize.Width + 1f);
        label1.Height = (int)(signatureSize.Height + 1f);
        label2.Left = value;
        label2.Top = label1.Bottom;
        label2.Width = (int)contentSize.Width;
        label2.Height = Height - label2.Top - value;
    }

    public void SetActiveParameter(int value)
    {
        if (label1.ActiveIndex != value)
        {
            label1.ActiveIndex = value;
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

    public void Reposition(ICompletionHost completionHost, bool above)
    {
        Point point = Parent.PointToScreen(new Point(0, 0));
        Rectangle rectangle = completionHost.CaretScreenBounds;
        rectangle.Offset(-point.X, -point.Y);
        int value = ((!above) ? (rectangle.Bottom + 5) : (rectangle.Top - 5 - Height));
        if (value < 0)
        {
            value = rectangle.Bottom + 5;
        }
        else if (value > SystemInformation.VirtualScreen.Height - Height)
        {
            value = rectangle.Top - 5 - Height;
        }
        Top = value;
        Left = rectangle.Left;
    }

    public void ShowAt(ICompletionHost completionHost, bool above)
    {
        Reposition(completionHost, above);
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
