using SmarTTY.Controls;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The tool bar above the remote file editor. WPF scales its icons, spacing and text for high-DPI monitors by itself,
/// unlike the WinForms controls the editor is made of. The commands run in the editor panel, which also owns the
/// context menu with the same commands.
/// </summary>
internal sealed class RemoteFileEditorToolBar : ToolBar
{
    private readonly Dictionary<EditorToolbarCommand, ButtonBase> _buttons = new Dictionary<EditorToolbarCommand, ButtonBase>();

    public RemoteFileEditorToolBar(RemoteFileEditorPanel panel)
    {
        if (panel == null)
        {
            throw new ArgumentNullException(nameof(panel));
        }
        foreach (EditorToolbarCommand? command in EditorCommandCatalog.ToolBarLayout)
        {
            if (!command.HasValue)
            {
                Separator separator = new Separator();
                separator.SetResourceReference(StyleProperty, ToolBar.SeparatorStyleKey);
                Items.Add(separator);
                continue;
            }
            EditorCommandDescription description = EditorCommandCatalog.Describe(command.Value);
            Image icon = new Image
            {
                Source = LoadIcon(description.IconPath),
                Width = 16,
                Height = 16
            };
            // The images are 16 pixels; smooth the upscaling on a high-DPI monitor.
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            // Commands that switch something on are toggle buttons; the state below keeps them in step with the editor.
            ButtonBase button = (command.Value == EditorToolbarCommand.ShowAllCharacters)
                ? new ToggleButton { Content = icon, ToolTip = description.ToolTip }
                : new Button { Content = icon, ToolTip = description.ToolTip };
            ToolTipService.SetShowOnDisabled(button, true);
            button.SetResourceReference(StyleProperty, (command.Value == EditorToolbarCommand.ShowAllCharacters)
                ? ToolBar.ToggleButtonStyleKey
                : ToolBar.ButtonStyleKey);
            EditorToolbarCommand current = command.Value;
            button.Click += (sender, e) => panel.RunCommand(current);
            _buttons[current] = button;
            Items.Add(button);
        }
        panel.StateChanged += (sender, e) => UpdateState(panel.CommandState);
        UpdateState(panel.CommandState);
    }

    private void UpdateState(EditorCommandState state)
    {
        foreach (KeyValuePair<EditorToolbarCommand, ButtonBase> pair in _buttons)
        {
            pair.Value.IsEnabled = state.IsEnabled(pair.Key);
            if (pair.Value is ToggleButton toggle)
            {
                toggle.IsChecked = state.IsChecked(pair.Key);
            }
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
