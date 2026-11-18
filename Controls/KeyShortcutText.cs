using System;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The text form of a key shortcut ("Control + Shift + K"). The names of the keys come from the WinForms key converter,
/// which is why this lives in the view and not in <see cref="KeyBindingMap.KeyShortcut"/>.
/// </summary>
public static class KeyShortcutText
{
    /// <summary>Reads a shortcut written as "Control + Alt + Shift + Key"; false (and an empty shortcut) when the text is not one.</summary>
    public static bool TryParse(string str, out KeyBindingMap.KeyShortcut keyShortcut)
    {
        keyShortcut = new KeyBindingMap.KeyShortcut();
        try
        {
            bool ctrl = false;
            bool alt = false;
            bool shift = false;
            Keys key = Keys.None;
            KeysConverter keysConverter = new KeysConverter();
            string[] parts = str.Split(new char[2] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                switch (part)
                {
                    case "Control":
                        ctrl = true;
                        break;
                    case "Alt":
                        alt = true;
                        break;
                    case "Shift":
                        shift = true;
                        break;
                    default:
                        key = (Keys)keysConverter.ConvertFromString(part);
                        break;
                }
            }
            if (key == Keys.None)
            {
                return false;
            }
            keyShortcut = new KeyBindingMap.KeyShortcut
            {
                Ctrl = ctrl,
                Alt = alt,
                Shift = shift,
                Key = key.ToVirtualKey()
            };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes a shortcut as "Control + Alt + Shift + Key".</summary>
    public static string Format(KeyBindingMap.KeyShortcut keyShortcut)
    {
        KeysConverter keysConverter = new KeysConverter();
        string text = "";
        if (keyShortcut.Ctrl)
        {
            text += "Control + ";
        }
        if (keyShortcut.Alt)
        {
            text += "Alt + ";
        }
        if (keyShortcut.Shift)
        {
            text += "Shift + ";
        }
        return text + keysConverter.ConvertToString(keyShortcut.Key.ToKeys());
    }
}
