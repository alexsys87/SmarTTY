using SmarTTY.Completion;

namespace SmarTTY.Controls;

/// <summary>Makes the suggestion popups (windows) of a code editor.</summary>
internal class SuggestionPopupFactory : ISuggestionPopupFactory
{
    private readonly ICompletionHost _host;

    public SuggestionPopupFactory(ICompletionHost completionHost)
    {
        _host = completionHost;
    }

    public ISuggestionPopup Create(ISuggestionSource suggestionSource, bool allowMultiSelect)
    {
        return new SuggestionPopup(suggestionSource, _host, allowMultiSelect);
    }
}
