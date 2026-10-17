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

    public void Show(ISuggestionSource suggestionSource, bool autoSize, bool allowMultiSelect)
    {
        if (_popup != null)
        {
            _popup.Hide();
        }
        _popup = new SuggestionPopup(suggestionSource, _host, allowMultiSelect);
        _popup.BackColor = this.BackColor;
        if (_popup != null)
        {
            _popup.Show(autoSize);
        }
    }

    public void Hide()
    {
        if (_popup != null)
        {
            _popup.Hide();
        }
    }

    public void SelectMatch(string prefix, bool exactMatch)
    {
        if (_popup != null && prefix != null)
        {
            _popup.SelectMatch(prefix, exactMatch);
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
