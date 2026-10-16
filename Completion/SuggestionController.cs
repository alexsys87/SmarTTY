using System.Drawing;

namespace SmarTTY.Completion;

public class SuggestionController
{
    private SuggestionPopup _popup;

    private ICompletionHost _host;

    public Color BackColor;

    public bool IsVisible
    {
        get
        {
            if (_popup != null)
            {
                return _popup.IsVisible;
            }
            return false;
        }
    }

    public SuggestionController(ICompletionHost completionHost)
    {
        _host = completionHost;
    }

    public void Show(ISuggestionSource suggestionSource, bool flag, bool flag2)
    {
        if (_popup != null)
        {
            _popup.Hide();
        }
        _popup = new SuggestionPopup(suggestionSource, _host, flag2);
        _popup.BackColor = this.BackColor;
        if (_popup != null)
        {
            _popup.Show(flag);
        }
    }

    public void Hide()
    {
        if (_popup != null)
        {
            _popup.Hide();
        }
    }

    public void SelectMatch(string text, bool flag)
    {
        if (_popup != null && text != null)
        {
            _popup.SelectMatch(text, flag);
        }
    }

    public void AcceptSelected()
    {
        if (IsVisible)
        {
            _popup.AcceptSelected();
        }
    }
}
