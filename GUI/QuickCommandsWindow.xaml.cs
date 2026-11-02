using SmarTTY.GUI.Backend;
using SmarTTY.IO;
using SmarTTY.Terminal;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > Quick commands: the list of the commands and shell scripts that the menu of the terminal offers (see
/// <see cref="QuickCommand"/>), with an editor for the selected one. The list is saved to QuickCommands.xml next to Settings.xml
/// when the window is closed with Save; the settings export and import carry it as its own part.
/// </summary>
public partial class QuickCommandsWindow : Window
{
    /// <summary>An item of the list as the window edits it (a copy: Cancel leaves the saved list alone).</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public QuickCommand Item { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public Row(QuickCommand item)
        {
            Item = item;
        }

        public string Name
        {
            get => Item.Name;
            set
            {
                Item.Name = value;
                Changed();
            }
        }

        public string Text
        {
            get => Item.Text;
            set
            {
                Item.Text = value;
                Changed();
            }
        }

        public string FileName
        {
            get => Item.FileName;
            set
            {
                Item.FileName = value;
                Changed();
            }
        }

        public bool Enabled
        {
            get => Item.Enabled;
            set
            {
                Item.Enabled = value;
                Changed();
            }
        }

        public bool IsCommand
        {
            get => Item.Kind == QuickCommandKind.Command;
            set
            {
                if (value)
                {
                    SetKind(QuickCommandKind.Command);
                }
            }
        }

        public bool IsScript
        {
            get => Item.Kind == QuickCommandKind.Script;
            set
            {
                if (value)
                {
                    SetKind(QuickCommandKind.Script);
                }
            }
        }

        private void SetKind(QuickCommandKind kind)
        {
            if (Item.Kind == kind)
            {
                return;
            }
            Item.Kind = kind;
            if (kind == QuickCommandKind.Script && string.IsNullOrWhiteSpace(Item.FileName))
            {
                Item.FileName = "script.sh";
            }
            if (kind == QuickCommandKind.Script && !string.IsNullOrWhiteSpace(Item.Text) && !Item.Text.StartsWith("#!", StringComparison.Ordinal))
            {
                // A command turned into a script: give it a shebang, so that it runs with sh whatever the login shell is.
                Item.Text = "#!/bin/sh\n" + Item.Text;
            }
            Changed();
        }

        public string MenuText => Item.MenuText;

        public string KindText => Item.Kind == QuickCommandKind.Script ? "Script" : "Command";

        public string Summary => Item.Kind == QuickCommandKind.Script
            ? Item.FileName + ": " + string.Join(" | ", (Item.Text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("#!", StringComparison.Ordinal)).Take(3))
            : QuickCommandRules.ToCommandLine(Item.Text);

        public string Problem => QuickCommandRules.Validate(Item);

        public bool HasProblem => Problem != null;

        private void Changed()
        {
            foreach (string property in new[] { nameof(Name), nameof(Text), nameof(FileName), nameof(Enabled), nameof(IsCommand), nameof(IsScript), nameof(MenuText), nameof(KindText), nameof(Summary), nameof(Problem), nameof(HasProblem) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }
    }

    private readonly ObservableCollection<Row> _Rows = new ObservableCollection<Row>();

    private static string _LastFolder;

    public QuickCommandsWindow()
    {
        InitializeComponent();
        ItemList.ItemsSource = _Rows;
        FileText.Text = Path.Combine(GlobalSettings.DataDirectory, QuickCommandList.FileName);
        QuickCommandList list;
        try
        {
            list = QuickCommandStore.Load();
        }
        catch (Exception exception)
        {
            MessageBoxes.Warning("The saved quick commands cannot be read (" + exception.Message + "). Saving replaces them with this list.");
            list = new QuickCommandList();
        }
        foreach (QuickCommand item in list.Items)
        {
            _Rows.Add(new Row(item.Clone()));
        }
        if (_Rows.Count > 0)
        {
            ItemList.SelectedIndex = 0;
        }
        UpdateButtons();
    }

    private Row Selected => ItemList.SelectedItem as Row;

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditorBox.DataContext = Selected;
        EditorBox.IsEnabled = Selected != null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int index = ItemList.SelectedIndex;
        DuplicateButton.IsEnabled = index >= 0;
        DeleteButton.IsEnabled = index >= 0;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index < _Rows.Count - 1;
    }

    private void Add(QuickCommand item, Control focus)
    {
        Row row = new Row(item);
        _Rows.Add(row);
        ItemList.SelectedItem = row;
        ItemList.ScrollIntoView(row);
        Dispatcher.BeginInvoke((Action)(() =>
        {
            focus.Focus();
            (focus as TextBox)?.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        Add(new QuickCommand { Kind = QuickCommandKind.Command, Text = "" }, CommandBox);
    }

    private void AddScript_Click(object sender, RoutedEventArgs e)
    {
        Add(new QuickCommand { Kind = QuickCommandKind.Script, FileName = "script.sh", Text = "#!/bin/sh\n\n" }, FileNameBox);
    }

    private void ImportScript_Click(object sender, RoutedEventArgs e)
    {
        string text = PickScript(out string fileName);
        if (text != null)
        {
            Add(new QuickCommand { Kind = QuickCommandKind.Script, Name = Path.GetFileNameWithoutExtension(fileName), FileName = fileName, Text = text }, NameBox);
        }
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row == null)
        {
            return;
        }
        string text = PickScript(out string fileName);
        if (text != null)
        {
            row.Text = text;
            if (string.IsNullOrWhiteSpace(row.FileName) || row.FileName == "script.sh")
            {
                row.FileName = fileName;
            }
        }
    }

    /// <summary>Asks for a script file of this computer and returns its text with Unix line ends; null when cancelled or unusable.</summary>
    private string PickScript(out string fileName)
    {
        fileName = null;
        string path = LocalFileDialogWindow.OpenFile(this, "Choose a shell script", "Shell scripts (*.sh;*.ash;*.bash)|*.sh;*.ash;*.bash|All files (*.*)|*.*",
            initialDirectory: Directory.Exists(_LastFolder) ? _LastFolder : null);
        if (path == null)
        {
            return null;
        }
        _LastFolder = Path.GetDirectoryName(path);
        try
        {
            byte[] content = File.ReadAllBytes(path);
            string problem = RemoteScript.Validate(content);
            if (problem != null)
            {
                MessageBoxes.Error(Path.GetFileName(path) + ": " + problem);
                return null;
            }
            fileName = Path.GetFileName(path);
            return new UTF8Encoding(false).GetString(RemoteScript.Normalize(content));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            MessageBoxes.Error("Cannot read " + path + ": " + exception.Message);
            return null;
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row != null)
        {
            QuickCommand copy = row.Item.Clone();
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? copy.Name : copy.Name + " (copy)";
            Add(copy, NameBox);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Row row = Selected;
        if (row == null)
        {
            return;
        }
        int index = _Rows.IndexOf(row);
        _Rows.Remove(row);
        if (_Rows.Count > 0)
        {
            ItemList.SelectedIndex = Math.Min(index, _Rows.Count - 1);
        }
        UpdateButtons();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        Move(-1);
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        Move(1);
    }

    private void Move(int step)
    {
        int index = ItemList.SelectedIndex;
        int target = index + step;
        if (index < 0 || target < 0 || target >= _Rows.Count)
        {
            return;
        }
        _Rows.Move(index, target);
        ItemList.SelectedIndex = target;
        UpdateButtons();
    }

    private void ItemList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected != null)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Row invalid = _Rows.FirstOrDefault(row => row.HasProblem);
        if (invalid != null)
        {
            ItemList.SelectedItem = invalid;
            ItemList.ScrollIntoView(invalid);
            MessageBoxes.Error((string.IsNullOrWhiteSpace(invalid.MenuText) ? "An item" : "\"" + invalid.MenuText + "\"") + ": " + invalid.Problem);
            return;
        }
        QuickCommandList list = new QuickCommandList();
        foreach (Row row in _Rows)
        {
            QuickCommand item = row.Item.Clone();
            item.Name = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name.Trim();
            if (item.Kind == QuickCommandKind.Command)
            {
                item.Text = QuickCommandRules.ToCommandLine(item.Text);
                item.FileName = null;
            }
            else
            {
                item.Text = (item.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            }
            list.Items.Add(item);
        }
        try
        {
            QuickCommandStore.Save(list);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            MessageBoxes.Error("Cannot save the quick commands: " + exception.Message);
            return;
        }
        DialogResult = true;
    }
}
