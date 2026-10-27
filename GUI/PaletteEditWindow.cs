using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI;

/// <summary>
/// The "Edit console palette" dialog: the eight normal and eight bright colors of the terminal, each swatch opening the
/// color picker, and a link that restores the PuTTY palette. Built in code, like <see cref="GoToLineWindow"/>.
/// </summary>
internal sealed class PaletteEditWindow : Window
{
    private ConsolePalette _palette;

    private readonly Border[] _normal = new Border[8];

    private readonly Border[] _bright = new Border[8];

    /// <summary>The palette being edited (a clone of the one passed in).</summary>
    public ConsolePalette Palette => _palette;

    private PaletteEditWindow(ConsolePalette consolePalette)
    {
        Title = "Edit console palette";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");

        _palette = consolePalette.Clone();
        if (_palette.NormalColors.Length < 8 || _palette.BrightColors.Length < 8)
        {
            _palette = ConsolePalette.PuTTY.Clone();
        }

        Grid root = new Grid
        {
            Margin = new Thickness(12),
            MinWidth = 460
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90.0) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        for (int row = 0; row < 11; row++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        string[] columns = { "Color index", "Normal color", "Bright color" };
        for (int column = 0; column < 3; column++)
        {
            AddAt(root, new TextBlock
            {
                Text = columns[column],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 6)
            }, 0, column);
        }
        for (int index = 0; index < 8; index++)
        {
            AddAt(root, new TextBlock
            {
                Text = index.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2)
            }, index + 1, 0);
            _normal[index] = AddSwatch(root, index + 1, 1, index, isBright: false);
            _bright[index] = AddSwatch(root, index + 1, 2, index, isBright: true);
        }
        for (int index = 0; index < 8; index++)
        {
            _normal[index].Background = ToBrush(_palette.NormalColors[index]);
            _bright[index].Background = ToBrush(_palette.BrightColors[index]);
        }

        Button restore = new Button
        {
            Content = "Restore default palette",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        restore.SetResourceReference(StyleProperty, "HyperlinkButton");
        restore.Click += Restore_Click;
        AddAt(root, restore, 9, 0, 3);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Button ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 80,
            Margin = new Thickness(0, 0, 5, 0)
        };
        ok.Click += (object sender, RoutedEventArgs e) => DialogResult = true;
        Button cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        AddAt(root, buttons, 10, 0, 3);

        Content = root;
    }

    private Border AddSwatch(Grid root, int row, int column, int colorIndex, bool isBright)
    {
        Border swatch = new Border
        {
            Height = 22,
            Margin = new Thickness(4, 2, 4, 2),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");
        swatch.MouseLeftButtonDown += (object sender, MouseButtonEventArgs e) => PickColor(colorIndex, isBright);
        AddAt(root, swatch, row, column);
        return swatch;
    }

    private static void AddAt(Grid root, UIElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        root.Children.Add(element);
    }

    private void PickColor(int colorIndex, bool isBright)
    {
        DeviceTerminalSettings.ColorWrapper current = isBright ? _palette.BrightColors[colorIndex] : _palette.NormalColors[colorIndex];
        DeviceTerminalSettings.ColorWrapper? selected = ColorPickerWindow.Pick(this, current);
        if (selected == null)
        {
            return;
        }
        if (isBright)
        {
            _palette.BrightColors[colorIndex] = selected.Value;
            _bright[colorIndex].Background = ToBrush(selected.Value);
        }
        else
        {
            _palette.NormalColors[colorIndex] = selected.Value;
            _normal[colorIndex].Background = ToBrush(selected.Value);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        _palette = ConsolePalette.PuTTY.Clone();
        for (int index = 0; index < 8; index++)
        {
            _normal[index].Background = ToBrush(_palette.NormalColors[index]);
            _bright[index].Background = ToBrush(_palette.BrightColors[index]);
        }
    }

    private static Brush ToBrush(DeviceTerminalSettings.ColorWrapper color)
    {
        return new SolidColorBrush(Color.FromArgb((byte)color.Alpha, (byte)color.Red, (byte)color.Green, (byte)color.Blue));
    }

    /// <summary>Shows the dialog; returns the edited palette, or null if it was cancelled.</summary>
    internal static ConsolePalette Ask(Window owner, ConsolePalette palette)
    {
        PaletteEditWindow window = new PaletteEditWindow(palette);
        WindowOwnership.Attach(window, owner);
        return window.ShowDialog() == true ? window.Palette : null;
    }
}
