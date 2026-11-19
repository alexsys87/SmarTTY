using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// The variables set with "export" and "unset" in a Smart Terminal tab, in the order they were set. Every command runs in a channel of its own, so they are
/// put in front of every command (<see cref="BuildExports"/>) and take effect again one after another, as they did when they were typed: a variable that
/// was set from another one ("export B=$A-two") keeps the value A had then, also when A is set again later. A null value stands for "unset".
/// The list is replaced as a whole when a variable is set, so threads that read it (the completion builds commands) never see it half changed.
/// </summary>
public sealed class SessionEnvironment
{
    private readonly object _gate = new object();

    // Never changed in place: Set makes a new array and puts it here.
    private volatile KeyValuePair<string, string>[] _settings = new KeyValuePair<string, string>[0];

    /// <summary>There is a variable that was set or unset.</summary>
    public bool Any => _settings.Length > 0;

    /// <summary>The names of the variables that have a value (the unset ones are left out), in the order of their last setting.</summary>
    public IEnumerable<string> Names
    {
        get
        {
            KeyValuePair<string, string>[] settings = _settings;
            List<string> names = new List<string>();
            for (int index = 0; index < settings.Length; index++)
            {
                if (settings[index].Value != null && LastIndexOf(settings, settings[index].Key) == index)
                {
                    names.Add(settings[index].Key);
                }
            }
            return names;
        }
    }

    /// <summary>
    /// Sets (or, with a null value, unsets) a variable. An earlier setting of it is dropped when nothing between it and the next setting of the same variable
    /// refers to it (the next setting included: "export PATH=$PATH:/x" needs the one before); otherwise it stays, so that what refers to it is worked out with the value it had.
    /// </summary>
    public void Set(KeyValuePair<string, string> assignment)
    {
        lock (_gate)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>(_settings);
            list.Add(assignment);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int index = 0; index < list.Count - 1 && !changed; index++)
                {
                    if (!IsNeeded(list, index))
                    {
                        list.RemoveAt(index);
                        changed = true;
                    }
                }
            }
            _settings = list.ToArray();
        }
    }

    /// <summary>
    /// The commands that set the variables, each followed by " && ", to put in front of a command. "export A=1 && cmd" rather than "A=1 cmd": with the
    /// latter $A in the command itself was expanded before the assignment ("echo $A" printed nothing) and the variable did not reach the other commands of a pipeline.
    /// </summary>
    public string BuildExports()
    {
        StringBuilder exports = new StringBuilder();
        foreach (KeyValuePair<string, string> item in _settings)
        {
            exports.Append(item.Value == null ? "unset " + item.Key : "export " + item.Key + "=" + item.Value);
            exports.Append(" && ");
        }
        return exports.ToString();
    }

    private static int LastIndexOf(IList<KeyValuePair<string, string>> list, string name)
    {
        for (int index = list.Count - 1; index >= 0; index--)
        {
            if (list[index].Key == name)
            {
                return index;
            }
        }
        return -1;
    }

    // A setting that has a later one of the same variable is needed only when a setting between them (the later one included) has a value that refers to
    // the variable: a reference is worked out with the latest setting before it. The last setting of a variable is always needed.
    private static bool IsNeeded(IList<KeyValuePair<string, string>> list, int index)
    {
        string name = list[index].Key;
        for (int later = index + 1; later < list.Count; later++)
        {
            if (list[later].Value != null && Refers(list[later].Value, name))
            {
                return true;
            }
            if (list[later].Key == name)
            {
                return false;
            }
        }
        return true;
    }

    // The value has $NAME or ${NAME... in it (also in single quotes: that only keeps a setting that may not be needed).
    private static bool Refers(string value, string name)
    {
        int position = 0;
        while ((position = value.IndexOf('$', position)) >= 0)
        {
            int start = position + 1;
            if (start < value.Length && value[start] == '{')
            {
                start++;
            }
            if (string.CompareOrdinal(value, start, name, 0, name.Length) == 0 && (start + name.Length >= value.Length || !IsNameCharacter(value[start + name.Length])))
            {
                return true;
            }
            position++;
        }
        return false;
    }

    private static bool IsNameCharacter(char ch)
    {
        return ch == '_' || (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
    }
}
