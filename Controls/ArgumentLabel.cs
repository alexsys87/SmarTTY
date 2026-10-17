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
                string separator = _signature.Separator;
                for (int parameterCountIndex = 0; parameterCountIndex < _signature.ParameterCount; parameterCountIndex++)
                {
                    if (text != "")
                    {
                        text += separator;
                    }
                    text += _signature.GetParameter(parameterCountIndex).Text;
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
        int total = 0;
        int sum = 0;
        string text = _signature.Separator;
        Font font = new Font(Font, FontStyle.Bold);
        for (int parameterCountIndex = 0; parameterCountIndex < _signature.ParameterCount; parameterCountIndex++)
        {
            if (parameterCountIndex == _signature.ParameterCount - 1)
            {
                text = "";
            }
            Size size = Size.Ceiling(graphics.MeasureString(_signature.GetParameter(parameterCountIndex).Text + text, font));
            if (_signature.IsVertical)
            {
                total = Math.Max(total, size.Width);
                sum += size.Height;
            }
            else
            {
                total += size.Width;
                sum = Math.Max(sum, size.Height);
            }
        }
        return new SizeF(total, sum);
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
        int total = 0;
        int sum = (int)((float)Height - MeasureSignature().Height) / 2;
        string text = _signature.Separator;
        Font font = new Font(Font, FontStyle.Bold);
        for (int parameterCountIndex = 0; parameterCountIndex < _signature.ParameterCount; parameterCountIndex++)
        {
            if (parameterCountIndex == _signature.ParameterCount - 1)
            {
                text = "";
            }
            Font labelFont = ((parameterCountIndex == _activeIndex) ? font : Font);
            string value = _signature.GetParameter(parameterCountIndex).Text + text;
            Size size = Size.Ceiling(e.Graphics.MeasureString(value, labelFont));
            e.Graphics.DrawString(value, labelFont, Brushes.Black, new Rectangle(total, sum, size.Width, size.Height), stringFormat);
            if (_signature.IsVertical)
            {
                sum += size.Height;
            }
            else
            {
                total += size.Width;
            }
        }
    }

    public ArgumentLabel()
    {
        InitializeComponent();
    }
}
