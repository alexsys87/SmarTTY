using SmarTTY.IO;
using System;
using System.Windows;
using System.Windows.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// The "Permissions..." dialog of the file list: read, write and execute for the owner, the group and the others, the
/// setuid, setgid and sticky bits, the octal form, and whether the change reaches into the selected folders. Built in
/// code, like <see cref="GoToLineWindow"/>.
/// </summary>
internal sealed class RemotePermissionsWindow : Window
{
    /// <summary>What the user asked for: the bits to switch and how far the change reaches.</summary>
    public sealed class Request
    {
        public RemotePermissionChange Change;

        public ChmodScope Scope;
    }

    // The twelve check boxes, in the order of these masks: owner, group and others (read, write, execute), then the special bits.
    private static readonly int[] Masks =
    {
        RemotePermissions.OwnerRead, RemotePermissions.OwnerWrite, RemotePermissions.OwnerExecute,
        RemotePermissions.GroupRead, RemotePermissions.GroupWrite, RemotePermissions.GroupExecute,
        RemotePermissions.OtherRead, RemotePermissions.OtherWrite, RemotePermissions.OtherExecute,
        RemotePermissions.SetUid, RemotePermissions.SetGid, RemotePermissions.Sticky
    };

    private readonly CheckBox[] _boxes = new CheckBox[Masks.Length];

    private readonly bool?[] _initial = new bool?[Masks.Length];

    private readonly TextBox _octal;

    private readonly CheckBox _recursive;

    private readonly ComboBox _scope;

    private bool _updating;

    /// <param name="modes">The modes of the selected items; null for an item whose mode is not known.</param>
    /// <param name="anyFolder">True if a folder is selected: only then can the change reach into the contents.</param>
    private RemotePermissionsWindow(string prompt, int?[] modes, bool anyFolder)
    {
        Title = "Permissions";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");

        Grid root = new Grid
        {
            Margin = new Thickness(12),
            MinWidth = 356
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int column = 0; column < 3; column++)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90.0) });
        }
        for (int row = 0; row < 11; row++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddAt(root, new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }, 0, 0, 4);

        string[] columns = { "Owner", "Group", "Others" };
        string[] rows = { "Read", "Write", "Execute" };
        for (int column = 0; column < 3; column++)
        {
            AddAt(root, new TextBlock { Text = columns[column], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) }, 1, column + 1, 1);
        }
        for (int row = 0; row < 3; row++)
        {
            AddAt(root, new TextBlock { Text = rows[row], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) }, row + 2, 0, 1);
        }
        for (int index = 0; index < 9; index++)
        {
            // Index runs through read, write, execute of the owner, then of the group, then of the others.
            int column = index / 3;
            int row = index % 3;
            _boxes[index] = NewBox(root, "", row + 2, column + 1);
        }

        string[] specialNames = { "Set user ID", "Set group ID", "Sticky bit" };
        for (int index = 0; index < 3; index++)
        {
            _boxes[9 + index] = NewBox(root, specialNames[index], 5, 1 + index);
        }

        StackPanel octalRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        octalRow.Children.Add(new TextBlock { Text = "Octal:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _octal = new TextBox { MaxLength = 4, Width = 60 };
        octalRow.Children.Add(_octal);
        AddAt(root, octalRow, 6, 0, 4);

        _recursive = new CheckBox { Content = "Also change what is inside the selected folders", IsEnabled = anyFolder, Margin = new Thickness(0, 10, 0, 0) };
        AddAt(root, _recursive, 7, 0, 4);

        _scope = new ComboBox
        {
            IsEnabled = false,
            MinWidth = 200,
            Margin = new Thickness(20, 6, 0, 0)
        };
        _scope.Items.Add("Files and folders");
        _scope.Items.Add("Files only");
        _scope.Items.Add("Folders only");
        _scope.SelectedIndex = 0;
        AddAt(root, _scope, 8, 0, 4);

        TextBlock note = new TextBlock
        {
            Text = "Only the permissions you change are applied to what is inside the folders; the others stay as they are.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        AddAt(root, note, 9, 0, 4);

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
        AddAt(root, buttons, 10, 0, 4);

        Content = root;

        // A bit is on or off in the boxes when it is so for all the selected items, and "mixed" (a third state that leaves it
        // as it is) when they differ or a mode is unknown.
        for (int index = 0; index < Masks.Length; index++)
        {
            int on = 0;
            int off = 0;
            foreach (int? mode in modes)
            {
                if (!mode.HasValue)
                {
                    on++;
                    off++;
                }
                else if ((mode.Value & Masks[index]) != 0)
                {
                    on++;
                }
                else
                {
                    off++;
                }
            }
            _initial[index] = on > 0 && off > 0 ? (bool?)null : on > 0;
            _boxes[index].IsThreeState = _initial[index] == null;
            _boxes[index].IsChecked = _initial[index];
        }
        UpdateOctal();

        foreach (CheckBox box in _boxes)
        {
            box.Click += (object sender, RoutedEventArgs e) => UpdateOctal();
        }
        _octal.TextChanged += (object sender, TextChangedEventArgs e) => ApplyOctal();
        _recursive.Click += (object sender, RoutedEventArgs e) => _scope.IsEnabled = _recursive.IsChecked == true;
    }

    private static CheckBox NewBox(Grid root, string text, int row, int column)
    {
        CheckBox box = new CheckBox
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        };
        AddAt(root, box, row, column, 1);
        return box;
    }

    private static void AddAt(Grid root, UIElement element, int row, int column, int columnSpan)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        root.Children.Add(element);
    }

    // The octal text follows the boxes, as long as every bit is on or off.
    private void UpdateOctal()
    {
        if (_updating)
        {
            return;
        }
        int mode = 0;
        for (int index = 0; index < Masks.Length; index++)
        {
            if (_boxes[index].IsChecked == null)
            {
                SetOctalText("");
                return;
            }
            if (_boxes[index].IsChecked == true)
            {
                mode |= Masks[index];
            }
        }
        SetOctalText(RemotePermissions.ToOctal(mode));
    }

    private void SetOctalText(string text)
    {
        _updating = true;
        try
        {
            _octal.Text = text;
        }
        finally
        {
            _updating = false;
        }
    }

    // Typing a number sets the boxes (a mode of up to three digits has no special bits, so those boxes go off).
    private void ApplyOctal()
    {
        if (_updating || !RemotePermissions.TryParseOctal(_octal.Text, out int mode))
        {
            return;
        }
        _updating = true;
        try
        {
            for (int index = 0; index < Masks.Length; index++)
            {
                _boxes[index].IsChecked = (mode & Masks[index]) != 0;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    // The bits whose box differs from what the items had: those are the ones to switch.
    private Request GetRequest()
    {
        RemotePermissionChange change = new RemotePermissionChange();
        for (int index = 0; index < Masks.Length; index++)
        {
            bool? state = _boxes[index].IsChecked;
            if (state == _initial[index] || state == null)
            {
                continue;
            }
            if (state == true)
            {
                change.SetBits |= Masks[index];
            }
            else
            {
                change.ClearBits |= Masks[index];
            }
        }
        ChmodScope scope = ChmodScope.Selected;
        if (_recursive.IsChecked == true)
        {
            scope = _scope.SelectedIndex == 1 ? ChmodScope.RecursiveFiles : _scope.SelectedIndex == 2 ? ChmodScope.RecursiveFolders : ChmodScope.Recursive;
        }
        return new Request { Change = change, Scope = scope };
    }

    /// <summary>Shows the dialog; returns what to change, or null if it was cancelled or nothing was changed.</summary>
    internal static Request Ask(Window owner, string prompt, int?[] modes, bool anyFolder)
    {
        RemotePermissionsWindow dialog = new RemotePermissionsWindow(prompt, modes, anyFolder);
        WindowOwnership.Attach(dialog, owner);
        if (dialog.ShowDialog() != true)
        {
            return null;
        }
        Request request = dialog.GetRequest();
        return request.Change.IsEmpty ? null : request;
    }
}
