using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The context menu of the WPF text view in the editor tab (stage 3E-4): what the WinForms context menu of <see cref="RemoteFileEditorPanel"/> shows in
/// the text view, on WPF. The commands of the editor are those of <see cref="EditorCommandCatalog.ContextMenuLayout"/>, run through
/// <see cref="RemoteFileEditorCommands.RunCommand"/>, enabled, checked and shown by <see cref="RemoteFileEditorCommands.CommandState"/>; the Text submenu
/// after Word Wrap runs <see cref="RemoteFileEditorCommands.RunTransform"/>. The submenus of the hex view are not here: the hex view has its own menu.
/// </summary>
internal sealed class TextViewContextMenu : ContextMenu
{
    // The commands of the Text submenu, in the order of the WinForms menu.
    private static readonly LineTransformKind[] Transforms =
    {
        LineTransformKind.TrimTrailingWhitespace,
        LineTransformKind.EnsureFinalNewline,
        LineTransformKind.SortAscending,
        LineTransformKind.SortDescending,
        LineTransformKind.RemoveDuplicateLines,
        LineTransformKind.IndentationToSpaces,
        LineTransformKind.IndentationToTabs
    };

    private readonly RemoteFileEditorCommands _commands;

    private readonly Dictionary<EditorToolbarCommand, MenuItem> _items = new Dictionary<EditorToolbarCommand, MenuItem>();

    private readonly Dictionary<LineTransformKind, MenuItem> _transformItems = new Dictionary<LineTransformKind, MenuItem>();

    private readonly MenuItem _textMenu;

    public TextViewContextMenu(RemoteFileEditorCommands commands)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _textMenu = new MenuItem { Header = "Text" };
        foreach (LineTransformKind kind in Transforms)
        {
            LineTransformKind current = kind;
            MenuItem item = new MenuItem { Header = LineTransforms.Title(kind) };
            item.Click += (sender, e) => _commands.RunTransform(current);
            _transformItems[kind] = item;
            _textMenu.Items.Add(item);
        }
        foreach (EditorToolbarCommand? command in EditorCommandCatalog.ContextMenuLayout)
        {
            if (!command.HasValue)
            {
                Items.Add(new Separator());
                continue;
            }
            Items.Add(CreateCommandItem(command.Value));
            if (command.Value == EditorToolbarCommand.WordWrap)
            {
                // After Word Wrap, with the other commands about how the text looks and is laid out.
                Items.Add(_textMenu);
            }
        }
        Opened += (sender, e) => Refresh();
    }

    private MenuItem CreateCommandItem(EditorToolbarCommand command)
    {
        EditorCommandDescription description = EditorCommandCatalog.Describe(command);
        // The key is shown only: the view handles the key itself.
        MenuItem item = new MenuItem
        {
            Header = description.Text,
            InputGestureText = description.Shortcut,
            Icon = new Image { Source = LoadIcon(description.IconPath), Width = 16, Height = 16 }
        };
        RenderOptions.SetBitmapScalingMode((Image)item.Icon, BitmapScalingMode.HighQuality);
        item.Click += (sender, e) => _commands.RunCommand(command);
        _items[command] = item;
        return item;
    }

    /// <summary>Enables, checks and shows the items as the editor allows now (when the menu opens).</summary>
    private void Refresh()
    {
        _commands.RefreshState();
        EditorCommandState state = _commands.CommandState;
        foreach (KeyValuePair<EditorToolbarCommand, MenuItem> pair in _items)
        {
            pair.Value.IsEnabled = state.IsEnabled(pair.Key);
            pair.Value.IsChecked = state.IsChecked(pair.Key);
            pair.Value.Visibility = state.IsVisible(pair.Key) ? Visibility.Visible : Visibility.Collapsed;
        }
        _textMenu.IsEnabled = !state.ReadOnly && !state.HexMode;
        foreach (KeyValuePair<LineTransformKind, MenuItem> pair in _transformItems)
        {
            pair.Value.IsEnabled = state.IsEnabled(pair.Key);
        }
    }

    private static ImageSource LoadIcon(string path)
    {
        BitmapImage image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/icons/" + path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
