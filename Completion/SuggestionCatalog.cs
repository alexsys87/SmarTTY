using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

namespace SmarTTY.Completion;

public class SuggestionCatalog
{
    public class SuggestionItem : ISuggestion
    {
        public string Name;

        public string Detail;

        public string Hint;

        public string Documentation;

        public int IconIndex;

        private int IconIndexValue => IconIndex;

        private string NameValue => Name;

        private string DetailValue => Detail;

        int ISuggestion.ImageIndex => IconIndexValue;

        string ISuggestion.DisplayName => NameValue;

        string ISuggestion.DetailText => DetailValue;

        public string HintText => Hint;

        public string HintDescription => null;

        public bool IsBold => false;

        public SuggestionItem(string name, string detail, string hint, string documentation, int iconIndex)
        {
            this.Name = name;
            this.Detail = detail;
            this.Hint = hint;
            Documentation = documentation;
            IconIndex = iconIndex;
        }
    }

    private class SuggestionSnapshot : ISuggestionSource
    {
        private List<ISuggestion> _items;

        private SuggestionCatalog _catalog;

        public IEnumerable Images => _catalog.Images;

        public IEnumerable<ISuggestion> Items => _items;

        public SuggestionSnapshot(SuggestionCatalog suggestionCatalog, List<SuggestionItem> itemsList)
        {
            this._catalog = suggestionCatalog;
            this._items = new List<ISuggestion>();
            foreach (SuggestionItem item in itemsList)
            {
                this._items.Add(item);
            }
        }
    }

    private List<Image> _images = new List<Image>();

    private Dictionary<string, SuggestionItem> _items = new Dictionary<string, SuggestionItem>();

    internal List<Image> Images => _images;

    public bool Add(string name, int imageIndex, string detail, string hint, string documentation)
    {
        if (imageIndex >= this._images.Count)
        {
            imageIndex = -1;
        }
        if (!this._items.ContainsKey(name))
        {
            this._items.Add(name, new SuggestionItem(name, detail, hint, documentation, imageIndex));
        }
        return false;
    }

    /// <summary>
    /// Fills the catalog from a list of suggestions (the editor's completion items): a keyword or API symbol
    /// arrives with its detail, hint and documentation. Used to offer an already built list through the popup.
    /// </summary>
    public void Load(IEnumerable<ISuggestion> suggestions)
    {
        if (suggestions == null)
        {
            return;
        }
        foreach (ISuggestion suggestion in suggestions)
        {
            if (suggestion != null && !string.IsNullOrEmpty(suggestion.DisplayName) && !_items.ContainsKey(suggestion.DisplayName))
            {
                _items.Add(suggestion.DisplayName, new SuggestionItem(
                    suggestion.DisplayName,
                    suggestion.DetailText,
                    suggestion.HintText,
                    suggestion.HintDescription,
                    suggestion.ImageIndex));
            }
        }
    }

    /// <summary>Drops every item (the images stay; they belong to the list, not to one fill).</summary>
    public void Clear()
    {
        _items.Clear();
    }

    internal SuggestionPopup CreatePopup(string prefix, ICompletionHost completionHost, bool allowMultiSelect)
    {
        return new SuggestionPopup(this, prefix, completionHost, allowMultiSelect);
    }

    internal ISuggestionSource CreateSnapshot(string prefix)
    {
        List<SuggestionItem> itemsList = new List<SuggestionItem>();
        foreach (KeyValuePair<string, SuggestionItem> item in _items)
        {
            // The same rule as CountMatching: a key shorter than the prefix cannot start with it. (The old test
            // could ask for a substring that runs past the end of a short key and threw ArgumentOutOfRange.)
            if (SuggestionFilter.MatchesPrefix(item.Key, prefix))
            {
                itemsList.Add(item.Value);
            }
        }
        return new SuggestionSnapshot(this, itemsList);
    }

    public int CountMatching(string prefix)
    {
        int matchCount = 0;
        foreach (KeyValuePair<string, SuggestionItem> item in _items)
        {
            if (SuggestionFilter.MatchesPrefix(item.Key, prefix))
            {
                matchCount++;
            }
        }
        return matchCount;
    }

    public bool Contains(string name)
    {
        return _items.ContainsKey(name);
    }

    public SuggestionItem Find(string name)
    {
        try
        {
            return _items[name];
        }
        catch (Exception)
        {
            return null;
        }
    }
}
