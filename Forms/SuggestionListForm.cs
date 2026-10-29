using SmarTTY.Completion;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

internal partial class SuggestionListForm : Form
{
    public bool _showHints = true;

    private TextHintForm _hintForm = new TextHintForm();

    private List<ListViewItem> _allItems;

    private bool _isFiltered;

    public StringComparer Comparer = StringComparer.CurrentCultureIgnoreCase;

    private bool _suppressDeactivate;

    private bool _hostNotified;

    public bool ShowHints
    {
        get
        {
            return _showHints;
        }
        set
        {
            _showHints = value;
        }
    }

    public Color ListBackColor
    {
        get
        {
            return listView1.BackColor;
        }
        set
        {
            listView1.BackColor = value;
        }
    }

    internal static bool IsWindows()
    {
        PlatformID platform = Environment.OSVersion.Platform;
        if (platform == PlatformID.Unix || platform == PlatformID.MacOSX)
        {
            return false;
        }
        return true;
    }

    public SuggestionListForm(ICompletionHost completionHost, bool allowSorting)
    {
        InitializeComponent();
        listView1.Sorting = (allowSorting ? SortOrder.Ascending : SortOrder.None);
        listView1.Host = completionHost;
        Disposed += (sender, e) => _hintForm.Dispose();
        if (!IsWindows())
        {
            listView1.StateImageList = null;
            listView1.SmallImageList = _imageList;
        }
    }

    internal void SetSource(ISuggestionSource suggestionSource)
    {
        _imageList.Images.Clear();
        foreach (Image img in suggestionSource.Images)
        {
            _imageList.Images.Add(img);
        }
        _allItems = new List<ListViewItem>();
        foreach (ISuggestion suggestion in suggestionSource.Items)
        {
            ListViewItem listViewItem = new ListViewItem(suggestion.DisplayName);
            if (suggestion.IsBold)
            {
                listViewItem.Font = new Font(listViewItem.Font, FontStyle.Bold);
            }
            listViewItem.ImageIndex = suggestion.ImageIndex;
            listViewItem.StateImageIndex = suggestion.ImageIndex;
            listViewItem.SubItems.Add(suggestion.DetailText);
            listViewItem.Tag = suggestion;
            listView1.Items.Add(listViewItem);
            _allItems.Add(listViewItem);
        }
        listView1.Sort();
    }

    private void SelectIndex(int index)
    {
        listView1.SelectedIndices.Clear();
        if (index < listView1.Items.Count)
        {
            ListViewItem listViewItem = listView1.Items[index];
            bool selected = (listView1.Items[index].Focused = true);
            listViewItem.Selected = selected;
            listView1.Items[index].EnsureVisible();
        }
    }

    public void FocusList()
    {
        Focus();
        listView1.Focus();
    }

    private string[] SplitWords(string text)
    {
        int start = 0;
        List<string> words = new List<string>();
        int i;
        for (i = 0; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]) || i == 0 || char.IsDigit(text[i]))
            {
                if (start < i)
                {
                    words.Add(text.Substring(start, i - start));
                }
                start = i;
            }
            else if (text[i] == '_')
            {
                if (start < i)
                {
                    words.Add(text.Substring(start, i - start));
                }
                for (start = i + 1; start < text.Length && text[start] != '_'; start++)
                {
                }
            }
        }
        if (start < i)
        {
            words.Add(text.Substring(start, i - start));
        }
        return words.ToArray();
    }

    private void ResetFilter(string prefix)
    {
        if (!_isFiltered)
        {
            return;
        }
        _isFiltered = false;
        listView1.Items.Clear();
        foreach (ListViewItem item in _allItems)
        {
            item.ForeColor = listView1.ForeColor;
            listView1.Items.Add(item);
        }
        SelectMatch(prefix, allowSubstrings: false);
    }

    private void AddSubstringMatches(string filterText, List<ListViewItem> matches)
    {
        for (int i = 0; i < listView1.Items.Count; i++)
        {
            if (listView1.Items[i].Text.IndexOf(filterText) != -1 && !matches.Contains(listView1.Items[i]))
            {
                matches.Add(listView1.Items[i]);
            }
        }
        for (int i = 0; i < listView1.Items.Count; i++)
        {
            if (listView1.Items[i].Text.IndexOf(filterText, StringComparison.CurrentCultureIgnoreCase) != -1 && !matches.Contains(listView1.Items[i]))
            {
                matches.Add(listView1.Items[i]);
            }
        }
    }

    private ListViewItem AddAbbreviationMatches(string prefix, List<ListViewItem> matches)
    {
        List<ListViewItem> exactAbbrev = new List<ListViewItem>();
        List<ListViewItem> prefixAbbrev = new List<ListViewItem>();
        List<ListViewItem> exactWord = new List<ListViewItem>();
        List<ListViewItem> prefixWord = new List<ListViewItem>();
        for (int i = 0; i < listView1.Items.Count; i++)
        {
            string[] words = SplitWords(listView1.Items[i].Text);
            string abbrev = "";
            foreach (string word in words)
            {
                abbrev += char.ToUpper(word[0]);
            }
            foreach (string word in words)
            {
                if (StringComparer.CurrentCultureIgnoreCase.Compare(word, prefix) == 0)
                {
                    exactWord.Add(listView1.Items[i]);
                }
                if (word.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    prefixWord.Add(listView1.Items[i]);
                }
            }
            if (abbrev == prefix)
            {
                exactAbbrev.Add(listView1.Items[i]);
            }
            if (abbrev.StartsWith(prefix))
            {
                prefixAbbrev.Add(listView1.Items[i]);
            }
        }
        ListViewItem bestMatch = null;
        if (exactAbbrev.Count > 0)
        {
            bestMatch = exactAbbrev[0];
        }
        else if (exactWord.Count > 0)
        {
            bestMatch = exactWord[0];
        }
        foreach (ListViewItem item in exactAbbrev)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        foreach (ListViewItem item in prefixAbbrev)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        foreach (ListViewItem item in exactWord)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        foreach (ListViewItem item in prefixWord)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        return bestMatch;
    }

    internal void SelectMatch(string prefix, bool allowSubstrings)
    {
        int nearestIdx = 0;
        for (int i = 0; i < listView1.Items.Count; i++)
        {
            if (Comparer.Compare(listView1.Items[i].Text, prefix) == 0)
            {
                SelectIndex(i);
                ResetFilter(prefix);
                return;
            }
        }
        for (int i = 0; i < listView1.Items.Count; i++)
        {
            string itemText = listView1.Items[i].Text;
            if (Comparer.Compare(itemText, prefix) < 0)
            {
                nearestIdx = i;
            }
            if (itemText.Length >= prefix.Length && Comparer.Compare(itemText.Substring(0, prefix.Length), prefix) == 0)
            {
                SelectIndex(i);
                ResetFilter(prefix);
                return;
            }
        }
        List<ListViewItem> matches = new List<ListViewItem>();
        ListViewItem bestMatch = null;
        if (allowSubstrings)
        {
            bestMatch = AddAbbreviationMatches(prefix, matches);
            AddSubstringMatches(prefix, matches);
        }
        int topCount = 0;
        if (bestMatch != null)
        {
            matches.Remove(bestMatch);
            matches.Insert(0, bestMatch);
        }
        if (matches.Count != 0)
        {
            foreach (ListViewItem item in matches)
            {
                item.Remove();
            }
            foreach (ListViewItem item in matches)
            {
                listView1.Items.Insert(topCount++, item);
            }
            SelectIndex(0);
            for (int i = 0; i < listView1.Items.Count; i++)
            {
                listView1.Items[i].ForeColor = ((i < topCount) ? Color.Black : Color.DarkGray);
            }
            this._isFiltered = true;
        }
        else
        {
            ResetFilter(prefix);
            SelectIndex(nearestIdx);
        }
    }

    private void SuggestionListForm_Deactivate(object sender, EventArgs e)
    {
        if (!_suppressDeactivate)
        {
            Hide();
        }
        NotifyHost(active: false);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        NotifyHost(Visible);
    }

    private void listView1_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (!_showHints || listView1.SelectedIndices.Count <= 0)
        {
            return;
        }
        _suppressDeactivate = true;
        string hintText = (listView1.Items[listView1.SelectedIndices[0]].Tag as ISuggestion).HintText;
        if (hintText != "")
        {
            string hintDesc = (listView1.Items[listView1.SelectedIndices[0]].Tag as ISuggestion).HintDescription;
            if (hintDesc == null)
            {
                _hintForm.ShowDescription = false;
            }
            else
            {
                _hintForm.Description = hintDesc;
            }
        }
        _hintForm.SignatureText = hintText;
        if (hintText == "")
        {
            if (_hintForm.Visible)
            {
                _hintForm.Hide();
            }
        }
        else
        {
            _hintForm.ShowBeside(this, listView1.Items[listView1.SelectedIndices[0]].Position.Y, isVertical: true);
            Activate();
            FocusList();
        }
        _suppressDeactivate = false;
    }

    private void SuggestionListForm_VisibleChanged(object sender, EventArgs e)
    {
        if (!Visible && _hintForm.Visible)
        {
            _hintForm.Hide();
        }
    }

    public void SetSize(int contentWidth, int detailWidth, int maxHeight)
    {
        if (_imageList.Images.Count > 0)
        {
            contentWidth += _imageList.Images[0].Width + 5;
        }
        listView1.Columns[0].Width = contentWidth + 10;
        Width = contentWidth + 35;
        if (detailWidth == 0)
        {
            listView1.Columns[1].Width = 0;
        }
        int totalHeight = Font.Height * listView1.Items.Count;
        if (totalHeight > maxHeight)
        {
            totalHeight = maxHeight;
        }
        Height = totalHeight + 20;
    }

    /// <summary>The item that is selected in the list, or null.</summary>
    internal ISuggestion SelectedSuggestion => (listView1.SelectedItems.Count > 0) ? listView1.SelectedItems[0].Tag as ISuggestion : null;

    public void AcceptSelected()
    {
        listView1.AcceptSelected();
    }

    private void SuggestionListForm_Activated(object sender, EventArgs e)
    {
        NotifyHost(active: true);
    }

    private void SuggestionListForm_FormClosed(object sender, FormClosedEventArgs e)
    {
        NotifyHost(active: false);
    }

    internal void NotifyHost(bool active)
    {
        if (active != _hostNotified)
        {
            listView1.Host.SetPopupActive(active);
            _hostNotified = active;
        }
    }
}
