using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Terminal;

public class LineSplitter
{
    private readonly Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

    private readonly StringBuilder _pending = new StringBuilder();

    // How much of _pending (from the start) has already been searched for a newline and found to have none: a line
    // that keeps growing without ending is otherwise rescanned from its very first character on every chunk that
    // arrives for it, which is quadratic in its eventual length. Only the text appended since needs to be searched
    // again, since a newline cannot have appeared earlier without already being found.
    private int _scanned;

    public string PendingText => _pending.ToString();

    public IEnumerable<string> Feed(byte[] buffer, int offset, int count)
    {
        List<string> lines = new List<string>();
        _pending.Append(_decoder.Decode(buffer, offset, count));
        int lineStart = 0;
        for (int index = _scanned; index < _pending.Length; index++)
        {
            if (_pending[index] == '\n')
            {
                lines.Add(_pending.ToString(lineStart, index - lineStart));
                lineStart = index + 1;
            }
        }
        if (lineStart != 0)
        {
            _pending.Remove(0, lineStart);
        }
        _scanned = _pending.Length;
        return lines;
    }
}
