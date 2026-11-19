using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// The variables set with "export" and "unset" in a Smart Terminal tab, in the order they were set: a later value may refer to an earlier one.
/// Every command runs in a channel of its own, so they are put in front of every command (<see cref="BuildExports"/>). A null value stands for "unset".
/// </summary>
public sealed class SessionEnvironment
{
    private readonly List<KeyValuePair<string, string>> _environment = new List<KeyValuePair<string, string>>();

    /// <summary>There is a variable that was set or unset.</summary>
    public bool Any => _environment.Count > 0;

    /// <summary>The names of the variables that have a value (the unset ones are left out).</summary>
    public IEnumerable<string> Names => _environment.ToArray().Where((KeyValuePair<string, string> item) => item.Value != null).Select((KeyValuePair<string, string> item) => item.Key);

    /// <summary>Sets (or, with a null value, unsets) a variable: an earlier setting of it is dropped and the new one goes to the end.</summary>
    public void Set(KeyValuePair<string, string> assignment)
    {
        _environment.RemoveAll((KeyValuePair<string, string> item) => item.Key == assignment.Key);
        _environment.Add(assignment);
    }

    /// <summary>
    /// The commands that set the variables, each followed by " && ", to put in front of a command. "export A=1 && cmd" rather than "A=1 cmd": with the
    /// latter $A in the command itself was expanded before the assignment ("echo $A" printed nothing) and the variable did not reach the other commands of a pipeline.
    /// </summary>
    public string BuildExports()
    {
        StringBuilder exports = new StringBuilder();
        // A copy: completion threads build commands while the UI thread may change the list.
        foreach (KeyValuePair<string, string> item in _environment.ToArray())
        {
            exports.Append(item.Value == null ? "unset " + item.Key : "export " + item.Key + "=" + item.Value);
            exports.Append(" && ");
        }
        return exports.ToString();
    }
}
