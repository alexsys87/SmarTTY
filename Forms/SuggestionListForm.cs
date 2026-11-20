using System.ComponentModel;
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    internal void SelectMatch(string prefix, bool allowSubstrings)
    {
        List<string> names = new List<string>();
        for (int i = 0; i < listView1.Items.Count; i++)
        {
            names.Add(listView1.Items[i].Text);
        }
        SuggestionMatch match = SuggestionListMatch.Find(names, prefix, allowSubstrings, Comparer);
        if (match.Kind == SuggestionMatchKind.Item)
        {
            SelectIndex(match.Index);
            ResetFilter(prefix);
            return;
        }
        if (match.Kind == SuggestionMatchKind.Filtered)
        {
            List<ListViewItem> matches = new List<ListViewItem>();
            foreach (int index in match.Matches)
            {
                matches.Add(listView1.Items[index]);
            }
            int topCount = 0;
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
            SelectIndex(match.Index);
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
