using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.IO;

/// <summary>
/// Protects the drafts of the editor with the Windows data protection (DPAPI) for the current user: the files in the
/// Drafts folder can then be read only by this user on this computer, not by other accounts, backups or a copied folder.
/// </summary>
public sealed class DpapiDraftProtector : IDraftProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SmarTTY editor draft");

    public byte[] Protect(byte[] data)
    {
        return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] data)
    {
        return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }
}
