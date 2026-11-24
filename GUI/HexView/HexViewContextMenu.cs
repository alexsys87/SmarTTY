using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Image = System.Windows.Controls.Image;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;

namespace SmarTTY.GUI.HexView;

/// <summary>
/// The context menu of the WPF hex view in the editor tab (stage 3B-3): what the WinForms context menu of the remote file editor panel (removed in 3E-5) showed
/// in the hex view, on WPF. The commands of the editor are the panel's (<see cref="EditorCommandCatalog.ContextMenuLayout"/>, run through
/// <see cref="RemoteFileEditorCommands.RunCommand"/>, enabled, checked and shown by <see cref="RemoteFileEditorCommands.CommandState"/>); the Text submenu
/// is there and off, as it is in the hex view; the submenus of the hex view are <see cref="HexMenuModel"/>, after Hex Editor.
/// </summary>
internal sealed class HexViewContextMenu : ContextMenu
{
    private readonly RemoteFileEditorCommands _panel;

    private readonly Dictionary<EditorToolbarCommand, MenuItem> _commands = new Dictionary<EditorToolbarCommand, MenuItem>();

    private readonly List<KeyValuePair<HexSubmenu, MenuItem>> _submenus = new List<KeyValuePair<HexSubmenu, MenuItem>>();

    private readonly List<KeyValuePair<HexMenuItem, MenuItem>> _items = new List<KeyValuePair<HexMenuItem, MenuItem>>();

    /// <param name="afterCommand">What follows a command of a hex submenu (the focus back to the view, the state of the commands).</param>
    public HexViewContextMenu(RemoteFileEditorCommands panel, IHexEditorView view, Action<HexDialogKind> requestDialog, Action afterCommand)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        if (view == null)
        {
            throw new ArgumentNullException(nameof(view));
        }
        afterCommand = afterCommand ?? (() => { });
        HexMenuModel model = new HexMenuModel(view, requestDialog,
            error => ThemedMessageBox.Show(error, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Asterisk));
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
                // The Text submenu of the text view: off in the hex view.
                Items.Add(new MenuItem { Header = "Text", IsEnabled = false });
            }
            else if (command.Value == EditorToolbarCommand.ToggleHex)
            {
                foreach (HexSubmenu submenu in model.Submenus)
                {
                    Items.Add(CreateSubmenu(submenu, afterCommand));
                }
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
        item.Click += (sender, e) => _panel.RunCommand(command);
        _commands[command] = item;
        return item;
    }

    private MenuItem CreateSubmenu(HexSubmenu submenu, Action afterCommand)
    {
        MenuItem menu = new MenuItem { Header = submenu.Text };
        foreach (HexMenuItem entry in submenu.Items)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            MenuItem item = new MenuItem
            {
                Header = entry.Text,
                InputGestureText = entry.Shortcut ?? ""
            };
            if (entry.Swatch.HasValue)
            {
                RgbColor color = entry.Swatch.Value;
                item.Icon = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
                    Stroke = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    StrokeThickness = 1
                };
            }
            HexMenuItem command = entry;
            item.Click += (sender, e) =>
            {
                try
                {
                    command.Run();
                }
                catch (Exception exception)
                {
                    ThemedMessageBox.Show(exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
                afterCommand();
            };
            menu.Items.Add(item);
            _items.Add(new KeyValuePair<HexMenuItem, MenuItem>(entry, item));
        }
        _submenus.Add(new KeyValuePair<HexSubmenu, MenuItem>(submenu, menu));
        return menu;
    }

    /// <summary>Enables, checks and shows the items as the editor allows now (when the menu opens).</summary>
    private void Refresh()
    {
        _panel.RefreshState();
        EditorCommandState state = _panel.CommandState;
        foreach (KeyValuePair<EditorToolbarCommand, MenuItem> pair in _commands)
        {
            pair.Value.IsEnabled = state.IsEnabled(pair.Key);
            pair.Value.IsChecked = state.IsChecked(pair.Key);
            pair.Value.Visibility = state.IsVisible(pair.Key) ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (KeyValuePair<HexSubmenu, MenuItem> pair in _submenus)
        {
            pair.Value.IsEnabled = pair.Key.IsEnabled;
        }
        foreach (KeyValuePair<HexMenuItem, MenuItem> pair in _items)
        {
            pair.Value.IsEnabled = pair.Key.IsEnabled;
            pair.Value.IsChecked = pair.Key.IsChecked;
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
