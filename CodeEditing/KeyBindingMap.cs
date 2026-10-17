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

        public EditorCommand(string name, string description)
        {
            this.Name = name;
            this.Description = description;
            DefaultShortcut = null;
        }

        public override string ToString()
        {
            return Description;
        }

        public abstract void Execute(object target);
    }

    public class KeyShortcut
    {
        public Keys Key;

        public bool Ctrl;

        public bool Alt;

        public bool Shift;

        public bool Matches(KeyEventArgs e)
        {
            if (e.Alt != Alt)
            {
                return false;
            }
            if (e.Control != Ctrl)
            {
                return false;
            }
            if (e.Shift != Shift)
            {
                return false;
            }
            return e.KeyCode == Key;
        }

        public KeyShortcut(KeyEventArgs e)
        {
            Ctrl = e.Control;
            Alt = e.Alt;
            Shift = e.Shift;
            Key = e.KeyCode;
        }

        public KeyShortcut()
        {
        }

        public override int GetHashCode()
        {
            return $"{Key}{Ctrl}{Alt}{Shift}".GetHashCode();
        }

        public override bool Equals(object other)
        {
            if (!(other is KeyShortcut))
            {
                return false;
            }
            KeyShortcut keyShortcut = (KeyShortcut)other;
            if (keyShortcut.Alt == Alt && keyShortcut.Ctrl == Ctrl && keyShortcut.Shift == Shift)
            {
                return keyShortcut.Key == Key;
            }
            return false;
        }

        public static bool TryParse(string str, out KeyShortcut keyShortcut)
        {
            keyShortcut = new KeyShortcut();
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
                keyShortcut = new KeyShortcut
                {
                    Ctrl = ctrl,
                    Alt = alt,
                    Shift = shift,
                    Key = key
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

    public EditorCommand FindCommand(string commandName)
    {
        foreach (EditorCommand cmd in _commands)
        {
            if (cmd.Name == commandName)
            {
                return cmd;
            }
        }
        throw new InvalidOperationException("Command " + commandName + " not found");
    }

    public KeyBindingMap(IEnumerable<EditorCommand> commands)
    {
        _commands.AddRange(commands);
        foreach (EditorCommand cmd in _commands)
        {
            if (cmd.DefaultShortcut != null)
            {
                Bindings[cmd.DefaultShortcut] = cmd;
            }
        }
    }

    public KeyBindingMap()
        : this(EditorCommands.All)
    {
    }
}
