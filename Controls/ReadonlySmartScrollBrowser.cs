using CodeEditPP.Properties;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class ReadonlySmartScrollBrowser : ReadonlyTextBrowser
{
    private enum AutoScrollZone
    {
        None,
        Up,
        Down
    }

    private AutoScrollZone _activeZone;

    private AutoScrollZone _hotZone;

    private int _scrollSpeed;

    private Rectangle _zoneRect;

    private Rectangle _sliderRect;

    private bool _zoneDismissed;

    private Rectangle _fastScrollButton;

    private bool _smartScrollEnabled;

    private MouseEventArgs _lastMouse;

    private int _autoScrollDelta;

    private int _scrollRemainder;

    public bool SmartAutoScroll
    {
        get
        {
            return _smartScrollEnabled;
        }
        set
        {
            _smartScrollEnabled = value;
        }
    }

    public ReadonlySmartScrollBrowser()
    {
        InitializeComponent();
    }

    private void SetHotZone(AutoScrollZone autoScrollZone, bool flag)
    {
        if (flag)
        {
            _activeZone = AutoScrollZone.None;
        }
        if (autoScrollZone != _hotZone)
        {
            _hotZone = autoScrollZone;
            if (_hotZone == AutoScrollZone.Up)
            {
                _zoneRect = new Rectangle(TextArea.Left, TextArea.Top, TextArea.Width, 32);
            }
            else
            {
                _zoneRect = new Rectangle(TextArea.Left, TextArea.Bottom - 32, TextArea.Width, 32);
            }
            _sliderRect = new Rectangle(_zoneRect.Left + 10 + _zoneRect.Height, _zoneRect.Top + 5, _zoneRect.Width - 20 - _zoneRect.Height * 2, _zoneRect.Height - 10);
            this._timer.Enabled = _hotZone != AutoScrollZone.None;
            if (_hotZone == AutoScrollZone.None)
            {
                _zoneDismissed = false;
            }
            Redraw();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e2)
    {
        if (_smartScrollEnabled)
        {
            if (_activeZone != AutoScrollZone.None && e2.Button == MouseButtons.Left && _fastScrollButton.Contains(e2.Location))
            {
                SetHotZone(_activeZone, flag: false);
                _activeZone = AutoScrollZone.None;
                _zoneDismissed = true;
                Redraw();
            }
            if (_hasSelection && e2.Button == MouseButtons.Left)
            {
                _lastMouse = e2;
                AutoScrollZone autoScrollZone = AutoScrollZone.None;
                if (TextArea.Bottom - e2.Y < 32)
                {
                    autoScrollZone = AutoScrollZone.Down;
                }
                else if (e2.Y - TextArea.Top < 32)
                {
                    autoScrollZone = AutoScrollZone.Up;
                }
                if (!_zoneDismissed)
                {
                    SetHotZone(autoScrollZone, flag: false);
                }
                else if (autoScrollZone != AutoScrollZone.None)
                {
                    SetHotZone(autoScrollZone, flag: false);
                }
                if (autoScrollZone != AutoScrollZone.None && _activeZone != autoScrollZone && !_zoneDismissed)
                {
                    int num = 32;
                    int num2 = ((e2.X <= TextArea.Right - (num + 5)) ? (e2.X + 5) : (e2.X - num - 5));
                    if (autoScrollZone == AutoScrollZone.Down)
                    {
                        _fastScrollButton = new Rectangle(num2, TextArea.Bottom - 32 - num - 5, num, num);
                    }
                    else
                    {
                        _fastScrollButton = new Rectangle(num2, TextArea.Top + 32 + 5, num, num);
                    }
                    _activeZone = autoScrollZone;
                    SetHotZone(autoScrollZone, flag: false);
                    Redraw();
                }
                if ((uint)(autoScrollZone - 1) <= 1u)
                {
                    _scrollSpeed = (e2.X - _sliderRect.Left) * 1000 / _sliderRect.Width;
                    if (_scrollSpeed < 0)
                    {
                        _scrollSpeed = 0;
                    }
                    if (_scrollSpeed > 1000)
                    {
                        _scrollSpeed = 1000;
                    }
                }
                else
                {
                    _scrollSpeed = 0;
                }
            }
            else
            {
                if (_hotZone != AutoScrollZone.None)
                {
                    Cursor = Cursors.IBeam;
                }
                SetHotZone(AutoScrollZone.None, flag: true);
            }
        }
        else if (_hasSelection && e2.Button == MouseButtons.Left)
        {
            if (e2.Y < 0)
            {
                _autoScrollDelta = -1 + e2.Y / 10;
                _timer.Enabled = true;
            }
            else if (e2.Y > Height)
            {
                _autoScrollDelta = 1 + (e2.Y - Height) / 10;
                _timer.Enabled = true;
            }
            else
            {
                _autoScrollDelta = 0;
                _timer.Enabled = false;
            }
        }
        base.OnMouseMove(e2);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_smartScrollEnabled)
        {
            if (e.Button == MouseButtons.Left)
            {
                SetHotZone(AutoScrollZone.None, flag: true);
                _scrollSpeed = 0;
                Redraw();
            }
        }
        else
        {
            _timer.Enabled = false;
            _autoScrollDelta = 0;
        }
        base.OnMouseUp(e);
    }

    internal static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int value2)
    {
        GraphicsPath graphicsPath = new GraphicsPath();
        graphicsPath.AddArc(rectangle.X, rectangle.Y, value2 * 2, value2 * 2, 180f, 90f);
        graphicsPath.AddLine(rectangle.X + value2, rectangle.Y, rectangle.Right - value2 * 2, rectangle.Y);
        graphicsPath.AddArc(rectangle.X + rectangle.Width - value2 * 2, rectangle.Y, value2 * 2, value2 * 2, 270f, 90f);
        graphicsPath.AddLine(rectangle.Right, rectangle.Y + value2 * 2, rectangle.Right, rectangle.Y + rectangle.Height - value2 * 2);
        graphicsPath.AddArc(rectangle.X + rectangle.Width - value2 * 2, rectangle.Y + rectangle.Height - value2 * 2, value2 * 2, value2 * 2, 0f, 90f);
        graphicsPath.AddLine(rectangle.Right - value2 * 2, rectangle.Bottom, rectangle.X + value2 * 2, rectangle.Bottom);
        graphicsPath.AddArc(rectangle.X, rectangle.Bottom - value2 * 2, value2 * 2, value2 * 2, 90f, 90f);
        graphicsPath.AddLine(rectangle.X, rectangle.Bottom - value2 * 2, rectangle.X, rectangle.Y + value2 * 2);
        graphicsPath.CloseFigure();
        return graphicsPath;
    }

    protected override void OnPaint(PaintEventArgs e2)
    {
        base.OnPaint(e2);
        e2.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int num = _zoneRect.Height;
        ImageAttributes imageAttributes = new ImageAttributes();
        imageAttributes.SetColorKey(Color.Magenta, Color.Magenta);
        if (_activeZone != AutoScrollZone.None)
        {
            bool flag = _activeZone == AutoScrollZone.Down;
            Image image = (flag ? Resources.fastDown : Resources.fastUp);
            GraphicsPath path = CreateRoundedRectangle(_fastScrollButton, _fastScrollButton.Width / 8);
            e2.Graphics.FillPath(new SolidBrush(Color.FromArgb(200, SystemColors.ButtonFace)), path);
            e2.Graphics.DrawPath(Pens.Black, path);
            e2.Graphics.DrawImage(image, _fastScrollButton, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, imageAttributes);
            return;
        }
        bool flag2 = _hotZone == AutoScrollZone.Down;
        Image image2 = (flag2 ? Resources.fastDown : Resources.fastUp);
        Image image3 = (flag2 ? Resources.down : Resources.up);
        if (_hotZone != AutoScrollZone.None)
        {
            int num2 = ((_hotZone == AutoScrollZone.Up) ? _sliderRect.Top : _sliderRect.Bottom);
            int num3 = ((_hotZone != AutoScrollZone.Up) ? _sliderRect.Top : _sliderRect.Bottom);
            e2.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(200, SystemColors.ButtonFace)), _zoneRect);
            Rectangle destRect = new Rectangle(_zoneRect.Left, _zoneRect.Top, num, num);
            e2.Graphics.DrawImage(image3, destRect, 0, 0, image3.Width, image3.Height, GraphicsUnit.Pixel, imageAttributes);
            destRect = new Rectangle(_zoneRect.Right - num, _zoneRect.Top, num, num);
            e2.Graphics.DrawImage(image2, destRect, 0, 0, image2.Width, image2.Height, GraphicsUnit.Pixel, imageAttributes);
            int num4 = _sliderRect.Width * _scrollSpeed / 1000;
            int num5 = num4 * _sliderRect.Height / _sliderRect.Width;
            if (_hotZone == AutoScrollZone.Up)
            {
                num5 = -num5;
            }
            e2.Graphics.FillPolygon(new SolidBrush(Color.FromArgb(200, SystemColors.Highlight)), new Point[3]
            {
                new Point(_sliderRect.Left, num2),
                new Point(_sliderRect.Left + num4, num2),
                new Point(_sliderRect.Left + num4, num2 - num5)
            });
            e2.Graphics.DrawLine(Pens.Black, _sliderRect.Left, num2, _sliderRect.Right, num2);
            e2.Graphics.DrawLine(Pens.Black, _sliderRect.Right, num2, _sliderRect.Right, num3);
            e2.Graphics.DrawLine(Pens.Black, _sliderRect.Left, num2, _sliderRect.Right, num3);
        }
    }

    protected virtual void ScrollBy(int value2)
    {
        TopLine += value2;
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        if (!_smartScrollEnabled)
        {
            ScrollBy(_autoScrollDelta);
        }
        else
        {
            if (_scrollSpeed == 0)
            {
                return;
            }
            int num = ((_hotZone == AutoScrollZone.Down) ? 1 : (-1));
            double num2 = ((_activeZone == AutoScrollZone.None) ? (Math.Exp((double)_scrollSpeed / 120.0) / 2.0) : ((double)num));
            if (num2 > 1.0)
            {
                TopLine += (int)num2 * num;
                this._scrollRemainder = 0;
            }
            else
            {
                int num3 = (int)(1.0 / num2);
                if (++this._scrollRemainder > num3)
                {
                    TopLine += num;
                    this._scrollRemainder = 0;
                }
            }
            base.OnMouseMove(_lastMouse);
        }
    }
}
