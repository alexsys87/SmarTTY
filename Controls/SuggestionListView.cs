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
                ISuggestion suggestion = (ISuggestion)(FocusedItem?.Tag);
                switch (SuggestionListMatch.MapKey(keys.ToVirtualKey(), suggestion, () => Host.CurrentText))
                {
                    case SuggestionListKeyAction.Hide:
                        Parent.Hide();
                        return true;
                    case SuggestionListKeyAction.HideAndTypeBlank:
                        Parent.Hide();
                        Host.ForwardMessage(Message.Create(Host.HostControl.Handle, 258, (IntPtr)32, IntPtr.Zero));
                        return true;
                    case SuggestionListKeyAction.Accept:
                        Parent.Hide();
                        Host.AcceptSuggestion(suggestion, keys);
                        return true;
                    case SuggestionListKeyAction.Forward:
                        Host.ForwardMessage(msg);
                        return true;
                    case SuggestionListKeyAction.ForwardAndSelectFirst:
                        Host.ForwardMessage(msg);
                        if (Items.Count > 0)
                        {
                            SelectedItems.Clear();
                            Items[0].Selected = true;
                            FocusedItem = Items[0];
                            FocusedItem.EnsureVisible();
                        }
                        return true;
                    case SuggestionListKeyAction.ForwardAndSelectLast:
                        Host.ForwardMessage(msg);
                        if (Items.Count > 0)
                        {
                            SelectedItems.Clear();
                            Items[Items.Count - 1].Selected = true;
                            FocusedItem = Items[Items.Count - 1];
                            FocusedItem.EnsureVisible();
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
