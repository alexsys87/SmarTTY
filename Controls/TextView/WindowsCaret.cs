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
    private static extern bool CreateCaret(IntPtr hWnd, IntPtr hBitmap, int width, int height);

    [DllImport("User32", SetLastError = true)]
    private static extern bool ShowCaret(IntPtr hWnd);

    [DllImport("User32")]
    private static extern bool DestroyCaret();

    [DllImport("User32")]
    private static extern bool SetCaretPos(int x, int y);

    public WindowsCaret(ICaretHost caretHost, int x, int y)
    {
        _control = caretHost.HostControl;
        _control.GotFocus += OnGotFocus;
        _control.LostFocus += OnLostFocus;
        this._x = x;
        this._y = y;
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

    public void MoveTo(int x, int y)
    {
        this._x = x;
        this._y = y;
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
        // The second attempt is for a caret another window took meanwhile; a window that cannot own a caret (it has
        // no focus any more) must not be retried for ever.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_created)
            {
                CreateNow();
            }
            if (ShowCaret(_control.Handle))
            {
                return;
            }
            DestroyCaret();
            _created = false;
        }
    }

    private void CreateNow()
    {
        int caretWidth = 0;
        if (_width != 0)
        {
            caretWidth = _width;
        }
        else if (_overwriteMode || _underlineStyle)
        {
            using (Graphics graphics = Graphics.FromHwnd(_control.Handle))
            {
                caretWidth = (int)graphics.MeasureString(new string('O', 10), _control.Font).Width / 10;
            }
        }
        int caretHeight = _height;
        if (caretHeight == 0)
        {
            caretHeight = _control.Font.Height;
        }
        if (_underlineStyle)
        {
            caretHeight = 2;
            _yOffset = _control.Font.Height - caretHeight;
        }
        else
        {
            _yOffset = 0;
        }
        CreateCaret(_control.Handle, IntPtr.Zero, caretWidth, caretHeight);
        SetCaretPos(_x, _y + _yOffset);
        _created = true;
    }
}
