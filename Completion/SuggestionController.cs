using SmarTTY.TextEditing;

namespace SmarTTY.Completion;

public class SuggestionController
{
    private ISuggestionPopup _popup;

    private ISuggestionPopupFactory _popupFactory;

    public RgbColor BackColor;

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

    public SuggestionController(ISuggestionPopupFactory popupFactory)
    {
        _popupFactory = popupFactory;
    }

    public void Show(ISuggestionSource suggestionSource, bool autoSize, bool allowMultiSelect)
    {
        if (_popup != null)
        {
            _popup.Dispose();
        }
        _popup = _popupFactory.Create(suggestionSource, allowMultiSelect);
        _popup.BackColor = this.BackColor;
        if (_popup != null)
        {
            _popup.Show(autoSize);
        }
    }

    /// <summary>
    /// Shows a catalog through the popup: the popup works on a snapshot of the catalog, filtered by
    /// <paramref name="prefix"/> (null keeps every item). The snapshot is taken once, when the popup opens.
    /// </summary>
    public void Show(SuggestionCatalog suggestionCatalog, string prefix, bool autoSize, bool allowMultiSelect)
    {
        if (_popup != null)
        {
            _popup.Dispose();
        }
        _popup = _popupFactory.Create(suggestionCatalog.CreateSnapshot(prefix), allowMultiSelect);
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

    /// <summary>
    /// Accepts the selected item only when it continues what was typed (<paramref name="typedWord"/>, case ignored);
    /// otherwise the popup just closes. The list keeps an item selected even when nothing matches the word, and that
    /// item must not replace a name the user is typing.
    /// </summary>
    public void AcceptSelectedIfMatches(string typedWord)
    {
        if (!IsVisible)
        {
            return;
        }
        ISuggestion selected = _popup.SelectedSuggestion;
        if (selected == null || string.IsNullOrEmpty(typedWord) || selected.DisplayName == null
            || !selected.DisplayName.StartsWith(typedWord, System.StringComparison.OrdinalIgnoreCase))
        {
            _popup.Hide();
            return;
        }
        _popup.AcceptSelected();
    }
}
