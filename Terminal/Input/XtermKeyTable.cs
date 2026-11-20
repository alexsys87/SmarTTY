using SmarTTY.TextEditing;
using System;
using System.IO;
using System.Xml;

namespace SmarTTY.Terminal.Input;

/// <summary>
/// The escape sequences of the keys that do not type a character (arrows, function keys, the editing keys, the keypad in application mode),
/// read from the key layout of the application (<c>VisualTTY/XTermKeys.xml</c>, which the WinForms terminal read too until stage 2). The names of the keys in the file are those of <c>Keys</c>, which <see cref="VirtualKey"/> has with the same values.
/// A sequence is stored without its leading ESC.
/// </summary>
public sealed class XtermKeyTable
{
    private readonly string[] _plainSequences = new string[256];

    private readonly string[] _appCursorSequences = new string[256];

    private readonly string[] _appNumPadSequences = new string[256];

    private readonly bool[] _numPadNonExtended = new bool[256];

    private readonly bool[] _numPadExtended = new bool[256];

    /// <summary>Reads a layout (the XML of <c>TerminalKeyLayout</c>).</summary>
    public XtermKeyTable(Stream layout)
    {
        XmlDocument document = new XmlDocument();
        document.Load(layout);
        XmlElement root = document.DocumentElement;
        ReadEntries(root, "Entries", _plainSequences);
        ReadEntries(root, "AppModeCursorKeys", _appCursorSequences);
        ReadEntries(root, "AppModeNumPadKeys", _appNumPadSequences);
        ReadKeys(root, "NumPadKeyCodesNonExtended", _numPadNonExtended);
        ReadKeys(root, "NumPadKeyCodesExtended", _numPadExtended);
    }

    private static int ParseKey(string name)
    {
        VirtualKey key = (VirtualKey)Enum.Parse(typeof(VirtualKey), name.Trim());
        return (int)(key & VirtualKey.KeyCode) & 0xFF;
    }

    private static void ReadEntries(XmlElement root, string listName, string[] sequences)
    {
        if (!(root[listName] is XmlElement list))
        {
            return;
        }
        foreach (XmlNode node in list.ChildNodes)
        {
            if (node is XmlElement entry && entry.Name == "Entry")
            {
                string keyName = entry["KeyCode"]?.InnerText;
                string sequence = entry["EscapeSequence"]?.InnerText;
                if (!string.IsNullOrEmpty(keyName) && sequence != null)
                {
                    sequences[ParseKey(keyName)] = sequence;
                }
            }
        }
    }

    private static void ReadKeys(XmlElement root, string listName, bool[] flags)
    {
        if (!(root[listName] is XmlElement list))
        {
            return;
        }
        foreach (XmlNode node in list.ChildNodes)
        {
            if (node is XmlElement entry && entry.Name == "Keys" && !string.IsNullOrEmpty(entry.InnerText))
            {
                flags[ParseKey(entry.InnerText)] = true;
            }
        }
    }

    /// <summary>The key is one of the numeric keypad (the scan code's extended flag tells the dedicated arrows and Enter from those of the keypad).</summary>
    public bool IsNumPadKey(VirtualKey key, bool isExtended)
    {
        int code = (int)(key & VirtualKey.KeyCode);
        if ((uint)code > 255u)
        {
            return false;
        }
        return isExtended ? _numPadExtended[code] : _numPadNonExtended[code];
    }

    /// <summary>The sequence of a key without ESC, or null when the key has none (it types a character, or it is not a terminal key).</summary>
    public string GetEscapeSequence(VirtualKey key, bool applicationKeypad, bool applicationCursorKeys, bool isNumPadKey)
    {
        int code = (int)(key & VirtualKey.KeyCode) & 0xFF;
        if (applicationKeypad && isNumPadKey)
        {
            string numPadSequence = _appNumPadSequences[code];
            if (numPadSequence != null)
            {
                return numPadSequence;
            }
        }
        if (applicationCursorKeys)
        {
            string cursorSequence = _appCursorSequences[code];
            if (cursorSequence != null)
            {
                return cursorSequence;
            }
        }
        return _plainSequences[code];
    }
}
