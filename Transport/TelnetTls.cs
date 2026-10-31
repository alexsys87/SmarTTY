using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>
/// Telnet over TLS. Two ways lead there: "telnets" (usually port 992), where the TLS handshake is made right after the
/// TCP connection is established, before the first Telnet byte, and STARTTLS (Telnet option 46), where the session
/// starts in clear text and both sides agree in-band to make the handshake (see <see cref="TelnetStream"/>). Either
/// way the Telnet protocol then runs inside the encrypted stream, and the handshake is the one made here.
/// </summary>
internal static class TelnetTls
{
    /// <summary>The port telnets servers listen on unless configured otherwise.</summary>
    public const int DefaultPort = 992;

    /// <summary>
    /// How long the server may stay silent during the handshake. A server that does not speak TLS (a plain telnet
    /// port) typically sends its banner, which fails at once, or sends nothing at all, which would wait forever.
    /// </summary>
    public const int HandshakeTimeoutMilliseconds = 15000;

    /// <summary>
    /// Makes the TLS handshake as a client over <paramref name="inner"/> and returns the encrypted stream (it owns
    /// <paramref name="inner"/> from then on). The server's certificate is checked against the trusted roots and
    /// against <paramref name="targetHost"/>; with <paramref name="allowInvalidCertificate"/> every certificate is
    /// accepted (self-signed ones of devices, for example), which gives no protection against an attacker in the
    /// middle. <paramref name="clientCertificate"/> (with its private key) is presented when the server asks for a
    /// client certificate. The protocol version is the one the operating system prefers.
    /// </summary>
    public static Stream Authenticate(Stream inner, string targetHost, bool allowInvalidCertificate, int handshakeTimeoutMilliseconds = HandshakeTimeoutMilliseconds,
        X509Certificate2 clientCertificate = null)
    {
        if (inner == null)
        {
            throw new ArgumentNullException(nameof(inner));
        }
        if (string.IsNullOrEmpty(targetHost))
        {
            throw new ArgumentException("The host name is needed to check the server's certificate.", nameof(targetHost));
        }
        RemoteCertificateValidationCallback validation = null;
        if (allowInvalidCertificate)
        {
            validation = (sender, certificate, chain, errors) => true;
        }
        LocalCertificateSelectionCallback selection = null;
        X509CertificateCollection clientCertificates = null;
        if (clientCertificate != null)
        {
            clientCertificates = new X509CertificateCollection { clientCertificate };
            // The default selection offers the certificate only when its issuer is in the server's list of acceptable
            // ones; a server that sends no list (many devices) would then get none. The user chose this one.
            selection = (sender, host, local, remote, acceptableIssuers) => clientCertificate;
        }
        SslStream ssl = new SslStream(inner, false, validation, selection);
        bool timeouts = inner.CanTimeout;
        int readTimeout = 0;
        int writeTimeout = 0;
        try
        {
            if (timeouts)
            {
                readTimeout = inner.ReadTimeout;
                writeTimeout = inner.WriteTimeout;
                inner.ReadTimeout = handshakeTimeoutMilliseconds;
                inner.WriteTimeout = handshakeTimeoutMilliseconds;
            }
            ssl.AuthenticateAsClient(targetHost, clientCertificates, SslProtocols.None, false);
            if (timeouts)
            {
                inner.ReadTimeout = readTimeout;
                inner.WriteTimeout = writeTimeout;
            }
            return ssl;
        }
        catch (Exception exception)
        {
            ssl.Dispose();
            if (exception is AuthenticationException || exception is IOException)
            {
                throw new IOException("The TLS handshake with " + targetHost + " failed: " + Describe(exception), exception);
            }
            throw;
        }
    }

    /// <summary>
    /// Finds the client certificate the user configured: the path of a .pfx / .p12 file without a password, or the
    /// thumbprint of a certificate in the personal store ("My") of the current user or of the computer, as the
    /// certificate dialog of Windows shows it (blanks and colons are ignored). Null for an empty text; throws
    /// <see cref="IOException"/> with a message for the user when the certificate cannot be used.
    /// </summary>
    public static X509Certificate2 LoadClientCertificate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.Trim().Trim('"');
        string thumbprint = NormalizeThumbprint(text);
        X509Certificate2 certificate;
        if (thumbprint != null && !File.Exists(text))
        {
            certificate = FindInStore(thumbprint, StoreLocation.CurrentUser) ?? FindInStore(thumbprint, StoreLocation.LocalMachine);
            if (certificate == null)
            {
                throw new IOException("The client certificate " + thumbprint + " is not in the personal certificate store of this user or computer.");
            }
        }
        else
        {
            try
            {
                certificate = new X509Certificate2(text, (string)null, X509KeyStorageFlags.UserKeySet);
            }
            catch (CryptographicException exception)
            {
                throw new IOException("The client certificate file " + text + " cannot be used: " + exception.Message +
                    " A file protected by a password has to be imported into the certificate store of Windows (open it with a double click); then select the certificate from the store.", exception);
            }
        }
        if (!certificate.HasPrivateKey)
        {
            string subject = certificate.Subject;
            certificate.Dispose();
            throw new IOException("The client certificate " + subject + " has no private key, so it cannot prove anything to the server.");
        }
        return certificate;
    }

    /// <summary>
    /// The thumbprint in the text (40 hexadecimal digits of SHA-1, or 64 of SHA-256), upper case, without the
    /// blanks, colons and invisible marks that a copy from the certificate dialog brings along; null when the text
    /// is something else.
    /// </summary>
    internal static string NormalizeThumbprint(string text)
    {
        StringBuilder digits = new StringBuilder();
        foreach (char c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                digits.Append(char.ToUpperInvariant(c));
            }
            else if (c != ' ' && c != ':' && c != '‎' && c != '‏' && c != '﻿')
            {
                return null;
            }
        }
        return digits.Length == 40 || digits.Length == 64 ? digits.ToString() : null;
    }

    private static X509Certificate2 FindInStore(string thumbprint, StoreLocation location)
    {
        X509Store store = new X509Store(StoreName.My, location);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            X509Certificate2 found = null;
            foreach (X509Certificate2 candidate in store.Certificates)
            {
                if (found == null && (string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)
                    || (thumbprint.Length == 64 && string.Equals(Sha256Thumbprint(candidate), thumbprint, StringComparison.Ordinal))))
                {
                    found = candidate;
                }
                else
                {
                    // Every certificate of the store is a copy with its own handles: only the one that is used stays.
                    candidate.Dispose();
                }
            }
            return found;
        }
        catch (CryptographicException)
        {
            // The store does not exist or cannot be opened: the certificate is not there.
            return null;
        }
        finally
        {
            store.Close();
        }
    }

    private static string Sha256Thumbprint(X509Certificate2 certificate)
    {
        using SHA256 sha = SHA256.Create();
        StringBuilder text = new StringBuilder();
        foreach (byte b in sha.ComputeHash(certificate.RawData))
        {
            text.Append(b.ToString("X2"));
        }
        return text.ToString();
    }

    private static string Describe(Exception exception)
    {
        // The interesting reason of an IOException is often the inner exception (a timeout, a reset).
        string text = exception.Message;
        if (exception.InnerException != null && !string.IsNullOrEmpty(exception.InnerException.Message) && !text.Contains(exception.InnerException.Message))
        {
            text += " (" + exception.InnerException.Message + ")";
        }
        return text;
    }
}

/// <summary>
/// A stream that first hands out some bytes that were already read from <see cref="Inner"/> and then reads on from it;
/// writing goes to it directly. STARTTLS uses it for the beginning of the TLS conversation that arrived together with
/// the server's last Telnet command.
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;

    private int _position;

    public PrefixedStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix ?? Array.Empty<byte>();
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Stream Inner { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => Inner.CanWrite;

    public override bool CanTimeout => Inner.CanTimeout;

    public override int ReadTimeout
    {
        get => Inner.ReadTimeout;
        set => Inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => Inner.WriteTimeout;
        set => Inner.WriteTimeout = value;
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position < _prefix.Length)
        {
            int taken = Math.Min(count, _prefix.Length - _position);
            Buffer.BlockCopy(_prefix, _position, buffer, offset, taken);
            _position += taken;
            return taken;
        }
        return Inner.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Inner.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        Inner.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
