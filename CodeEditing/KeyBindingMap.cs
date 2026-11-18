using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

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
        public VirtualKey Key;

        public bool Ctrl;

        public bool Alt;

        public bool Shift;

        public bool Matches(KeyStroke e)
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

        public KeyShortcut(KeyStroke e)
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
