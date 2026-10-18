using SmarTTY.Completion;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

internal partial class SuggestionListView : ListView
{
    public ICompletionHost Host;

    public SuggestionListView()
    {
        InitializeComponent();
    }

    public override bool PreProcessMessage(ref Message msg)
    {
        if (Host != null)
        {
            if (msg.Msg == 256 || msg.Msg == 260)
            {
                Keys keys = (Keys)(long)msg.WParam;
                if (HandleKey(keys))
                {
                    return true;
                }
                if (keys == Keys.Left || keys == Keys.Right || keys == Keys.Home || keys == Keys.End || keys == Keys.Delete || keys == Keys.Tab)
                {
                    Host.ForwardMessage(msg);
                    switch (keys)
                    {
                        case Keys.Home:
                            if (Items.Count > 0)
                            {
                                SelectedItems.Clear();
                                Items[0].Selected = true;
                                FocusedItem = Items[0];
                                FocusedItem.EnsureVisible();
                            }
                            break;
                        case Keys.End:
                            if (Items.Count > 0)
                            {
                                SelectedItems.Clear();
                                Items[Items.Count - 1].Selected = true;
                                FocusedItem = Items[Items.Count - 1];
                                FocusedItem.EnsureVisible();
                            }
                            break;
                    }
                    return true;
                }
            }
            if (msg.Msg == 258)
            {
                Host.ForwardMessage(msg);
                return true;
            }
        }
        return base.PreProcessMessage(ref msg);
    }

    private bool HandleKey(Keys keys)
    {
        switch (keys)
        {
            case Keys.Menu:
            case Keys.Escape:
                Parent.Hide();
                return true;
            case Keys.Tab:
            case Keys.Return:
            case Keys.Space:
                if (Host != null)
                {
                    ISuggestion suggestion = (ISuggestion)(FocusedItem?.Tag);
                    if (suggestion == null)
                    {
                        return false;
                    }
                    Parent.Hide();
                    if (keys == Keys.Space && (Host.CurrentText.Length > suggestion.DisplayName.Length || suggestion.DisplayName.Substring(0, Host.CurrentText.Length) != Host.CurrentText))
                    {
                        Host.ForwardMessage(Message.Create(Host.HostControl.Handle, 258, (IntPtr)32, IntPtr.Zero));
                        return true;
                    }
                    Host.AcceptSuggestion(suggestion, keys);
                }
                return true;
            default:
                return false;
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        this.AcceptSelected();
        base.OnDoubleClick(e);
    }

    public void AcceptSelected()
    {
        if (Host != null && SelectedItems.Count > 0)
        {
            ISuggestion suggestion = (ISuggestion)SelectedItems[0].Tag;
            Parent.Hide();
            Host.AcceptSuggestion(suggestion, Keys.None);
        }
    }
}
