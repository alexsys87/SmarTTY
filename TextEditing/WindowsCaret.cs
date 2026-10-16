using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmarTTY.TextEditing;

internal class WindowsCaret : ICaret
{
    private Control _control;

    private int _x;

    private int _y;

    private int _width;

    private int _height;

    private bool _overwriteMode;

    private bool _underlineStyle;

    private bool _showWhenUnfocused;

    private bool _created;

    private bool _focused;

    private bool _enabled;

    private int _yOffset;

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
            Recreate();
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
            Recreate();
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
            Recreate();
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
            Recreate();
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
            if (value)
            {
                if (_enabled)
                {
                    EnsureCreated();
                }
            }
            else if (!value && !_focused && _created)
            {
                if (_created)
                {
                    DestroyCaret();
                }
                _created = false;
            }
        }
    }

    [DllImport("User32")]
    private static extern bool CreateCaret(IntPtr handle, IntPtr handle2, int value2, int value3);

    [DllImport("User32", SetLastError = true)]
    private static extern bool ShowCaret(IntPtr handle);

    [DllImport("User32")]
    private static extern bool DestroyCaret();

    [DllImport("User32")]
    private static extern bool SetCaretPos(int value2, int value3);

    public WindowsCaret(ICaretHost caretHost, int value2, int value3)
    {
        _control = caretHost.HostControl;
        _control.GotFocus += OnGotFocus;
        _control.LostFocus += OnLostFocus;
        this._x = value2;
        this._y = value3;
        _yOffset = 0;
        _enabled = true;
    }

    public WindowsCaret(ICaretHost caretHost)
        : this(caretHost, 0, 0)
    {
    }

    public void Enable()
    {
        if (!_enabled)
        {
            _enabled = true;
            if (_focused)
            {
                EnsureCreated();
            }
        }
    }

    public void Disable()
    {
        if (_enabled)
        {
            _enabled = false;
            if (_created)
            {
                DestroyCaret();
                _created = false;
            }
        }
    }

    public void MoveTo(int value2, int value3)
    {
        this._x = value2;
        this._y = value3;
        if (_created)
        {
            SetCaretPos(this._x, this._y + _yOffset);
        }
    }

    public void SetPosition(Point point)
    {
        MoveTo(point.X, point.Y);
    }

    private void OnGotFocus(object sender, EventArgs e)
    {
        _focused = true;
        if (_enabled)
        {
            EnsureCreated();
        }
    }

    private void OnLostFocus(object sender, EventArgs e)
    {
        _focused = false;
        if (!_showWhenUnfocused)
        {
            if (_created)
            {
                DestroyCaret();
            }
            _created = false;
        }
    }

    private void Recreate()
    {
        if (_created)
        {
            DestroyCaret();
        }
        _created = false;
        if (_focused && _enabled)
        {
            EnsureCreated();
        }
    }

    private void EnsureCreated()
    {
        if (!_created)
        {
            int num = 0;
            if (_width != 0)
            {
                num = _width;
            }
            else if (_overwriteMode || _underlineStyle)
            {
                num = (int)Graphics.FromHwnd(_control.Handle).MeasureString(new string('O', 10), _control.Font).Width / 10;
            }
            int num2 = _height;
            if (num2 == 0)
            {
                num2 = _control.Font.Height;
            }
            if (_underlineStyle)
            {
                num2 = 2;
                _yOffset = _control.Font.Height - num2;
            }
            else
            {
                _yOffset = 0;
            }
            CreateCaret(_control.Handle, IntPtr.Zero, num, num2);
            SetCaretPos(_x, _y + _yOffset);
            _created = true;
        }
        if (!ShowCaret(_control.Handle))
        {
            DestroyCaret();
            _created = false;
            EnsureCreated();
        }
    }
}
