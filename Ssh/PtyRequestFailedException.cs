using System;

namespace SmarTTY.Ssh;

public class PtyRequestFailedException : Exception
{
    internal PtyRequestFailedException()
        : base("The SSH server refused to allocate a pseudo-terminal (PTY).")
    {
    }
}
