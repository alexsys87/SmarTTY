using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace VisualTTY;

internal class XTermKeyMap
{

    private string[] _plainSequences = new string[256];

    private string[] _appCursorSequences = new string[256];

    private string[] _appNumPadSequences = new string[256];

    private bool[] _numPadNonExtended = new bool[256];

    private bool[] _numPadExtended = new bool[256];

    public XTermKeyMap()
    {
        Stream manifestResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VisualTTY.XTermKeys.xml");
        TerminalKeyLayout terminalKeyLayout = (TerminalKeyLayout)new XmlSerializer(typeof(TerminalKeyLayout)).Deserialize(manifestResourceStream);
        foreach (TerminalKeyLayout.Entry entry in terminalKeyLayout.Entries)
        {
            _plainSequences[(int)(entry.KeyCode & (Keys.F16 | Keys.F17))] = entry.EscapeSequence;
        }
        foreach (TerminalKeyLayout.Entry appModeCursorKey in terminalKeyLayout.AppModeCursorKeys)
        {
            _appCursorSequences[(int)(appModeCursorKey.KeyCode & (Keys.F16 | Keys.F17))] = appModeCursorKey.EscapeSequence;
        }
        foreach (TerminalKeyLayout.Entry appModeNumPadKey in terminalKeyLayout.AppModeNumPadKeys)
        {
            _appNumPadSequences[(int)(appModeNumPadKey.KeyCode & (Keys.F16 | Keys.F17))] = appModeNumPadKey.EscapeSequence;
        }
        foreach (Keys nonExtendedKey in terminalKeyLayout.NumPadKeyCodesNonExtended)
        {
            _numPadNonExtended[(int)(nonExtendedKey & (Keys.F16 | Keys.F17))] = true;
        }
        foreach (Keys extendedKey in terminalKeyLayout.NumPadKeyCodesExtended)
        {
            _numPadExtended[(int)(extendedKey & (Keys.F16 | Keys.F17))] = true;
        }
    }

    public bool IsNumPadKey(Keys keys, bool isExtended)
    {
        if ((uint)keys > 255u)
        {
            return false;
        }
        if (isExtended)
        {
            return this._numPadExtended[(int)keys];
        }
        return _numPadNonExtended[(int)keys];
    }

    public string GetEscapeSequence(Keys keys, bool isNumPadMode, bool isApplicationCursorMode, bool isNumPadKey)
    {
        if (isNumPadMode & isNumPadKey)
        {
            string numPadSeq = this._appNumPadSequences[(int)(keys & (Keys.F16 | Keys.F17))];
            if (numPadSeq != null)
            {
                return numPadSeq;
            }
        }
        if (isApplicationCursorMode)
        {
            string cursorSeq = _appCursorSequences[(int)(keys & (Keys.F16 | Keys.F17))];
            if (cursorSeq != null)
            {
                return cursorSeq;
            }
        }
        return _plainSequences[(int)(keys & (Keys.F16 | Keys.F17))];
    }
}
