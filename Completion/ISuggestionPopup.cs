using SmarTTY.TextEditing;

namespace SmarTTY.Completion;

/// <summary>The list of suggestions the <see cref="SuggestionController"/> shows and drives; the window is the view's.</summary>
public interface ISuggestionPopup
{
    /// <summary>True while the popup is on the screen.</summary>
    bool IsVisible { get; }

    /// <summary>The background colour of the list.</summary>
    RgbColor BackColor { set; }

    /// <summary>The item that is selected in the list, or null.</summary>
    ISuggestion SelectedSuggestion { get; }

    /// <summary>Shows the popup under the caret, fitted to its items when <paramref name="autoSize"/> is set.</summary>
    void Show(bool autoSize);

    /// <summary>Hides the popup; it can be shown again.</summary>
    void Hide();

    /// <summary>Selects the item that begins with the text (or equals it, when <paramref name="exactMatch"/> is set).</summary>
    void SelectMatch(string prefix, bool exactMatch);

    /// <summary>Accepts the selected item.</summary>
    void AcceptSelected();

    /// <summary>Closes the window of the popup for good.</summary>
    void Dispose();
}

/// <summary>Makes the popups of a <see cref="SuggestionController"/>; each popup is a new window.</summary>
public interface ISuggestionPopupFactory
{
    /// <summary>Makes a popup that lists the items of the source.</summary>
    ISuggestionPopup Create(ISuggestionSource suggestionSource, bool allowMultiSelect);
}
