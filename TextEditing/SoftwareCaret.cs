using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.TextEditing;

internal class SoftwareCaret : ICaret
{
    private ICaretHost _host;

    private int _width;

    private int _height;

    private bool _overwriteMode;

    private bool _underlineStyle;

    private bool _showWhenUnfocused;

    private bool _focused;

    private bool _enabled;

    private bool _blinkOn;

    private int _yOffset;

    private Rectangle _bounds;

    private Timer _blinkTimer;

    private Timer _focusTimer;

    public bool IsEnabled => _enabled;

    public int Height
    {
        get
        {
            return _height;
        }
        set
        {
            _height = value;
            UpdateGeometry();
        }
    }

    public int Width
    {
        get
        {
            return _width;
        }
        set
        {
            _width = value;
            UpdateGeometry();
        }
    }

    public bool OverwriteMode
    {
        get
        {
            return _overwriteMode;
        }
        set
        {
            _overwriteMode = value;
            UpdateGeometry();
        }
    }

    public bool UnderlineStyle
    {
        get
        {
            return _underlineStyle;
        }
        set
        {
            _underlineStyle = value;
            UpdateGeometry();
        }
    }

    public bool ShowWhenUnfocused
    {
        get
        {
            return _showWhenUnfocused;
        }
        set
        {
            _showWhenUnfocused = value;
        }
    }

    public SoftwareCaret(ICaretHost caretHost, int x, int y)
    {
        _host = caretHost;
        _host.HostControl.GotFocus += OnGotFocus;
        _host.HostControl.LostFocus += OnLostFocus;
        _host.HostControl.Paint += OnPaint;
        _bounds = new Rectangle(x, y, _bounds.Width, _bounds.Height);
        _enabled = true;
        Timer timer = (this._blinkTimer = new Timer());
        timer.Interval = 530;
        timer.Enabled = true;
        timer.Tick += OnBlink;
        timer.Start();
        _focusTimer = new Timer();
        _focusTimer.Interval = 100;
        _focusTimer.Enabled = true;
        _focusTimer.Tick += PollFocus;
    }

    private void PollFocus(object sender, EventArgs e)
    {
        if (_focused != _host.HostControl.Focused)
        {
            if (_host.HostControl.Focused)
            {
                OnGotFocus(_host, e);
            }
            else
            {
                OnLostFocus(_host, e);
            }
        }
    }

    private void OnBlink(object sender, EventArgs e)
    {
        _blinkOn = !_blinkOn;
        _host.HostControl.Invalidate(_bounds);
    }

    public SoftwareCaret(ICaretHost caretHost)
        : this(caretHost, 0, 0)
    {
    }

    public void Enable()
    {
        _enabled = true;
    }

    public void Disable()
    {
    }

    public void MoveTo(int x, int y)
    {
        _host.HostControl.Invalidate(_bounds);
        _bounds = new Rectangle(x, y + _yOffset, _bounds.Width, _bounds.Height);
        _host.HostControl.Invalidate(_bounds);
        _blinkTimer.Stop();
        _blinkTimer.Start();
        _blinkOn = true;
    }

    public void SetPosition(Point point)
    {
        MoveTo(point.X, point.Y);
    }

    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        _host.HostControl.Invalidate(_bounds);
    }

    private void OnLostFocus(object sender, EventArgs e)
    {
        _focused = false;
        _host.HostControl.Invalidate(_bounds);
    }

    private void UpdateGeometry()
    {
        _host.HostControl.Invalidate(_bounds);
        int width = 1;
        if (_width != 0)
        {
            width = _width;
        }
        else if (_overwriteMode || _underlineStyle)
        {
            width = (int)Graphics.FromHwnd(_host.HostControl.Handle).MeasureString(new string('O', 10), _host.HostControl.Font).Width / 10;
        }
        int caretHeight = _height;
        if (caretHeight == 0)
        {
            caretHeight = _host.HostControl.Font.Height;
        }
        if (_underlineStyle)
        {
            caretHeight = 2;
            _yOffset = _host.HostControl.Font.Height - caretHeight;
        }
        else
        {
            _yOffset = 0;
        }
        _bounds.Width = width;
        _bounds.Height = caretHeight;
        _host.CaretBounds = _bounds;
        _host.HostControl.Invalidate(_bounds);
        _blinkTimer.Stop();
        _blinkTimer.Start();
        _blinkOn = true;
    }

    private void OnPaint(object sender, PaintEventArgs e)
    {
        if (_enabled && (_focused || _showWhenUnfocused) && _blinkOn)
        {
            e.Graphics.FillRectangle(Brushes.Black, _bounds);
        }
    }
}
