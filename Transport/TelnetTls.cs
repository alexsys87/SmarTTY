using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;

namespace SmarTTY.Transport;

/// <summary>
/// Telnet over TLS ("telnets", usually port 992): the TLS handshake is made right after the TCP connection is
/// established, before the first Telnet byte, and the Telnet protocol then runs inside the encrypted stream.
/// (The in-band STARTTLS option of Telnet, option 46, is a different thing and is not supported.)
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
    /// middle. The protocol version is the one the operating system prefers.
    /// </summary>
    public static Stream Authenticate(Stream inner, string targetHost, bool allowInvalidCertificate, int handshakeTimeoutMilliseconds = HandshakeTimeoutMilliseconds)
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
        SslStream ssl = new SslStream(inner, false, validation);
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
            ssl.AuthenticateAsClient(targetHost, null, SslProtocols.None, false);
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
