using SmarTTY.Terminal;
using System.Collections.Generic;

namespace SmarTTY;

public class LineSplitter
{
    private Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

    private string _pending = "";

    public string PendingText => _pending;

    public IEnumerable<string> Feed(byte[] buffer, int value2, int value3)
    {
        List<string> list = new List<string>();
        this._pending += this._decoder.Decode(buffer, value2, value3);
        int num = 0;
        while (true)
        {
            int num2 = this._pending.IndexOf('\n', num);
            if (num2 == -1)
            {
                break;
            }
            list.Add(this._pending.Substring(num, num2 - num));
            num = num2 + 1;
        }
        if (num != 0)
        {
            this._pending = this._pending.Substring(num);
        }
        return list;
    }
}
