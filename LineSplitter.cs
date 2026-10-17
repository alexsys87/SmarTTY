using SmarTTY.Terminal;
using System.Collections.Generic;

namespace SmarTTY;

public class LineSplitter
{
    private Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

    private string _pending = "";

    public string PendingText => _pending;

    public IEnumerable<string> Feed(byte[] buffer, int offset, int count)
    {
        List<string> lines = new List<string>();
        this._pending += this._decoder.Decode(buffer, offset, count);
        int startIndex = 0;
        while (true)
        {
            int newlineIndex = this._pending.IndexOf('\n', startIndex);
            if (newlineIndex == -1)
            {
                break;
            }
            lines.Add(this._pending.Substring(startIndex, newlineIndex - startIndex));
            startIndex = newlineIndex + 1;
        }
        if (startIndex != 0)
        {
            this._pending = this._pending.Substring(startIndex);
        }
        return lines;
    }
}
