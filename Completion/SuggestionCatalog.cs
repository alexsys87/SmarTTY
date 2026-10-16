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

        public SuggestionItem(string text, string text2, string text3, string text4, int value2)
        {
            this.Name = text;
            this.Detail = text2;
            this.Hint = text3;
            Documentation = text4;
            IconIndex = value2;
        }
    }

    private class SuggestionSnapshot : ISuggestionSource
    {
        private List<ISuggestion> _items;

        private SuggestionCatalog _catalog;

        public IEnumerable Images => _catalog.Images;

        public IEnumerable<ISuggestion> Items => _items;

        public SuggestionSnapshot(SuggestionCatalog suggestionCatalog, List<SuggestionItem> list)
        {
            this._catalog = suggestionCatalog;
            this._items = new List<ISuggestion>();
            foreach (SuggestionItem item in list)
            {
                this._items.Add(item);
            }
        }
    }

    private List<Image> _images = new List<Image>();

    private Dictionary<string, SuggestionItem> _items = new Dictionary<string, SuggestionItem>();

    internal List<Image> Images => _images;

    public bool Add(string text, int value2, string text2, string text3, string text4)
    {
        if (value2 >= this._images.Count)
        {
            value2 = -1;
        }
        if (!this._items.ContainsKey(text))
        {
            this._items.Add(text, new SuggestionItem(text, text2, text3, text4, value2));
        }
        return false;
    }

    public int AddImage(Image image)
    {
        _images.Add(image);
        return _images.Count - 1;
    }

    internal SuggestionPopup CreatePopup(string text, ICompletionHost completionHost)
    {
        return new SuggestionPopup(this, text, completionHost, flag: true);
    }

    internal ISuggestionSource CreateSnapshot(string text)
    {
        List<SuggestionItem> list = new List<SuggestionItem>();
        foreach (KeyValuePair<string, SuggestionItem> item in _items)
        {
            if (text == null || (item.Key.Length < text.Length && !(item.Key.Substring(0, text.Length) != text)))
            {
                list.Add(item.Value);
            }
        }
        return new SuggestionSnapshot(this, list);
    }

    public int CountMatching(string text)
    {
        int num = 0;
        foreach (KeyValuePair<string, SuggestionItem> item in _items)
        {
            if (text == null || (item.Key.Length >= text.Length && !(item.Key.Substring(0, text.Length) != text)))
            {
                num++;
            }
        }
        return num;
    }

    public bool Contains(string text)
    {
        return _items.ContainsKey(text);
    }

    public SuggestionItem Find(string text)
    {
        try
        {
            return _items[text];
        }
        catch (Exception)
        {
            return null;
        }
    }
}
