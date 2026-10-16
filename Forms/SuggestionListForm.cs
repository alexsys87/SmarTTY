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

    public SuggestionListForm(ICompletionHost completionHost, bool flag)
    {
        InitializeComponent();
        listView1.Sorting = (flag ? SortOrder.Ascending : SortOrder.None);
        listView1.Host = completionHost;
        if (!IsWindows())
        {
            listView1.StateImageList = null;
            listView1.SmallImageList = _imageList;
        }
    }

    internal void SetSource(ISuggestionSource suggestionSource)
    {
        _imageList.Images.Clear();
        foreach (Image item in suggestionSource.Images)
        {
            _imageList.Images.Add(item);
        }
        _allItems = new List<ListViewItem>();
        foreach (ISuggestion item2 in suggestionSource.Items)
        {
            ListViewItem listViewItem = new ListViewItem(item2.DisplayName);
            if (item2.IsBold)
            {
                listViewItem.Font = new Font(listViewItem.Font, FontStyle.Bold);
            }
            listViewItem.ImageIndex = item2.ImageIndex;
            listViewItem.StateImageIndex = item2.ImageIndex;
            listViewItem.SubItems.Add(item2.DetailText);
            listViewItem.Tag = item2;
            listView1.Items.Add(listViewItem);
            _allItems.Add(listViewItem);
        }
        listView1.Sort();
    }

    private void SelectIndex(int value2)
    {
        listView1.SelectedIndices.Clear();
        if (value2 < listView1.Items.Count)
        {
            ListViewItem listViewItem = listView1.Items[value2];
            bool selected = (listView1.Items[value2].Focused = true);
            listViewItem.Selected = selected;
            listView1.Items[value2].EnsureVisible();
        }
    }

    public void FocusList()
    {
        Focus();
        listView1.Focus();
    }

    private string[] SplitWords(string text)
    {
        int num = 0;
        List<string> list = new List<string>();
        int num2;
        for (num2 = 0; num2 < text.Length; num2++)
        {
            if (char.IsUpper(text[num2]) || num2 == 0 || char.IsDigit(text[num2]))
            {
                if (num < num2)
                {
                    list.Add(text.Substring(num, num2 - num));
                }
                num = num2;
            }
            else if (text[num2] == '_')
            {
                if (num < num2)
                {
                    list.Add(text.Substring(num, num2 - num));
                }
                for (num = num2 + 1; num < text.Length && text[num] != '_'; num++)
                {
                }
            }
        }
        if (num < num2)
        {
            list.Add(text.Substring(num, num2 - num));
        }
        return list.ToArray();
    }

    private void ResetFilter(string text)
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
        SelectMatch(text, flag: false);
    }

    private void AddSubstringMatches(string text, List<ListViewItem> list)
    {
        for (int num = 0; num < listView1.Items.Count; num++)
        {
            if (listView1.Items[num].Text.IndexOf(text) != -1 && !list.Contains(listView1.Items[num]))
            {
                list.Add(listView1.Items[num]);
            }
        }
        for (int num2 = 0; num2 < listView1.Items.Count; num2++)
        {
            if (listView1.Items[num2].Text.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) != -1 && !list.Contains(listView1.Items[num2]))
            {
                list.Add(listView1.Items[num2]);
            }
        }
    }

    private ListViewItem AddAbbreviationMatches(string text4, List<ListViewItem> list5)
    {
        List<ListViewItem> list = new List<ListViewItem>();
        List<ListViewItem> list2 = new List<ListViewItem>();
        List<ListViewItem> list3 = new List<ListViewItem>();
        List<ListViewItem> list4 = new List<ListViewItem>();
        for (int num = 0; num < listView1.Items.Count; num++)
        {
            string[] array = SplitWords(listView1.Items[num].Text);
            string text = "";
            string[] array2 = array;
            foreach (string text2 in array2)
            {
                text += char.ToUpper(text2[0]);
            }
            array2 = array;
            foreach (string text3 in array2)
            {
                if (StringComparer.CurrentCultureIgnoreCase.Compare(text3, text4) == 0)
                {
                    list3.Add(listView1.Items[num]);
                }
                if (text3.StartsWith(text4, StringComparison.CurrentCultureIgnoreCase))
                {
                    list4.Add(listView1.Items[num]);
                }
            }
            if (text == text4)
            {
                list.Add(listView1.Items[num]);
            }
            if (text.StartsWith(text4))
            {
                list2.Add(listView1.Items[num]);
            }
        }
        ListViewItem result = null;
        if (list.Count > 0)
        {
            result = list[0];
        }
        else if (list3.Count > 0)
        {
            result = list3[0];
        }
        foreach (ListViewItem item in list)
        {
            if (!list5.Contains(item))
            {
                list5.Add(item);
            }
        }
        foreach (ListViewItem item2 in list2)
        {
            if (!list5.Contains(item2))
            {
                list5.Add(item2);
            }
        }
        foreach (ListViewItem item3 in list3)
        {
            if (!list5.Contains(item3))
            {
                list5.Add(item3);
            }
        }
        foreach (ListViewItem item4 in list4)
        {
            if (!list5.Contains(item4))
            {
                list5.Add(item4);
            }
        }
        return result;
    }

    internal void SelectMatch(string text2, bool flag)
    {
        int num = 0;
        for (int num2 = 0; num2 < listView1.Items.Count; num2++)
        {
            if (Comparer.Compare(listView1.Items[num2].Text, text2) == 0)
            {
                SelectIndex(num2);
                ResetFilter(text2);
                return;
            }
        }
        for (int num3 = 0; num3 < listView1.Items.Count; num3++)
        {
            string text = listView1.Items[num3].Text;
            if (Comparer.Compare(text, text2) < 0)
            {
                num = num3;
            }
            if (text.Length >= text2.Length && Comparer.Compare(text.Substring(0, text2.Length), text2) == 0)
            {
                SelectIndex(num3);
                ResetFilter(text2);
                return;
            }
        }
        List<ListViewItem> list = new List<ListViewItem>();
        ListViewItem listViewItem = null;
        if (flag)
        {
            listViewItem = AddAbbreviationMatches(text2, list);
            AddSubstringMatches(text2, list);
        }
        int num4 = 0;
        if (listViewItem != null)
        {
            list.Remove(listViewItem);
            list.Insert(0, listViewItem);
        }
        if (list.Count != 0)
        {
            foreach (ListViewItem item in list)
            {
                item.Remove();
            }
            foreach (ListViewItem item2 in list)
            {
                listView1.Items.Insert(num4++, item2);
            }
            SelectIndex(0);
            for (int num5 = 0; num5 < listView1.Items.Count; num5++)
            {
                listView1.Items[num5].ForeColor = ((num5 < num4) ? Color.Black : Color.DarkGray);
            }
            this._isFiltered = true;
        }
        else
        {
            ResetFilter(text2);
            SelectIndex(num);
        }
    }

    private void SuggestionListForm_Deactivate(object sender, EventArgs e)
    {
        if (!_suppressDeactivate)
        {
            Hide();
        }
        NotifyHost(flag: false);
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
        string text = (listView1.Items[listView1.SelectedIndices[0]].Tag as ISuggestion).HintText;
        if (text != "")
        {
            string text2 = (listView1.Items[listView1.SelectedIndices[0]].Tag as ISuggestion).HintDescription;
            if (text2 == null)
            {
                _hintForm.ShowDescription = false;
            }
            else
            {
                _hintForm.Description = text2;
            }
        }
        _hintForm.SignatureText = text;
        if (text == "")
        {
            if (_hintForm.Visible)
            {
                _hintForm.Hide();
            }
        }
        else
        {
            _hintForm.ShowBeside(this, listView1.Items[listView1.SelectedIndices[0]].Position.Y, flag: true);
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

    public void SetSize(int value2, int value3, int value4)
    {
        if (_imageList.Images.Count > 0)
        {
            value2 += _imageList.Images[0].Width + 5;
        }
        listView1.Columns[0].Width = value2 + 10;
        Width = value2 + 35;
        if (value3 == 0)
        {
            listView1.Columns[1].Width = 0;
        }
        int num = Font.Height * listView1.Items.Count;
        if (num > value4)
        {
            num = value4;
        }
        Height = num + 20;
    }

    public void AcceptSelected()
    {
        listView1.AcceptSelected();
    }

    private void SuggestionListForm_Activated(object sender, EventArgs e)
    {
        NotifyHost(flag: true);
    }

    private void SuggestionListForm_FormClosed(object sender, FormClosedEventArgs e)
    {
        NotifyHost(flag: false);
    }

    internal void NotifyHost(bool flag)
    {
        if (flag != _hostNotified)
        {
            listView1.Host.SetPopupActive(flag);
            _hostNotified = flag;
        }
    }
}
