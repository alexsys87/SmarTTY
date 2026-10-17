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

    public int AddImage(Image image)
    {
        _images.Add(image);
        return _images.Count - 1;
    }

    internal SuggestionPopup CreatePopup(string prefix, ICompletionHost completionHost)
    {
        return new SuggestionPopup(this, prefix, completionHost, allowMultiSelect: true);
    }

    internal ISuggestionSource CreateSnapshot(string prefix)
    {
        List<SuggestionItem> itemsList = new List<SuggestionItem>();
        foreach (KeyValuePair<string, SuggestionItem> item in _items)
        {
            if (prefix == null || (item.Key.Length < prefix.Length && !(item.Key.Substring(0, prefix.Length) != prefix)))
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
            if (prefix == null || (item.Key.Length >= prefix.Length && !(item.Key.Substring(0, prefix.Length) != prefix)))
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
