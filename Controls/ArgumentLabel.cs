using SmarTTY.Completion;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class ArgumentLabel : UserControl
{
    private ISignatureInfo _signature;

    private int _activeIndex;

    public int ActiveIndex
    {
        get
        {
            return _activeIndex;
        }
        set
        {
            _activeIndex = value;
            Invalidate();
        }
    }

    public ISignatureInfo Signature
    {
        get
        {
            return _signature;
        }
        set
        {
            _signature = value;
            Invalidate();
        }
    }

    public override string Text
    {
        get
        {
            if (_signature != null)
            {
                string text = "";
                string text2 = _signature.Separator;
                for (int num = 0; num < _signature.ParameterCount; num++)
                {
                    if (text != "")
                    {
                        text += text2;
                    }
                    text += _signature.GetParameter(num).Text;
                }
                return text;
            }
            return base.Text;
        }
        set
        {
            base.Text = value;
            _signature = null;
            Invalidate();
        }
    }

    public SizeF MeasureSignature()
    {
        Graphics graphics = Graphics.FromHwnd(Handle);
        if (_signature == null)
        {
            return graphics.MeasureString(Text, Font);
        }
        int num = 0;
        int num2 = 0;
        string text = _signature.Separator;
        Font font = new Font(Font, FontStyle.Bold);
        for (int num3 = 0; num3 < _signature.ParameterCount; num3++)
        {
            if (num3 == _signature.ParameterCount - 1)
            {
                text = "";
            }
            Size size = Size.Ceiling(graphics.MeasureString(_signature.GetParameter(num3).Text + text, font));
            if (_signature.IsVertical)
            {
                num = Math.Max(num, size.Width);
                num2 += size.Height;
            }
            else
            {
                num += size.Width;
                num2 = Math.Max(num2, size.Height);
            }
        }
        return new SizeF(num, num2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        StringFormat stringFormat = new StringFormat(StringFormat.GenericDefault);
        base.OnPaint(e);
        if (_signature == null)
        {
            stringFormat.Alignment = StringAlignment.Near;
            stringFormat.LineAlignment = StringAlignment.Center;
            e.Graphics.DrawString(base.Text, Font, Brushes.Black, ClientRectangle, stringFormat);
            return;
        }
        int num = 0;
        int num2 = (int)((float)Height - MeasureSignature().Height) / 2;
        string text = _signature.Separator;
        Font font = new Font(Font, FontStyle.Bold);
        for (int num3 = 0; num3 < _signature.ParameterCount; num3++)
        {
            if (num3 == _signature.ParameterCount - 1)
            {
                text = "";
            }
            Font font2 = ((num3 == _activeIndex) ? font : Font);
            string text2 = _signature.GetParameter(num3).Text + text;
            Size size = Size.Ceiling(e.Graphics.MeasureString(text2, font2));
            e.Graphics.DrawString(text2, font2, Brushes.Black, new Rectangle(num, num2, size.Width, size.Height), stringFormat);
            if (_signature.IsVertical)
            {
                num2 += size.Height;
            }
            else
            {
                num += size.Width;
            }
        }
    }

    public ArgumentLabel()
    {
        InitializeComponent();
    }
}
