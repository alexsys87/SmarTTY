using System;
using System.Collections.Generic;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// What <see cref="NetconsoleLineInput"/> needs of the panel: the text of the input box (read, and set with the caret put at its end), sending the line
/// in it, sending bytes to the device as they are, the encoding of the connection, and the lines sent before. Every member is something the panel did itself before.
/// </summary>
public interface INetconsoleLineHost
{
    /// <summary>The text of the input box.</summary>
    string InputText { get; }

    /// <summary>Puts text in the input box and the caret after it.</summary>
    void SetInputText(string text);

    /// <summary>Sends the line in the box with the line ending of the connection (and empties the box and <see cref="NetconsoleLineInput.Reset"/> when it was sent).</summary>
    void SendLine();

    /// <summary>Sends bytes to the device as they are.</summary>
    void SendBytes(byte[] bytes);

    /// <summary>The bytes of a text in the encoding of the connection.</summary>
    byte[] Encode(string text);

    /// <summary>The lines sent before, the oldest first (the history of this tab).</summary>
    IReadOnlyList<string> History { get; }
}

/// <summary>
/// Line input of a netconsole tab: what is typed into the terminal goes into the input box below it instead of to the device, and Up and Down in the
/// box walk through the lines sent before. The text that was in the box before the walk started is kept as a draft and given back when Down goes past the newest line.
/// </summary>
public sealed class NetconsoleLineInput
{
    private readonly INetconsoleLineHost _host;

    // The history entry Up/Down showed last in the input box; -1 while the box holds what is being typed.
    private int _historyIndex = -1;

    // What was typed before Up was pressed, given back when Down goes past the newest entry.
    private string _draft = "";

    public NetconsoleLineInput(INetconsoleLineHost host)
    {
        _host = host;
    }

    /// <summary>The walk through the history starts again from the newest line, and the draft is forgotten (the line was sent or Esc cleared the box).</summary>
    public void Reset()
    {
        _draft = "";
        _historyIndex = -1;
    }

    // Below the newest entry while the box holds what is being typed.
    private int CurrentIndex(IReadOnlyList<string> history)
    {
        return _historyIndex < 0 || _historyIndex > history.Count ? history.Count : _historyIndex;
    }

    /// <summary>Up: the line before the one in the box (the first time, the box is kept as the draft).</summary>
    public void Older()
    {
        IReadOnlyList<string> history = _host.History;
        int index = CurrentIndex(history);
        if (index > 0)
        {
            if (index == history.Count)
            {
                _draft = _host.InputText;
            }
            index--;
            _historyIndex = index;
            _host.SetInputText(history[index]);
        }
    }

    /// <summary>Down: the line after the one in the box, or the draft past the newest one.</summary>
    public void Newer()
    {
        IReadOnlyList<string> history = _host.History;
        int index = CurrentIndex(history);
        if (index < history.Count)
        {
            index++;
            _historyIndex = index == history.Count ? -1 : index;
            _host.SetInputText(index == history.Count ? _draft : history[index]);
        }
    }

    /// <summary>
    /// What is typed or pasted into the terminal itself: letters and tabs go into the box, Enter sends the line, Backspace edits it; the other control
    /// characters (Ctrl+C) go to the device at once, as one write after the rest.
    /// </summary>
    public void Process(string text)
    {
        StringBuilder printable = new StringBuilder();
        List<byte> direct = new List<byte>();
        char previous = '\0';
        foreach (char c in text)
        {
            char current = c;
            bool pairedLineFeed = current == '\n' && previous == '\r';
            previous = current;
            if (current == '\r' || current == '\n')
            {
                if (pairedLineFeed)
                {
                    continue; // the CR of the pair has sent the line already
                }
                AppendToInput(printable.ToString());
                printable.Clear();
                _host.SendLine();
            }
            else if (c == '\b' || c == '\u007f')
            {
                if (printable.Length > 0)
                {
                    printable.Length--;
                }
                else if (_host.InputText.Length > 0)
                {
                    _host.SetInputText(_host.InputText.Substring(0, _host.InputText.Length - 1));
                }
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                printable.Append(c);
            }
            else
            {
                direct.AddRange(_host.Encode(c.ToString()));
            }
        }
        AppendToInput(printable.ToString());
        if (direct.Count > 0)
        {
            _host.SendBytes(direct.ToArray());
        }
    }

    private void AppendToInput(string text)
    {
        if (text.Length > 0)
        {
            _host.SetInputText(_host.InputText + text);
        }
    }
}
