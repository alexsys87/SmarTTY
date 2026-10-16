using SmarTTY.Forms;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Completion;

internal class SuggestionPopup
{
    private SuggestionListForm _form;

    private IntPtr _parentHandle;

    private ISuggestionSource _source;

    private ICompletionHost _host;

    private int _maxWidth;

    private int _maxHeight;

    public Color BackColor
    {
        get
        {
            return _form.ListBackColor;
        }
        set
        {
            _form.ListBackColor = value;
        }
    }

    public bool IsVisible => _form.Visible;

    internal SuggestionPopup(SuggestionCatalog suggestionCatalog, string text, ICompletionHost completionHost, bool flag)
        : this(suggestionCatalog.CreateSnapshot(text), completionHost, flag)
    {
    }

    internal SuggestionPopup(ISuggestionSource suggestionSource, ICompletionHost completionHost, bool flag)
    {
        _form = new SuggestionListForm(completionHost, flag);
        _maxWidth = _form.Width;
        _maxHeight = _form.Height;
        _source = suggestionSource;
        _form.SetSource(_source);
        _host = completionHost;
        Control parent = _host.HostControl.Parent;
        this._parentHandle = parent.Handle;
    }

    public void SelectMatch(string text, bool flag)
    {
        _form.SelectMatch(text, flag);
    }

    public void Show(bool flag)
    {
        Rectangle rectangle = _host.CaretScreenBounds;
        Screen screen = Screen.FromControl(_host.HostControl);
        int num = rectangle.Left - 16;
        if (num < screen.WorkingArea.Left)
        {
            num = screen.WorkingArea.Left;
        }
        if (num + _form.Width >= screen.WorkingArea.Right)
        {
            num = screen.WorkingArea.Right - _form.Width;
        }
        if (flag)
        {
            Graphics graphics = Graphics.FromHwnd(_form.Handle);
            int num2 = 0;
            foreach (ISuggestion item in _source.Items)
            {
                SizeF sizeF = graphics.MeasureString(item.DisplayName, _form.Font);
                if ((int)sizeF.Width > num2)
                {
                    num2 = (int)sizeF.Width;
                }
            }
            if (num2 > _maxWidth)
            {
                num2 = _maxWidth;
            }
            _form.SetSize(num2, 0, _maxHeight);
        }
        int num3 = rectangle.Bottom + 5;
        if (num3 + _form.Height >= screen.WorkingArea.Bottom)
        {
            num3 = rectangle.Top - 5 - _form.Height;
        }
        _form.Top = num3;
        _form.Left = num;
        if (!_form.Visible)
        {
            _form.Show();
            _form.FocusList();
        }
        _form.NotifyHost(flag: true);
    }

    public void Hide()
    {
        if (_form.Visible)
        {
            _form.Hide();
            _form.NotifyHost(flag: false);
        }
    }

    private static int CommonPrefixLength(string text, string text2)
    {
        int num = Math.Min(text.Length, text2.Length);
        for (int num2 = 0; num2 < num; num2++)
        {
            if (text[num2] != text2[num2])
            {
                return num2;
            }
        }
        return num;
    }

    public string GetCommonPrefix(string text2)
    {
        string text = null;
        foreach (ISuggestion item in _source.Items)
        {
            if (item.DisplayName.Length < text2.Length || item.DisplayName.Substring(0, text2.Length) != text2)
            {
                continue;
            }
            if (text == null)
            {
                text = item.DisplayName;
                continue;
            }
            int num = CommonPrefixLength(item.DisplayName, text);
            if (num < text.Length)
            {
                text = text.Substring(0, num);
            }
        }
        return text;
    }

    internal void AcceptSelected()
    {
        if (_form != null && _form.Visible)
        {
            _form.AcceptSelected();
        }
    }
}
