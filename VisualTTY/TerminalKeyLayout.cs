using System.Collections.Generic;
using System.Windows.Forms;

namespace VisualTTY;

public class TerminalKeyLayout
{
    public class Entry
    {
        public Keys KeyCode;

        public string EscapeSequence;
    }

    public string Name;

    public List<Entry> Entries = new List<Entry>();

    public List<Entry> AppModeCursorKeys = new List<Entry>();

    public List<Entry> AppModeNumPadKeys = new List<Entry>();

    public List<Keys> NumPadKeyCodesNonExtended = new List<Keys>();

    public List<Keys> NumPadKeyCodesExtended = new List<Keys>();
}
