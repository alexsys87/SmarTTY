using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.CodeEditing;

public class KeyBindingMap
{
    public abstract class EditorCommand
    {
        public string Name;

        public string Description;

        public KeyShortcut DefaultShortcut;

        public EditorCommand(string text, string text2)
        {
            this.Name = text;
            this.Description = text2;
            DefaultShortcut = null;
        }

        public override string ToString()
        {
            return Description;
        }

        public abstract void Execute(object obj);
    }

    public class KeyShortcut
    {
        public Keys Key;

        public bool Ctrl;

        public bool Alt;

        public bool Shift;

        public bool Matches(KeyEventArgs e2)
        {
            if (e2.Alt != Alt)
            {
                return false;
            }
            if (e2.Control != Ctrl)
            {
                return false;
            }
            if (e2.Shift != Shift)
            {
                return false;
            }
            return e2.KeyCode == Key;
        }

        public KeyShortcut(KeyEventArgs e2)
        {
            Ctrl = e2.Control;
            Alt = e2.Alt;
            Shift = e2.Shift;
            Key = e2.KeyCode;
        }

        public KeyShortcut()
        {
        }

        public override int GetHashCode()
        {
            return $"{Key}{Ctrl}{Alt}{Shift}".GetHashCode();
        }

        public override bool Equals(object obj)
        {
            if (!(obj is KeyShortcut))
            {
                return false;
            }
            KeyShortcut keyShortcut = (KeyShortcut)obj;
            if (keyShortcut.Alt == Alt && keyShortcut.Ctrl == Ctrl && keyShortcut.Shift == Shift)
            {
                return keyShortcut.Key == Key;
            }
            return false;
        }

        public static bool TryParse(string text2, out KeyShortcut keyShortcut)
        {
            keyShortcut = new KeyShortcut();
            try
            {
                bool flag = false;
                bool flag2 = false;
                bool flag3 = false;
                Keys keys = Keys.None;
                KeysConverter keysConverter = new KeysConverter();
                string[] array = text2.Split(new char[2] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string text in array)
                {
                    switch (text)
                    {
                        case "Control":
                            flag = true;
                            break;
                        case "Alt":
                            flag2 = true;
                            break;
                        case "Shift":
                            flag3 = true;
                            break;
                        default:
                            keys = (Keys)keysConverter.ConvertFromString(text);
                            break;
                    }
                }
                if (keys == Keys.None)
                {
                    return false;
                }
                keyShortcut = new KeyShortcut
                {
                    Ctrl = flag,
                    Alt = flag2,
                    Shift = flag3,
                    Key = keys
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override string ToString()
        {
            KeysConverter keysConverter = new KeysConverter();
            string text = "";
            if (Ctrl)
            {
                text += "Control + ";
            }
            if (Alt)
            {
                text += "Alt + ";
            }
            if (Shift)
            {
                text += "Shift + ";
            }
            return text + keysConverter.ConvertToString(Key);
        }
    }

    private List<EditorCommand> _commands = new List<EditorCommand>();

    public Dictionary<KeyShortcut, EditorCommand> Bindings = new Dictionary<KeyShortcut, EditorCommand>();

    public EditorCommand FindCommand(string text)
    {
        foreach (EditorCommand item in _commands)
        {
            if (item.Name == text)
            {
                return item;
            }
        }
        throw new InvalidOperationException("Command " + text + " not found");
    }

    public KeyBindingMap(IEnumerable<EditorCommand> items)
    {
        _commands.AddRange(items);
        foreach (EditorCommand item in _commands)
        {
            if (item.DefaultShortcut != null)
            {
                Bindings[item.DefaultShortcut] = item;
            }
        }
    }

    public KeyBindingMap()
        : this(EditorCommands.All)
    {
    }
}
