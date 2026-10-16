using System;

namespace ManagedLibSSH2;

public class RemoteOperationException : Exception
{
    public RemoteOperationException(string text)
        : base(text)
    {
    }
}
