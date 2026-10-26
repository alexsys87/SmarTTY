using SmarTTY.Forms;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Completion;

internal class SuggestionPopup
{
    private SuggestionListForm _form;

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

    internal SuggestionPopup(SuggestionCatalog suggestionCatalog, string filterText, ICompletionHost completionHost, bool allowMultiSelect)
        : this(suggestionCatalog.CreateSnapshot(filterText), completionHost, allowMultiSelect)
    {
    }

    internal SuggestionPopup(ISuggestionSource suggestionSource, ICompletionHost completionHost, bool allowMultiSelect)
    {
        _form = new SuggestionListForm(completionHost, allowMultiSelect);
        _maxWidth = _form.Width;
        _maxHeight = _form.Height;
        _source = suggestionSource;
        _form.SetSource(_source);
        _host = completionHost;
    }

    public void SelectMatch(string prefix, bool exactMatch)
    {
        _form.SelectMatch(prefix, exactMatch);
    }

    public void Show(bool autoSize)
    {
        Rectangle bounds = _host.CaretScreenBounds;
        Screen screen = Screen.FromControl(_host.HostControl);
        int xPos = bounds.Left - 16;
        if (xPos < screen.WorkingArea.Left)
        {
            xPos = screen.WorkingArea.Left;
        }
        if (xPos + _form.Width >= screen.WorkingArea.Right)
        {
            xPos = screen.WorkingArea.Right - _form.Width;
        }
        if (autoSize)
        {
            Graphics graphics = Graphics.FromHwnd(_form.Handle);
            int maxItemWidth = 0;
            foreach (ISuggestion suggestion in _source.Items)
            {
                SizeF sizeF = graphics.MeasureString(suggestion.DisplayName, _form.Font);
                if ((int)sizeF.Width > maxItemWidth)
                {
                    maxItemWidth = (int)sizeF.Width;
                }
            }
            if (maxItemWidth > _maxWidth)
            {
                maxItemWidth = _maxWidth;
            }
            _form.SetSize(maxItemWidth, 0, _maxHeight);
        }
        int yPos = bounds.Bottom + 5;
        if (yPos + _form.Height >= screen.WorkingArea.Bottom)
        {
            yPos = bounds.Top - 5 - _form.Height;
        }
        _form.Top = yPos;
        _form.Left = xPos;
        if (!_form.Visible)
        {
            _form.Show();
            _form.FocusList();
        }
        _form.NotifyHost(active: true);
    }

    public void Hide()
    {
        if (_form.Visible)
        {
            _form.Hide();
            _form.NotifyHost(active: false);
        }
    }

    private static int CommonPrefixLength(string first, string second)
    {
        int limit = Math.Min(first.Length, second.Length);
        for (int i = 0; i < limit; i++)
        {
            if (first[i] != second[i])
            {
                return i;
            }
        }
        return limit;
    }

    public string GetCommonPrefix(string prefix)
    {
        string commonPrefix = null;
        foreach (ISuggestion suggestion in _source.Items)
        {
            if (suggestion.DisplayName.Length < prefix.Length || suggestion.DisplayName.Substring(0, prefix.Length) != prefix)
            {
                continue;
            }
            if (commonPrefix == null)
            {
                commonPrefix = suggestion.DisplayName;
                continue;
            }
            int len = CommonPrefixLength(suggestion.DisplayName, commonPrefix);
            if (len < commonPrefix.Length)
            {
                commonPrefix = commonPrefix.Substring(0, len);
            }
        }
        return commonPrefix;
    }

    internal void AcceptSelected()
    {
        if (_form != null && _form.Visible)
        {
            _form.AcceptSelected();
        }
    }
}
