using System;

namespace ManagedLibSSH2;

internal class LibSsh2Exception : Exception
{
    private int _errorCode;

    public LibSsh2Exception(int value2)
        : base("LIBSSH2 returned " + LibSsh2Exception.GetErrorName(value2))
    {
        _errorCode = value2;
    }

    public LibSsh2Exception(int value2, string text)
        : base(LibSsh2Exception.GetErrorName(value2) + text)
    {
        this._errorCode = value2;
    }

    public static string GetErrorName(int value2)
    {
        return value2 switch
        {
            -1 => "LIBSSH2_ERROR_SOCKET_NONE",
            -2 => "LIBSSH2_ERROR_BANNER_RECV",
            -3 => "LIBSSH2_ERROR_BANNER_SEND",
            -4 => "LIBSSH2_ERROR_INVALID_MAC",
            -5 => "LIBSSH2_ERROR_KEX_FAILURE",
            -6 => "LIBSSH2_ERROR_ALLOC",
            -7 => "LIBSSH2_ERROR_SOCKET_SEND",
            -8 => "LIBSSH2_ERROR_KEY_EXCHANGE_FAILURE",
            -9 => "LIBSSH2_ERROR_TIMEOUT",
            -10 => "LIBSSH2_ERROR_HOSTKEY_INIT",
            -11 => "LIBSSH2_ERROR_HOSTKEY_SIGN",
            -12 => "LIBSSH2_ERROR_DECRYPT",
            -13 => "LIBSSH2_ERROR_SOCKET_DISCONNECT",
            -14 => "LIBSSH2_ERROR_PROTO",
            -15 => "LIBSSH2_ERROR_PASSWORD_EXPIRED",
            -16 => "LIBSSH2_ERROR_FILE",
            -17 => "LIBSSH2_ERROR_METHOD_NONE",
            -18 => "LIBSSH2_ERROR_AUTHENTICATION_FAILED",
            -19 => "LIBSSH2_ERROR_PUBLICKEY_UNVERIFIED",
            -20 => "LIBSSH2_ERROR_CHANNEL_OUTOFORDER",
            -21 => "LIBSSH2_ERROR_CHANNEL_FAILURE",
            -22 => "LIBSSH2_ERROR_CHANNEL_REQUEST_DENIED",
            -23 => "LIBSSH2_ERROR_CHANNEL_UNKNOWN",
            -24 => "LIBSSH2_ERROR_CHANNEL_WINDOW_EXCEEDED",
            -25 => "LIBSSH2_ERROR_CHANNEL_PACKET_EXCEEDED",
            -26 => "LIBSSH2_ERROR_CHANNEL_CLOSED",
            -27 => "LIBSSH2_ERROR_CHANNEL_EOF_SENT",
            -28 => "LIBSSH2_ERROR_SCP_PROTOCOL",
            -29 => "LIBSSH2_ERROR_ZLIB",
            -30 => "LIBSSH2_ERROR_SOCKET_TIMEOUT",
            -31 => "LIBSSH2_ERROR_SFTP_PROTOCOL",
            -32 => "LIBSSH2_ERROR_REQUEST_DENIED",
            -33 => "LIBSSH2_ERROR_METHOD_NOT_SUPPORTED",
            -34 => "LIBSSH2_ERROR_INVAL",
            -35 => "LIBSSH2_ERROR_INVALID_POLL_TYPE",
            -36 => "LIBSSH2_ERROR_PUBLICKEY_PROTOCOL",
            -37 => "LIBSSH2_ERROR_EAGAIN",
            -38 => "LIBSSH2_ERROR_BUFFER_TOO_SMALL",
            -39 => "LIBSSH2_ERROR_BAD_USE",
            -40 => "LIBSSH2_ERROR_COMPRESS",
            -41 => "LIBSSH2_ERROR_OUT_OF_BOUNDARY",
            -42 => "LIBSSH2_ERROR_AGENT_PROTOCOL",
            -43 => "LIBSSH2_ERROR_SOCKET_RECV",
            -44 => "LIBSSH2_ERROR_ENCRYPT",
            -45 => "LIBSSH2_ERROR_BAD_SOCKET",
            _ => $"LIBSSH2 Error {value2}",
        };
    }
}
