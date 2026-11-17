using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SmarTTY.Native;

/// <summary>
/// The system certificate picker. X509Certificate2UI is not part of .NET (Core), so the same Windows dialog is
/// opened through cryptui.dll.
/// </summary>
internal static class CertificateDialog
{
    private const uint CERT_STORE_ADD_ALWAYS = 4;

    // CERT_STORE_PROV_MEMORY is the predefined provider name "Memory" (an integer constant, not a string).
    private static readonly IntPtr CERT_STORE_PROV_MEMORY = new IntPtr(2);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertOpenStore(IntPtr storeProvider, uint encodingType, IntPtr cryptProv, uint flags, IntPtr parameters);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertAddCertificateContextToStore(IntPtr store, IntPtr certificateContext, uint addDisposition, IntPtr storedContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertFreeCertificateContext(IntPtr certificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr store, uint flags);

    [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CryptUIDlgSelectCertificateFromStore(IntPtr store, IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPWStr)] string title, [MarshalAs(UnmanagedType.LPWStr)] string displayString,
        uint displayFlags, uint flags, IntPtr reserved);

    /// <summary>Shows the picker and returns the thumbprint of the chosen certificate, or null when the dialog is cancelled.</summary>
    public static string SelectThumbprint(IEnumerable<X509Certificate2> certificates, IntPtr owner, string title, string message)
    {
        IntPtr store = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, IntPtr.Zero, 0, IntPtr.Zero);
        if (store == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            foreach (X509Certificate2 certificate in certificates)
            {
                CertAddCertificateContextToStore(store, certificate.Handle, CERT_STORE_ADD_ALWAYS, IntPtr.Zero);
            }
            IntPtr selected = CryptUIDlgSelectCertificateFromStore(store, owner, title, message, 0, 0, IntPtr.Zero);
            if (selected == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return new X509Certificate2(selected).Thumbprint;
            }
            finally
            {
                CertFreeCertificateContext(selected);
            }
        }
        finally
        {
            CertCloseStore(store, 0);
        }
    }
}
