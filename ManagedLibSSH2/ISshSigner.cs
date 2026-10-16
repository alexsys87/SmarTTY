using System;

namespace ManagedLibSSH2;

public interface ISshSigner
{
    byte[] GetPublicKeyBlob();

    int Sign(IntPtr handle, out IntPtr handle2, out IntPtr handle3, byte[] buffer, int value2, IntPtr handle4);
}
