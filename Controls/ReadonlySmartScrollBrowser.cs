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

    private void SetHotZone(AutoScrollZone autoScrollZone, bool resetActive)
    {
        if (resetActive)
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_smartScrollEnabled)
        {
            if (_activeZone != AutoScrollZone.None && e.Button == MouseButtons.Left && _fastScrollButton.Contains(e.Location))
            {
                SetHotZone(_activeZone, resetActive: false);
                _activeZone = AutoScrollZone.None;
                _zoneDismissed = true;
                Redraw();
            }
            if (_hasSelection && e.Button == MouseButtons.Left)
            {
                _lastMouse = e;
                AutoScrollZone autoScrollZone = AutoScrollZone.None;
                if (TextArea.Bottom - e.Y < 32)
                {
                    autoScrollZone = AutoScrollZone.Down;
                }
                else if (e.Y - TextArea.Top < 32)
                {
                    autoScrollZone = AutoScrollZone.Up;
                }
                if (!_zoneDismissed)
                {
                    SetHotZone(autoScrollZone, resetActive: false);
                }
                else if (autoScrollZone != AutoScrollZone.None)
                {
                    SetHotZone(autoScrollZone, resetActive: false);
                }
                if (autoScrollZone != AutoScrollZone.None && _activeZone != autoScrollZone && !_zoneDismissed)
                {
                    int value = 32;
                    int currentValue = ((e.X <= TextArea.Right - (value + 5)) ? (e.X + 5) : (e.X - value - 5));
                    if (autoScrollZone == AutoScrollZone.Down)
                    {
                        _fastScrollButton = new Rectangle(currentValue, TextArea.Bottom - 32 - value - 5, value, value);
                    }
                    else
                    {
                        _fastScrollButton = new Rectangle(currentValue, TextArea.Top + 32 + 5, value, value);
                    }
                    _activeZone = autoScrollZone;
                    SetHotZone(autoScrollZone, resetActive: false);
                    Redraw();
                }
                if ((uint)(autoScrollZone - 1) <= 1u)
                {
                    _scrollSpeed = (e.X - _sliderRect.Left) * 1000 / _sliderRect.Width;
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
                SetHotZone(AutoScrollZone.None, resetActive: true);
            }
        }
        else if (_hasSelection && e.Button == MouseButtons.Left)
        {
            if (e.Y < 0)
            {
                _autoScrollDelta = -1 + e.Y / 10;
                _timer.Enabled = true;
            }
            else if (e.Y > Height)
            {
                _autoScrollDelta = 1 + (e.Y - Height) / 10;
                _timer.Enabled = true;
            }
            else
            {
                _autoScrollDelta = 0;
                _timer.Enabled = false;
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_smartScrollEnabled)
        {
            if (e.Button == MouseButtons.Left)
            {
                SetHotZone(AutoScrollZone.None, resetActive: true);
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

    internal static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int value)
    {
        GraphicsPath graphicsPath = new GraphicsPath();
        graphicsPath.AddArc(rectangle.X, rectangle.Y, value * 2, value * 2, 180f, 90f);
        graphicsPath.AddLine(rectangle.X + value, rectangle.Y, rectangle.Right - value * 2, rectangle.Y);
        graphicsPath.AddArc(rectangle.X + rectangle.Width - value * 2, rectangle.Y, value * 2, value * 2, 270f, 90f);
        graphicsPath.AddLine(rectangle.Right, rectangle.Y + value * 2, rectangle.Right, rectangle.Y + rectangle.Height - value * 2);
        graphicsPath.AddArc(rectangle.X + rectangle.Width - value * 2, rectangle.Y + rectangle.Height - value * 2, value * 2, value * 2, 0f, 90f);
        graphicsPath.AddLine(rectangle.Right - value * 2, rectangle.Bottom, rectangle.X + value * 2, rectangle.Bottom);
        graphicsPath.AddArc(rectangle.X, rectangle.Bottom - value * 2, value * 2, value * 2, 90f, 90f);
        graphicsPath.AddLine(rectangle.X, rectangle.Bottom - value * 2, rectangle.X, rectangle.Y + value * 2);
        graphicsPath.CloseFigure();
        return graphicsPath;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int height = _zoneRect.Height;
        ImageAttributes imageAttributes = new ImageAttributes();
        imageAttributes.SetColorKey(Color.Magenta, Color.Magenta);
        if (_activeZone != AutoScrollZone.None)
        {
            bool isFastScrollDown = _activeZone == AutoScrollZone.Down;
            Image fastScrollImage = (isFastScrollDown ? Resources.fastDown : Resources.fastUp);
            GraphicsPath path = CreateRoundedRectangle(_fastScrollButton, _fastScrollButton.Width / 8);
            e.Graphics.FillPath(new SolidBrush(Color.FromArgb(200, SystemColors.ButtonFace)), path);
            e.Graphics.DrawPath(Pens.Black, path);
            e.Graphics.DrawImage(fastScrollImage, _fastScrollButton, 0, 0, fastScrollImage.Width, fastScrollImage.Height, GraphicsUnit.Pixel, imageAttributes);
            return;
        }
        bool isHotZoneDown = _hotZone == AutoScrollZone.Down;
        Image scrollDirectionImage = (isHotZoneDown ? Resources.fastDown : Resources.fastUp);
        Image arrowImage = (isHotZoneDown ? Resources.down : Resources.up);
        if (_hotZone != AutoScrollZone.None)
        {
            int value = ((_hotZone == AutoScrollZone.Up) ? _sliderRect.Top : _sliderRect.Bottom);
            int currentValue = ((_hotZone != AutoScrollZone.Up) ? _sliderRect.Top : _sliderRect.Bottom);
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(200, SystemColors.ButtonFace)), _zoneRect);
            Rectangle destRect = new Rectangle(_zoneRect.Left, _zoneRect.Top, height, height);
            e.Graphics.DrawImage(arrowImage, destRect, 0, 0, arrowImage.Width, arrowImage.Height, GraphicsUnit.Pixel, imageAttributes);
            destRect = new Rectangle(_zoneRect.Right - height, _zoneRect.Top, height, height);
            e.Graphics.DrawImage(scrollDirectionImage, destRect, 0, 0, scrollDirectionImage.Width, scrollDirectionImage.Height, GraphicsUnit.Pixel, imageAttributes);
            int candidateValue = _sliderRect.Width * _scrollSpeed / 1000;
            int width = candidateValue * _sliderRect.Height / _sliderRect.Width;
            if (_hotZone == AutoScrollZone.Up)
            {
                width = -width;
            }
            e.Graphics.FillPolygon(new SolidBrush(Color.FromArgb(200, SystemColors.Highlight)), new Point[3]
            {
                new Point(_sliderRect.Left, value),
                new Point(_sliderRect.Left + candidateValue, value),
                new Point(_sliderRect.Left + candidateValue, value - width)
            });
            e.Graphics.DrawLine(Pens.Black, _sliderRect.Left, value, _sliderRect.Right, value);
            e.Graphics.DrawLine(Pens.Black, _sliderRect.Right, value, _sliderRect.Right, currentValue);
            e.Graphics.DrawLine(Pens.Black, _sliderRect.Left, value, _sliderRect.Right, currentValue);
        }
    }

    protected virtual void ScrollBy(int scrollOffset)
    {
        TopLine += scrollOffset;
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
            int value = ((_hotZone == AutoScrollZone.Down) ? 1 : (-1));
            double currentValue = ((_activeZone == AutoScrollZone.None) ? (Math.Exp((double)_scrollSpeed / 120.0) / 2.0) : ((double)value));
            if (currentValue > 1.0)
            {
                TopLine += (int)currentValue * value;
                this._scrollRemainder = 0;
            }
            else
            {
                int candidateValue = (int)(1.0 / currentValue);
                if (++this._scrollRemainder > candidateValue)
                {
                    TopLine += value;
                    this._scrollRemainder = 0;
                }
            }
            base.OnMouseMove(_lastMouse);
        }
    }
}
