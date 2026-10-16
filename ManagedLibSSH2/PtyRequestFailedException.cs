using System;

namespace ManagedLibSSH2;

public class PtyRequestFailedException : Exception
{
    internal PtyRequestFailedException(LibSsh2Exception libSsh2Exception)
        : base("SSH PTY request failed (" + libSsh2Exception.Message + "). Try increasing minimal delay between PTY requests and the extra PTY request delay under Tools->Options->VisualGDB->General->SSH.", libSsh2Exception)
    {
    }
}
