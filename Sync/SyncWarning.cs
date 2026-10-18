using SmarTTY.Ssh.QuickSync;
using System;

namespace SmarTTY.Sync;

public class SyncWarning
{
    public QuickSyncWarning Warning;

    public Exception Exception;

    public override string ToString()
    {
        string text = Warning.ToString();
        if (Exception != null)
        {
            text = text + ": " + Exception.Message;
        }
        return text;
    }
}
