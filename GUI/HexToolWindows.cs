using SmarTTY.Controls;
using SmarTTY.TextEditing;
using VisualTTY.DeviceTerminal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;

namespace SmarTTY.GUI;

/// <summary>The layout shared by the small dialogs of the hex view: a column of controls and a row of buttons.</summary>
internal abstract class HexToolWindow : Window
{
    protected readonly StackPanel Panel = new StackPanel { Margin = new Thickness(10) };

    protected HexToolWindow(string title)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());
        Content = Panel;
    }

    protected TextBlock AddText(string text, double top = 0)
    {
        TextBlock block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, top, 0, 5),
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap
        };
        Panel.Children.Add(block);
        return block;
    }

    protected TextBox AddTextBox(string text)
    {
        TextBox box = new TextBox
        {
            Text = text,
            MinWidth = 320
        };
        Panel.Children.Add(box);
        return box;
    }

    /// <summary>Adds the buttons at the bottom right; the first one is the default.</summary>
    protected void AddButtons(params Button[] buttons)
    {
        StackPanel row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        for (int index = 0; index < buttons.Length; index++)
        {
            buttons[index].MinWidth = 75;
            buttons[index].Margin = new Thickness((index == 0) ? 0 : 5, 0, 0, 0);
            row.Children.Add(buttons[index]);
        }
        Panel.Children.Add(row);
    }

    protected Button CreateCancelButton(string text = "Cancel")
    {
        return new Button { Content = text, IsCancel = true };
    }
}

/// <summary>Chooses the colour and the label that mark the selected bytes in the hex view.</summary>
internal sealed class HexHighlightWindow : HexToolWindow
{
    private readonly TextBox _label;

    private readonly WrapPanel _swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };

    private readonly List<RadioButton> _choices = new List<RadioButton>();

    public DrawingColor SelectedColor { get; private set; }

    public string Label => _label.Text ?? "";

    public HexHighlightWindow(DrawingColor color, string label, string target)
        : base("Highlight Bytes")
    {
        SelectedColor = color;
        AddText("Mark " + target + ".");
        AddText("Label (optional; shown in the tool tip and the data inspector):");
        _label = AddTextBox(label ?? "");
        AddText("Color:", 10);
        bool known = false;
        foreach (HexHighlightColor preset in HexEditorControl.HighlightColors)
        {
            bool chosen = preset.Color.ToArgb() == DrawingColor.FromArgb(255, color).ToArgb();
            known |= chosen;
            AddSwatch(preset.Color, preset.Name, chosen);
        }
        if (!known)
        {
            AddSwatch(DrawingColor.FromArgb(255, color), "Custom", true);
        }
        Panel.Children.Add(_swatches);
        Button other = new Button
        {
            Content = "Other Color...",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 1, 8, 1)
        };
        other.Click += (sender, e) => PickOtherColor();
        Panel.Children.Add(other);

        Button ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (sender, e) => DialogResult = true;
        AddButtons(ok, CreateCancelButton());
        Loaded += (sender, e) =>
        {
            _label.Focus();
            _label.SelectAll();
        };
    }

    private void AddSwatch(DrawingColor color, string name, bool chosen)
    {
        RadioButton choice = new RadioButton
        {
            GroupName = "HexHighlightColor",
            IsChecked = chosen,
            ToolTip = name,
            Margin = new Thickness(0, 0, 10, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Border
            {
                Width = 26,
                Height = 18,
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1)
            }
        };
        DrawingColor value = color;
        choice.Checked += (sender, e) => SelectedColor = value;
        _choices.Add(choice);
        _swatches.Children.Add(choice);
    }

    private void PickOtherColor()
    {
        DeviceTerminalSettings.ColorWrapper? picked = ColorPickerWindow.Pick(this, SelectedColor);
        if (picked.HasValue)
        {
            DrawingColor color = DrawingColor.FromArgb(255, picked.Value.Red, picked.Value.Green, picked.Value.Blue);
            AddSwatch(color, "Custom", true);
            SelectedColor = color;
        }
    }
}

/// <summary>Asks for the byte pattern that fills the selection, or for the bytes inserted at the caret.</summary>
internal sealed class HexBytePatternWindow : HexToolWindow
{
    // The most bytes one command inserts: the file is kept in memory and sent to the server whole.
    private const int MaxInsertedBytes = 64 * 1024 * 1024;

    private readonly bool _insert;

    private readonly TextBox _count;

    private readonly TextBox _pattern;

    public byte[] Pattern { get; private set; }

    public int Count { get; private set; }

    public HexBytePatternWindow(bool insert, int selectionLength, int caretOffset)
        : base(insert ? "Insert Bytes" : "Fill Selection")
    {
        _insert = insert;
        if (insert)
        {
            AddText("Insert bytes at offset 0x" + caretOffset.ToString("X8", CultureInfo.InvariantCulture) + ".");
            AddText("Number of bytes (decimal, or 0x hexadecimal):");
            _count = AddTextBox("16");
            AddText("Byte pattern, repeated (hex, for example 00 or DE AD BE EF):", 8);
            _pattern = AddTextBox("00");
        }
        else
        {
            AddText("Fill " + HexEditorControl.FormatByteCount(selectionLength) + " with a byte pattern, repeated");
            AddText("(hex, for example FF for erased flash, or DE AD BE EF):");
            _pattern = AddTextBox("FF");
        }
        Button ok = new Button { Content = insert ? "Insert" : "Fill", IsDefault = true };
        ok.Click += (sender, e) => Accept();
        AddButtons(ok, CreateCancelButton());
        Loaded += (sender, e) =>
        {
            TextBox first = _count ?? _pattern;
            first.Focus();
            first.SelectAll();
        };
    }

    private void Accept()
    {
        if (_insert)
        {
            if (!HexDocument.TryParseOffset(_count.Text, int.MaxValue, out int count) || count <= 0 || count > MaxInsertedBytes)
            {
                MessageBoxes.Info("Enter the number of bytes, from 1 to " + MaxInsertedBytes.ToString("N0", CultureInfo.InvariantCulture) + ", e.g. 16 or 0x100.");
                _count.Focus();
                return;
            }
            Count = count;
        }
        if (!HexDocument.TryParseHexBytes(_pattern.Text, out byte[] pattern, out string error))
        {
            MessageBoxes.Info(error);
            _pattern.Focus();
            return;
        }
        Pattern = pattern;
        DialogResult = true;
    }
}

/// <summary>Shows the checksums and hashes of the selection or of the whole file; every value can be selected and copied.</summary>
internal sealed class HexChecksumWindow : HexToolWindow
{
    private readonly IReadOnlyList<HexChecksum> _checksums;

    public HexChecksumWindow(IReadOnlyList<HexChecksum> checksums, string description)
        : base("Checksums and Hashes")
    {
        _checksums = checksums ?? throw new ArgumentNullException(nameof(checksums));
        AddText(description + ":");
        Grid grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FontFamily monospace = new FontFamily("Consolas, Courier New");
        for (int index = 0; index < checksums.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock name = new TextBlock
            {
                Text = checksums[index].Name,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2)
            };
            TextBox value = new TextBox
            {
                Text = checksums[index].Value,
                IsReadOnly = true,
                FontFamily = monospace,
                MinWidth = 470,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(name, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
        }
        Panel.Children.Add(grid);

        Button copy = new Button { Content = "Copy All" };
        copy.Click += (sender, e) => CopyAll();
        Button close = CreateCancelButton("Close");
        close.IsDefault = true;
        AddButtons(copy, close);
    }

    private void CopyAll()
    {
        StringBuilder text = new StringBuilder();
        foreach (HexChecksum checksum in _checksums)
        {
            text.Append(checksum.Name).Append(": ").Append(checksum.Value).Append("\r\n");
        }
        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
        }
    }
}
