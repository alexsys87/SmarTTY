using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VisualTTY;
using DrawingColor = System.Drawing.Color;

namespace SmarTTY.GUI.HexView;

/// <summary>
/// Tools > WPF hex editor lab: the hex editor drawn with WPF (<see cref="WpfHexEditor"/>, stage 3B-2) on a file of this computer, before it takes the
/// place of the WinForms hex view in the editor tab (3B-3). The keys, the mouse, the scrolling, the tool tip, the colours and the view options can
/// be tried here, and the data inspector (Ctrl+I); the bytes can be changed but are never saved.
/// </summary>
internal sealed class HexLabWindow : Window
{
    // A file larger than this is not read: the lab holds the bytes in memory as the editor does.
    private const long MaxBytes = 64L * 1024 * 1024;

    private readonly WpfHexEditor _editor = new WpfHexEditor();

    private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 3, 6, 3) };

    private readonly CheckBox _readOnly = new CheckBox { Content = "Read-only", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    private string _fileName = "(empty)";

    public HexLabWindow(GlobalSettings settings)
    {
        Title = "WPF hex editor lab";
        Width = 1000;
        Height = 650;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());

        GlobalSettings.EditorColors colors = settings.Editor.GetEffectiveColors(settings.IsLightTheme);
        _editor.SetColors(((DrawingColor)colors.BackColor).ToRgb(), ((DrawingColor)colors.TextColor).ToRgb());
        float zoom = settings.Editor.FontZoom > 0f ? settings.Editor.FontZoom : 1f;
        // The font of the editor tab: Courier New of 12 points, with the zoom of the editor.
        _editor.SetFont("Courier New", 12 * zoom);
        _editor.LoadViewOptions(
            settings.Editor.HexBytesPerRow > 0 ? settings.Editor.HexBytesPerRow : 16,
            settings.Editor.HexGroupSize > 0 ? settings.Editor.HexGroupSize : 1,
            settings.Editor.HexBigEndian,
            settings.Editor.HexColorByValue,
            settings.Editor.HexShowDataInspector);

        Button open = new Button { Content = "Open file...", Padding = new Thickness(8, 2, 8, 2) };
        open.Click += (sender, e) => OpenFile();
        _readOnly.Checked += (sender, e) => _editor.ReadOnly = true;
        _readOnly.Unchecked += (sender, e) => _editor.ReadOnly = false;
        ComboBox bytesPerRow = Choices("Bytes in a row", HexViewChoices.BytesPerRow, _editor.BytesPerRow, value => _editor.BytesPerRow = value);
        ComboBox groupSize = Choices("Group", HexViewChoices.GroupSize, _editor.GroupSize, value => _editor.GroupSize = value);
        CheckBox bigEndian = new CheckBox { Content = "Big endian", IsChecked = _editor.BigEndian, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        bigEndian.Click += (sender, e) => _editor.BigEndian = bigEndian.IsChecked == true;
        CheckBox colorByValue = new CheckBox { Content = "Colours by kind", IsChecked = _editor.ColorByValue, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        colorByValue.Click += (sender, e) => _editor.ColorByValue = colorByValue.IsChecked == true;

        StackPanel tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        tools.Children.Add(open);
        tools.Children.Add(_readOnly);
        tools.Children.Add(new TextBlock { Text = "Bytes in a row:", Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(bytesPerRow);
        tools.Children.Add(new TextBlock { Text = "Group:", Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(groupSize);
        tools.Children.Add(bigEndian);
        tools.Children.Add(colorByValue);

        DockPanel panel = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        panel.Children.Add(tools);
        panel.Children.Add(_status);
        panel.Children.Add(_editor);
        Content = panel;

        _editor.StateChanged += (sender, e) => UpdateStatus();
        _editor.SaveRequested += (sender, e) => Note("The lab does not save: the bytes stay in memory.");
        _editor.FindRequested += (sender, e) => Note("Ctrl+F: the search window is the editor tab's (3B-3); F3 repeats a search.");
        _editor.GoToOffsetRequested += (sender, e) => Note("Ctrl+G: the window to go to an offset is the editor tab's (3B-3).");
        _editor.SearchFinished += (sender, e) => Note(e.Found ? "Found at 0x" + e.Offset.ToString("X8", CultureInfo.InvariantCulture) : "Not found");
        Loaded += (sender, e) => _editor.Focus();
        UpdateStatus();
    }

    private static ComboBox Choices(string name, int[] values, int current, Action<int> apply)
    {
        ComboBox box = new ComboBox { MinWidth = 50, ToolTip = name };
        foreach (int value in values)
        {
            box.Items.Add(value);
        }
        box.SelectedItem = current;
        box.SelectionChanged += (sender, e) =>
        {
            if (box.SelectedItem is int value)
            {
                apply(value);
            }
        };
        return box;
    }

    private void OpenFile()
    {
        string path = LocalFileDialogWindow.OpenFile(this, "Open a file in the WPF hex editor lab", "All files (*.*)|*.*");
        if (path == null)
        {
            return;
        }
        try
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                MessageDialog.Show(path + " is larger than " + MaxBytes / (1024 * 1024) + " MB: the lab does not read it.", "WPF hex editor lab", DialogIcon.Warning, DialogButtons.Ok, owner: this);
                return;
            }
            byte[] bytes = File.ReadAllBytes(path);
            _fileName = path;
            _editor.Document = new HexDocument(bytes);
            _editor.ReadOnly = _readOnly.IsChecked == true;
            _editor.GoToOffset(0);
            UpdateStatus();
        }
        catch (Exception exception)
        {
            MessageDialog.Show(exception.Message, "WPF hex editor lab", DialogIcon.Error, DialogButtons.Ok, owner: this);
        }
    }

    private void UpdateStatus()
    {
        string selection = _editor.HasSelection
            ? "selection " + HexEditingModel.FormatByteCount(_editor.SelectionLength) + " from 0x" + _editor.SelectionStart.ToString("X8", CultureInfo.InvariantCulture)
            : "no selection";
        _status.Text = _fileName + "   |   offset 0x" + _editor.CaretOffset.ToString("X8", CultureInfo.InvariantCulture) + " (" + _editor.CaretOffset.ToString(CultureInfo.InvariantCulture) + ")   |   " + selection
            + "   |   " + HexEditingModel.FormatByteCount(_editor.Length) + "   |   " + (_editor.InsertMode ? "INS" : "OVR") + (_editor.IsModified ? "   |   modified (not saved)" : "");
    }

    private void Note(string text)
    {
        UpdateStatus();
        _status.Text += "   |   " + text;
    }
}
