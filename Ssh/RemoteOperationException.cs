using System;

namespace SmarTTY.Ssh;

public class RemoteOperationException : Exception
{
    public RemoteOperationException(string text)
        : base(text)
    {
    }
}
