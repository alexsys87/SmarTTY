using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmarTTY.GUI;

/// <summary>
/// "Command history" of the terminal menu: the commands of the tab, the newest first, with a search. The chosen
/// command is put into the command line of the terminal (it runs only when Enter is pressed there); where the source
/// allows it (Smart Terminal, COM/TCP), commands can be deleted from the history.
/// </summary>
public partial class CommandHistoryWindow : Window
{
    private const string Caption = "Command history";

    private readonly CommandHistorySource _source;

    // Everything the source has, the newest first; the list shows the part that matches the search.
    private List<string> _commands = new List<string>();

    private bool _loaded;

    /// <summary>The command to put into the terminal once the window is closed, or null.</summary>
    private string _chosen;

    private CommandHistoryWindow(CommandHistorySource source)
    {
        InitializeComponent();
        _source = source;
        Title = string.IsNullOrEmpty(source.Title) ? Caption : Caption + " - " + source.Title;
        if (source.Delete == null)
        {
            DeleteButton.Visibility = Visibility.Collapsed;
        }
        if (!string.IsNullOrEmpty(source.Note))
        {
            NoteText.Text = source.Note;
            NoteText.Visibility = Visibility.Visible;
        }
        StatusText.Text = "Reading the history...";
        Loaded += OnLoaded;
    }

    /// <summary>Shows the window for <paramref name="source"/> and puts the chosen command into the terminal.</summary>
    public static void Open(Window owner, CommandHistorySource source)
    {
        if (source == null)
        {
            return;
        }
        CommandHistoryWindow window = new CommandHistoryWindow(source);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        window.ShowDialog();
        // After the window is gone, so that the terminal keeps the focus it is given.
        if (window._chosen != null)
        {
            source.Insert?.Invoke(window._chosen);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        IReadOnlyList<string> loaded;
        try
        {
            // The history of a classic shell tab is read from the server.
            Func<IReadOnlyList<string>> load = _source.Load;
            loaded = load == null ? null : await Task.Run(load);
        }
        catch (Exception exception)
        {
            StatusText.Text = "The history could not be read: " + exception.Message;
            return;
        }
        _commands = CommandHistoryEditing.Distinct(loaded ?? new string[0]);
        _loaded = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_loaded)
        {
            return;
        }
        List<string> shown = CommandHistoryEditing.Filter(_commands, SearchBox.Text);
        CommandList.ItemsSource = shown;
        if (shown.Count > 0)
        {
            CommandList.SelectedIndex = 0;
            CommandList.ScrollIntoView(shown[0]);
        }
        if (_commands.Count == 0)
        {
            StatusText.Text = "The history is empty.";
        }
        else if (shown.Count == _commands.Count)
        {
            StatusText.Text = _commands.Count == 1 ? "1 command" : _commands.Count + " commands";
        }
        else
        {
            StatusText.Text = shown.Count + " of " + _commands.Count + " commands match";
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int selected = CommandList.SelectedItems.Count;
        InsertButton.IsEnabled = selected > 0;
        DeleteButton.IsEnabled = selected > 0 && _source.Delete != null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Down and PageDown go on in the list, from the selected command.
        if ((e.Key == Key.Down || e.Key == Key.PageDown) && CommandList.Items.Count > 0)
        {
            int index = Math.Max(0, CommandList.SelectedIndex);
            if (CommandList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            {
                item.Focus();
            }
            else
            {
                CommandList.Focus();
            }
            e.Handled = true;
        }
    }

    private void CommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double click on a command, not on the scroll bar of the list.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(CommandList, source) is ListBoxItem)
        {
            InsertSelected();
        }
    }

    private void CommandList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        InsertSelected();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelected();
    }

    private void InsertSelected()
    {
        if (!(CommandList.SelectedItem is string command))
        {
            return;
        }
        _chosen = command;
        DialogResult = true;
    }

    private void DeleteSelected()
    {
        Action<IReadOnlyList<string>> delete = _source.Delete;
        List<string> selected = CommandList.SelectedItems.Cast<string>().ToList();
        if (delete == null || selected.Count == 0)
        {
            return;
        }
        string question = selected.Count == 1
            ? "Delete this command from the history?\n\n" + selected[0]
            : "Delete " + selected.Count + " commands from the history?";
        if (!string.IsNullOrEmpty(_source.DeleteWarning))
        {
            question += "\n\n" + _source.DeleteWarning;
        }
        if (MessageDialog.Show(question, Caption, MessageDialogIcon.Question, MessageDialogButtons.YesNo, MessageBoxResult.No, this) != MessageBoxResult.Yes)
        {
            return;
        }
        int index = CommandList.SelectedIndex;
        try
        {
            delete(selected);
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
            return;
        }
        HashSet<string> removed = new HashSet<string>(selected, StringComparer.Ordinal);
        _commands.RemoveAll(removed.Contains);
        ApplyFilter();
        // The selection stays about where it was, so several commands can be deleted one after another.
        if (CommandList.Items.Count > 0)
        {
            CommandList.SelectedIndex = Math.Min(Math.Max(index, 0), CommandList.Items.Count - 1);
            if (CommandList.ItemContainerGenerator.ContainerFromIndex(CommandList.SelectedIndex) is ListBoxItem item)
            {
                item.Focus();
            }
        }
    }
}
