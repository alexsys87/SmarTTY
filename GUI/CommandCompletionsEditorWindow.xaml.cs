using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Editor of CommandCompletions.xml, the dictionary of the options that the Smart Terminal suggests for a command.
/// It edits <see cref="CompletionEditModel"/>, a copy of the database, and writes it back with
/// <see cref="CommandCompletionDatabase.SaveTo"/> (plain XML or gzip).
/// </summary>
public partial class CommandCompletionsEditorWindow : Window
{
    private const string Caption = "Command completions";

    // The file in use, else the place where the first save will put it (next to the exe, or %APPDATA%\SmarTTY when that is protected).
    private string _path = CommandCompletionDatabase.PlannedPath();

    private CompletionEditModel _model = new CompletionEditModel();

    private bool _dirty;

    private bool _loading;

    private bool _ready;

    private CommandCompletionsEditorWindow(Window owner)
    {
        InitializeComponent();
        _ready = LoadFromDisk(owner);
    }

    /// <summary>Opens the editor as a dialog of <paramref name="owner"/>.</summary>
    public static void Open(Window owner)
    {
        CommandCompletionsEditorWindow window = new CommandCompletionsEditorWindow(owner);
        if (window._ready)
        {
            window.Owner = owner;
            window.ShowDialog();
        }
    }

    // ------------------------------------------------------------------ loading

    private bool LoadFromDisk(Window owner)
    {
        CommandCompletionDatabase database = CommandCompletionDatabase.Load();
        if (database.LoadWarning != null)
        {
            // Load() answers a damaged file with an empty database: saving that would destroy what the user may still repair by hand.
            MessageBoxResult answer = MessageDialog.Show(database.LoadWarning + "\n\nOpen an empty list instead? Saving it replaces the damaged file.",
                Caption, MessageDialogIcon.Warning, MessageDialogButtons.YesNo, MessageBoxResult.No, owner ?? this);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        _loading = true;
        try
        {
            _model.Entries.CollectionChanged -= Entries_CollectionChanged;
            _model = CompletionEditModel.FromDatabase(database);
            foreach (CommandEditItem entry in _model.Entries)
            {
                Hook(entry);
            }
            _model.Entries.CollectionChanged += Entries_CollectionChanged;

            CommandList.ItemsSource = _model.Entries;
            ICollectionView view = CollectionViewSource.GetDefaultView(_model.Entries);
            view.Filter = Filter;
            SearchBox.Text = "";
            CompressBox.IsChecked = CommandCompletionDatabase.IsStoredCompressed(_path);
            PathText.Text = _path + (CompressBox.IsChecked == true ? ".gz" : "");
            if (_model.Entries.Count > 0)
            {
                CommandList.SelectedIndex = 0;
            }
        }
        finally
        {
            _loading = false;
        }
        SetDirty(false);
        UpdateState();
        StatusText.Text = _model.Entries.Count + " command(s) loaded.";
        return true;
    }

    // ------------------------------------------------------------------ change tracking

    private void Hook(CommandEditItem entry)
    {
        entry.PropertyChanged += Item_PropertyChanged;
        entry.Options.CollectionChanged += Options_CollectionChanged;
        foreach (OptionEditItem option in entry.Options)
        {
            option.PropertyChanged += Item_PropertyChanged;
        }
    }

    private void Entries_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (CommandEditItem entry in e.NewItems)
            {
                Hook(entry);
            }
        }
        MarkDirty();
    }

    private void Options_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // A move reports the moved item as new again; it is hooked already.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (OptionEditItem option in e.NewItems)
            {
                option.PropertyChanged += Item_PropertyChanged;
            }
        }
        MarkDirty();
    }

    private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        UpdateTest();
    }

    private void MarkDirty()
    {
        if (!_loading)
        {
            SetDirty(true);
        }
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        Title = dirty ? Caption + " *" : Caption;
    }

    // ------------------------------------------------------------------ list of commands

    private bool Filter(object item)
    {
        string text = SearchBox.Text.Trim();
        CommandEditItem entry = item as CommandEditItem;
        if (text.Length == 0 || entry == null)
        {
            return true;
        }
        return Contains(entry.Command, text) || Contains(entry.Description, text) || Contains(entry.CommandLineRegex, text) ||
               entry.Options.Any(option => Contains(option.Value, text) || Contains(option.Aliases, text) || Contains(option.ShortDescription, text));
    }

    private static bool Contains(string source, string text)
    {
        return source != null && source.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CommandList.ItemsSource != null)
        {
            CollectionViewSource.GetDefaultView(_model.Entries).Refresh();
        }
    }

    private CommandEditItem SelectedCommand
    {
        get { return CommandList.SelectedItem as CommandEditItem; }
    }

    private OptionEditItem SelectedOption
    {
        get { return OptionList.SelectedItem as OptionEditItem; }
    }

    private void CommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TestBox.Text = "";
        UpdateState();
    }

    private void OptionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        bool hasCommand = SelectedCommand != null;
        bool hasOption = SelectedOption != null;
        DetailsPanel.IsEnabled = hasCommand;
        DuplicateCommandButton.IsEnabled = hasCommand;
        DeleteCommandButton.IsEnabled = hasCommand;
        OptionPanel.IsEnabled = hasOption;
        DuplicateOptionButton.IsEnabled = hasOption;
        DeleteOptionButton.IsEnabled = hasOption;
        int index = hasCommand ? OptionList.SelectedIndex : -1;
        OptionUpButton.IsEnabled = hasOption && index > 0;
        OptionDownButton.IsEnabled = hasOption && hasCommand && index < SelectedCommand.Options.Count - 1;
        UpdateTest();
    }

    private void NewCommand_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        CommandEditItem entry = new CommandEditItem();
        _model.Entries.Add(entry);
        CommandList.SelectedItem = entry;
        CommandList.ScrollIntoView(entry);
        CommandBox.Focus();
    }

    private void DuplicateCommand_Click(object sender, RoutedEventArgs e)
    {
        CommandEditItem source = SelectedCommand;
        if (source == null)
        {
            return;
        }
        SearchBox.Text = "";
        CommandEditItem copy = source.Clone();
        _model.Entries.Insert(_model.Entries.IndexOf(source) + 1, copy);
        CommandList.SelectedItem = copy;
        CommandList.ScrollIntoView(copy);
        CommandBox.Focus();
        CommandBox.SelectAll();
    }

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        CommandEditItem entry = SelectedCommand;
        if (entry == null)
        {
            return;
        }
        string text = "Delete the command '" + entry.DisplayName + "'" + (entry.Options.Count > 0 ? " with its " + entry.Options.Count + " option(s)" : "") + "?";
        if (MessageDialog.Show(text, Caption, MessageDialogIcon.Question, MessageDialogButtons.YesNo, MessageBoxResult.No, this) != MessageBoxResult.Yes)
        {
            return;
        }
        int index = _model.Entries.IndexOf(entry);
        _model.Entries.Remove(entry);
        if (_model.Entries.Count > 0)
        {
            CommandList.SelectedIndex = Math.Min(index, _model.Entries.Count - 1);
        }
        UpdateState();
    }

    // ------------------------------------------------------------------ options of the command

    private void NewOption_Click(object sender, RoutedEventArgs e)
    {
        CommandEditItem entry = SelectedCommand;
        if (entry == null)
        {
            return;
        }
        OptionEditItem option = new OptionEditItem();
        entry.Options.Add(option);
        OptionList.SelectedItem = option;
        OptionList.ScrollIntoView(option);
        OptionValueBox.Focus();
    }

    private void DuplicateOption_Click(object sender, RoutedEventArgs e)
    {
        CommandEditItem entry = SelectedCommand;
        OptionEditItem source = SelectedOption;
        if (entry == null || source == null)
        {
            return;
        }
        OptionEditItem copy = source.Clone();
        entry.Options.Insert(entry.Options.IndexOf(source) + 1, copy);
        OptionList.SelectedItem = copy;
        OptionValueBox.Focus();
        OptionValueBox.SelectAll();
    }

    private void DeleteOption_Click(object sender, RoutedEventArgs e)
    {
        CommandEditItem entry = SelectedCommand;
        OptionEditItem option = SelectedOption;
        if (entry == null || option == null)
        {
            return;
        }
        int index = entry.Options.IndexOf(option);
        entry.Options.Remove(option);
        if (entry.Options.Count > 0)
        {
            OptionList.SelectedIndex = Math.Min(index, entry.Options.Count - 1);
        }
        UpdateState();
    }

    private void OptionUp_Click(object sender, RoutedEventArgs e)
    {
        MoveOption(-1);
    }

    private void OptionDown_Click(object sender, RoutedEventArgs e)
    {
        MoveOption(1);
    }

    private void MoveOption(int direction)
    {
        CommandEditItem entry = SelectedCommand;
        OptionEditItem option = SelectedOption;
        if (entry == null || option == null)
        {
            return;
        }
        int index = entry.Options.IndexOf(option);
        int target = index + direction;
        if (target < 0 || target >= entry.Options.Count)
        {
            return;
        }
        entry.Options.Move(index, target);
        OptionList.SelectedItem = option;
        UpdateState();
    }

    // ------------------------------------------------------------------ test of the command line

    private void TestBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTest();
    }

    private void UpdateTest()
    {
        CommandEditItem entry = SelectedCommand;
        string line = TestBox.Text;
        if (entry == null || line.Length == 0)
        {
            TestResult.Text = "";
            return;
        }
        TestResult.Text = entry.Matches(line) ? "matches: this entry is used" : "no match";
    }

    // ------------------------------------------------------------------ saving

    private void Compress_Changed(object sender, RoutedEventArgs e)
    {
        PathText.Text = _path + (CompressBox.IsChecked == true ? ".gz" : "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Save();
    }

    private bool Save()
    {
        List<string> warnings = new List<string>();
        List<string> errors = _model.Validate(warnings);
        if (errors.Count > 0)
        {
            string text = "Fix these problems before saving:\n\n" + string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? "\n... and " + (errors.Count - 12) + " more" : "");
            MessageDialog.Show(text, Caption, MessageDialogIcon.Warning, MessageDialogButtons.Ok, owner: this);
            return false;
        }

        bool compressed = CompressBox.IsChecked == true;
        try
        {
            _path = _model.ToDatabase().SaveToPreferredPlace(compressed);
            PathText.Text = _path + (compressed ? ".gz" : "");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
        {
            MessageDialog.Show("Could not save the command completions to:\n" + _path + (compressed ? ".gz" : "") + "\n\n" + ex.Message +
                               "\n\nThe file is saved next to SmarTTY.exe, or in %APPDATA%\\SmarTTY when that folder is protected; neither could be written.",
                Caption, MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: this);
            return false;
        }

        // Sessions that already read the dictionary read it again.
        CommandCompletionDatabase.NotifyChanged();
        SetDirty(false);
        StatusText.Text = "Saved " + _model.Entries.Count + " command(s)." + (warnings.Count > 0 ? " Note: " + warnings[0] + (warnings.Count > 1 ? " (+" + (warnings.Count - 1) + " more)" : "") : "");
        return true;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && MessageDialog.Show("Discard the changes and read the file again?", Caption, MessageDialogIcon.Question, MessageDialogButtons.YesNo, MessageBoxResult.No, this) != MessageBoxResult.Yes)
        {
            return;
        }
        LoadFromDisk(this);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }
        MessageBoxResult answer = MessageDialog.Show("Save the changes to the command completions?", Caption, MessageDialogIcon.Question, MessageDialogButtons.YesNoCancel, MessageBoxResult.Yes, this);
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
        {
            e.Cancel = true;
        }
    }
}
