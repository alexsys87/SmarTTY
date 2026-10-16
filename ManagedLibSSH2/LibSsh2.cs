using System;
using System.Runtime.InteropServices;

namespace ManagedLibSSH2;

internal static class LibSsh2
{
    public enum SessionFlag2
    {
        SigPipe = 1,
        Compress
    }

    public struct KeyboardInteractivePrompt
    {
        public IntPtr Text;

        public int Length;

        [MarshalAs(UnmanagedType.I1)]
        public bool Echo;
    }

    public struct KeyboardInteractiveResponse
    {
        public IntPtr Text;

        public int Length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void KeyboardInteractiveCallback([MarshalAs(UnmanagedType.LPStr)] string text, int value2, [MarshalAs(UnmanagedType.LPStr)] string text2, int value3, int value4, [In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] KeyboardInteractivePrompt[] items, [In][Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] KeyboardInteractiveResponse[] items2, IntPtr handle);

    public struct ScpFileStat
    {
        public uint Dev;

        public ushort Ino;

        public ushort Mode;

        public short NLink;

        public short Uid;

        public short Gid;

        public uint Rdev;

        public long Size;

        public ulong AccessTime;

        public ulong ModifyTime;

        public ulong ChangeTime;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SessionCallback(IntPtr handle, IntPtr handle2, [MarshalAs(UnmanagedType.LPStr)] string text, int value2, IntPtr handle3);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int PublicKeySignCallback(IntPtr handle, out IntPtr handle2, out IntPtr handle3, [In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] byte[] buffer, int value2, IntPtr handle4);

    public enum DisconnectReason
    {
        HostNotAllowedToConnect = 1,
        ProtocolError,
        KeyExchangeFailed,
        Reserved,
        MacError,
        CompressionError,
        ServiceNotAvailable,
        ProtocolVersionNotSupported,
        HostKeyNotVerifiable,
        ConnectionLost,
        ByApplication,
        TooManyConnections,
        AuthCancelledByUser,
        NoMoreAuthMethodsAvailable,
        IllegalUserName
    }

    private static class Native32
    {

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_init(int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_session_init_ex(IntPtr handle, IntPtr handle2, IntPtr handle3, IntPtr handle4);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_free(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_handshake(IntPtr handle, IntPtr handle2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_keepalive_send(IntPtr handle, out int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_keepalive_config(IntPtr handle, int value2, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_flag(IntPtr handle, SessionFlag2 sessionFlag2, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_userauth_password_ex(IntPtr handle, string text, int value2, string text2, int value3, IntPtr handle2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_userauth_authenticated(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_channel_open_ex(IntPtr handle, string text, int value2, int value3, int value4, string text2, int value5);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_setenv_ex(IntPtr handle, string text, int value2, string text2, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_free(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_close(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_channel_process_startup(IntPtr handle, string text, int value2, string text2, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_eof(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_flush_ex(IntPtr handle, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_send_eof(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_wait_closed(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_wait_eof(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_get_exit_status(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_write_ex(IntPtr handle, int value2, IntPtr handle2, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_read_ex(IntPtr handle, int value2, IntPtr handle2, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_read_ex(IntPtr handle, int value2, [Out] byte[] buffer, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_channel_request_pty_ex(IntPtr handle, string text, int value2, string text2, int value3, int value4, int value5, int value6, int value7);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_request_pty_size_ex(IntPtr handle, int value2, int value3, int value4, int value5);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_block_directions(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_last_errno(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_set_blocking(IntPtr handle, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_alloc(IntPtr handle, IntPtr handle2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_session_hostkey(IntPtr handle, out int value2, out int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_userauth_keyboard_interactive_ex(IntPtr handle, string text, int value2, KeyboardInteractiveCallback callback);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_scp_recv2(IntPtr handle, string text, out ScpFileStat scpFileStat);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_scp_send64(IntPtr handle, string text, int value2, long value3, int value4, int value5);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_handle_extended_data2(IntPtr handle, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_callback_set(IntPtr handle, int value2, SessionCallback callback);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_enable_blocking_send(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_channel_x11_req_ex(IntPtr handle, int value2, string text, string text2, int value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern string libssh2_session_banner_get(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_stats_get(IntPtr handle, out long value2, out long value3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_userauth_publickey(IntPtr handle, string text, [In] byte[] buffer, int value2, PublicKeySignCallback callback, IntPtr handle2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_session_disconnect_ex(IntPtr handle, DisconnectReason disconnectReason, string text, string text2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_userauth_publickey_fromfile_ex(IntPtr handle, string text, int value2, string text2, string text3, string text4);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_userauth_list(IntPtr handle, string text, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_knownhost_init(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_knownhost_free(IntPtr handle);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_knownhost_readfile(IntPtr handle, string text, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_knownhost_writefile(IntPtr handle, string text, int value2);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern KnownHostCheckResult libssh2_knownhost_checkp(IntPtr handle, string text, int value2, IntPtr handle2, int value3, int value4, IntPtr handle3);

        [DllImport("libssh2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_knownhost_addc(IntPtr handle, string text, string text2, IntPtr handle2, int value2, string text3, int value3, int value4, IntPtr handle3);
    }

    private static class Native64
    {

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_init(int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_session_init_ex(IntPtr handle, IntPtr handle2, IntPtr handle3, IntPtr handle4);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_free(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_handshake(IntPtr handle, IntPtr handle2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_keepalive_send(IntPtr handle, out int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_keepalive_config(IntPtr handle, int value2, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_flag(IntPtr handle, SessionFlag2 sessionFlag2, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_userauth_password_ex(IntPtr handle, string text, int value2, string text2, int value3, IntPtr handle2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_userauth_authenticated(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_channel_open_ex(IntPtr handle, string text, int value2, int value3, int value4, string text2, int value5);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_setenv_ex(IntPtr handle, string text, int value2, string text2, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_free(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_close(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_channel_process_startup(IntPtr handle, string text, int value2, string text2, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_eof(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_flush_ex(IntPtr handle, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_send_eof(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_wait_closed(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_wait_eof(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_get_exit_status(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_write_ex(IntPtr handle, int value2, IntPtr handle2, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_read_ex(IntPtr handle, int value2, IntPtr handle2, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_read_ex(IntPtr handle, int value2, [Out] byte[] buffer, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_channel_request_pty_ex(IntPtr handle, string text, int value2, string text2, int value3, int value4, int value5, int value6, int value7);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_request_pty_size_ex(IntPtr handle, int value2, int value3, int value4, int value5);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_block_directions(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_session_last_errno(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_set_blocking(IntPtr handle, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_alloc(IntPtr handle, IntPtr handle2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_session_hostkey(IntPtr handle, out int value2, out int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_userauth_keyboard_interactive_ex(IntPtr handle, string text, int value2, KeyboardInteractiveCallback callback);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_scp_recv2(IntPtr handle, string text, out ScpFileStat scpFileStat);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_scp_send64(IntPtr handle, string text, int value2, long value3, int value4, int value5);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libssh2_channel_handle_extended_data2(IntPtr handle, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_callback_set(IntPtr handle, int value2, SessionCallback callback);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_enable_blocking_send(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_channel_x11_req_ex(IntPtr handle, int value2, string text, string text2, int value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern string libssh2_session_banner_get(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_session_stats_get(IntPtr handle, out long value2, out long value3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_userauth_publickey(IntPtr handle, string text, [In] byte[] buffer, int value2, PublicKeySignCallback callback, IntPtr handle2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_session_disconnect_ex(IntPtr handle, DisconnectReason disconnectReason, string text, string text2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_userauth_publickey_fromfile_ex(IntPtr handle, string text, int value2, string text2, string text3, string text4);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr libssh2_userauth_list(IntPtr handle, string text, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libssh2_knownhost_init(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libssh2_knownhost_free(IntPtr handle);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_knownhost_readfile(IntPtr handle, string text, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_knownhost_writefile(IntPtr handle, string text, int value2);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern KnownHostCheckResult libssh2_knownhost_checkp(IntPtr handle, string text, int value2, IntPtr handle2, int value3, int value4, IntPtr handle3);

        [DllImport("libssh2-64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int libssh2_knownhost_addc(IntPtr handle, string text, string text2, IntPtr handle2, int value2, string text3, int value3, int value4, IntPtr handle3);
    }

    private static readonly bool _is64Bit = IntPtr.Size == 8;

    public static IntPtr OpenSessionChannel(IntPtr handle)
    {
        return ChannelOpenEx(handle, "session", "session".Length, 262144, 32768, null, 0);
    }

    public static int Init(int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_init(value2);
        }
        return Native32.libssh2_init(value2);
    }

    public static IntPtr SessionInitEx(IntPtr handle, IntPtr handle2, IntPtr handle3, IntPtr handle4)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_init_ex(handle, handle2, handle3, handle4);
        }
        return Native32.libssh2_session_init_ex(handle, handle2, handle3, handle4);
    }

    public static int SessionFree(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_free(handle);
        }
        return Native32.libssh2_session_free(handle);
    }

    public static int SessionHandshake(IntPtr handle, IntPtr handle2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_handshake(handle, handle2);
        }
        return Native32.libssh2_session_handshake(handle, handle2);
    }

    public static int KeepaliveSend(IntPtr handle, out int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_keepalive_send(handle, out value2);
        }
        return Native32.libssh2_keepalive_send(handle, out value2);
    }

    public static void KeepaliveConfig(IntPtr handle, int value2, int value3)
    {
        if (_is64Bit)
        {
            Native64.libssh2_keepalive_config(handle, value2, value3);
        }
        else
        {
            Native32.libssh2_keepalive_config(handle, value2, value3);
        }
    }

    public static int SessionFlag(IntPtr handle, SessionFlag2 sessionFlag2, int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_flag(handle, sessionFlag2, value2);
        }
        return Native32.libssh2_session_flag(handle, sessionFlag2, value2);
    }

    public static int UserauthPasswordEx(IntPtr handle, string text, int value2, string text2, int value3, IntPtr handle2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_userauth_password_ex(handle, text, value2, text2, value3, handle2);
        }
        return Native32.libssh2_userauth_password_ex(handle, text, value2, text2, value3, handle2);
    }

    public static int UserauthAuthenticated(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_userauth_authenticated(handle);
        }
        return Native32.libssh2_userauth_authenticated(handle);
    }

    public static IntPtr ChannelOpenEx(IntPtr handle, string text, int value2, int value3, int value4, string text2, int value5)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_open_ex(handle, text, value2, value3, value4, text2, value5);
        }
        return Native32.libssh2_channel_open_ex(handle, text, value2, value3, value4, text2, value5);
    }

    public static int ChannelSetenvEx(IntPtr handle, string text, int value2, string text2, int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_setenv_ex(handle, text, value2, text2, value3);
        }
        return Native32.libssh2_channel_setenv_ex(handle, text, value2, text2, value3);
    }

    public static int ChannelFree(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_free(handle);
        }
        return Native32.libssh2_channel_free(handle);
    }

    public static int ChannelClose(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_close(handle);
        }
        return Native32.libssh2_channel_close(handle);
    }

    public static int ChannelProcessStartup(IntPtr handle, string text, int value2, string text2, int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_process_startup(handle, text, value2, text2, value3);
        }
        return Native32.libssh2_channel_process_startup(handle, text, value2, text2, value3);
    }

    public static int ChannelEof(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_eof(handle);
        }
        return Native32.libssh2_channel_eof(handle);
    }

    public static int ChannelFlushEx(IntPtr handle, int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_flush_ex(handle, value2);
        }
        return Native32.libssh2_channel_flush_ex(handle, value2);
    }

    public static int ChannelSendEof(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_send_eof(handle);
        }
        return Native32.libssh2_channel_send_eof(handle);
    }

    public static int ChannelWaitClosed(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_wait_closed(handle);
        }
        return Native32.libssh2_channel_wait_closed(handle);
    }

    public static int ChannelWaitEof(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_wait_eof(handle);
        }
        return Native32.libssh2_channel_wait_eof(handle);
    }

    public static int ChannelGetExitStatus(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_get_exit_status(handle);
        }
        return Native32.libssh2_channel_get_exit_status(handle);
    }

    public static int ChannelWriteEx(IntPtr handle, int value2, IntPtr handle2, int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_write_ex(handle, value2, handle2, value3);
        }
        return Native32.libssh2_channel_write_ex(handle, value2, handle2, value3);
    }

    public static int ChannelReadEx(IntPtr handle, int value2, IntPtr handle2, int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_read_ex(handle, value2, handle2, value3);
        }
        return Native32.libssh2_channel_read_ex(handle, value2, handle2, value3);
    }

    public static int ChannelReadEx(IntPtr handle, int value2, byte[] buffer, int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_read_ex(handle, value2, buffer, value3);
        }
        return Native32.libssh2_channel_read_ex(handle, value2, buffer, value3);
    }

    public static int ChannelRequestPtyEx(IntPtr handle, string text, int value2, string text2, int value3, int value4, int value5, int value6, int value7)
    {
        if (LibSsh2._is64Bit)
        {
            return Native64.libssh2_channel_request_pty_ex(handle, text, value2, text2, value3, value4, value5, value6, value7);
        }
        return Native32.libssh2_channel_request_pty_ex(handle, text, value2, text2, value3, value4, value5, value6, value7);
    }

    public static int ChannelRequestPtySizeEx(IntPtr handle, int value2, int value3, int value4, int value5)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_request_pty_size_ex(handle, value2, value3, value4, value5);
        }
        return Native32.libssh2_channel_request_pty_size_ex(handle, value2, value3, value4, value5);
    }

    public static int SessionBlockDirections(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_block_directions(handle);
        }
        return Native32.libssh2_session_block_directions(handle);
    }

    public static int SessionLastErrno(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_last_errno(handle);
        }
        return Native32.libssh2_session_last_errno(handle);
    }

    public static void SessionSetBlocking(IntPtr handle, int value2)
    {
        if (_is64Bit)
        {
            Native64.libssh2_session_set_blocking(handle, value2);
        }
        else
        {
            Native32.libssh2_session_set_blocking(handle, value2);
        }
    }

    public static IntPtr Alloc(IntPtr handle, IntPtr handle2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_alloc(handle, handle2);
        }
        return Native32.libssh2_alloc(handle, handle2);
    }

    public static IntPtr SessionHostkey(IntPtr handle, out int value2, out int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_hostkey(handle, out value2, out value3);
        }
        return Native32.libssh2_session_hostkey(handle, out value2, out value3);
    }

    public static int UserauthKeyboardInteractiveEx(IntPtr handle, string text, int value2, KeyboardInteractiveCallback callback)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_userauth_keyboard_interactive_ex(handle, text, value2, callback);
        }
        return Native32.libssh2_userauth_keyboard_interactive_ex(handle, text, value2, callback);
    }

    public static IntPtr ScpRecv2(IntPtr handle, string text, out ScpFileStat scpFileStat)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_scp_recv2(handle, text, out scpFileStat);
        }
        return Native32.libssh2_scp_recv2(handle, text, out scpFileStat);
    }

    public static IntPtr ScpSend64(IntPtr handle, string text, int value2, long value3, int value4, int value5)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_scp_send64(handle, text, value2, value3, value4, value5);
        }
        return Native32.libssh2_scp_send64(handle, text, value2, value3, value4, value5);
    }

    public static int ChannelHandleExtendedData2(IntPtr handle, int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_handle_extended_data2(handle, value2);
        }
        return Native32.libssh2_channel_handle_extended_data2(handle, value2);
    }

    public static void SessionCallbackSet(IntPtr handle, int value2, SessionCallback callback)
    {
        if (_is64Bit)
        {
            Native64.libssh2_session_callback_set(handle, value2, callback);
        }
        else
        {
            Native32.libssh2_session_callback_set(handle, value2, callback);
        }
    }

    public static void SessionEnableBlockingSend(IntPtr handle)
    {
        if (_is64Bit)
        {
            Native64.libssh2_session_enable_blocking_send(handle);
        }
        else
        {
            Native32.libssh2_session_enable_blocking_send(handle);
        }
    }

    public static int ChannelX11ReqEx(IntPtr handle, int value2, string text, string text2, int value3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_channel_x11_req_ex(handle, value2, text, text2, value3);
        }
        return Native32.libssh2_channel_x11_req_ex(handle, value2, text, text2, value3);
    }

    public static string SessionBannerGet(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_banner_get(handle);
        }
        return Native32.libssh2_session_banner_get(handle);
    }

    public static void SessionStatsGet(IntPtr handle, out long value2, out long value3)
    {
        if (_is64Bit)
        {
            Native64.libssh2_session_stats_get(handle, out value2, out value3);
        }
        else
        {
            Native32.libssh2_session_stats_get(handle, out value2, out value3);
        }
    }

    public static int UserauthPublickey(IntPtr handle, string text, byte[] buffer, int value2, PublicKeySignCallback callback, IntPtr handle2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_userauth_publickey(handle, text, buffer, value2, callback, handle2);
        }
        return Native32.libssh2_userauth_publickey(handle, text, buffer, value2, callback, handle2);
    }

    public static int SessionDisconnectEx(IntPtr handle, DisconnectReason disconnectReason, string text, string text2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_session_disconnect_ex(handle, disconnectReason, text, text2);
        }
        return Native32.libssh2_session_disconnect_ex(handle, disconnectReason, text, text2);
    }

    public static int UserauthPublickeyFromfileEx(IntPtr handle, string text, int value2, string text2, string text3, string text4)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_userauth_publickey_fromfile_ex(handle, text, value2, text2, text3, text4);
        }
        return Native32.libssh2_userauth_publickey_fromfile_ex(handle, text, value2, text2, text3, text4);
    }

    public static IntPtr UserauthList(IntPtr handle, string text, int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_userauth_list(handle, text, value2);
        }
        return Native32.libssh2_userauth_list(handle, text, value2);
    }

    public static IntPtr KnownhostInit(IntPtr handle)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_knownhost_init(handle);
        }
        return Native32.libssh2_knownhost_init(handle);
    }

    public static void KnownhostFree(IntPtr handle)
    {
        if (_is64Bit)
        {
            Native64.libssh2_knownhost_free(handle);
        }
        else
        {
            Native32.libssh2_knownhost_free(handle);
        }
    }

    public static int KnownhostReadfile(IntPtr handle, string text, int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_knownhost_readfile(handle, text, value2);
        }
        return Native32.libssh2_knownhost_readfile(handle, text, value2);
    }

    public static int KnownhostWritefile(IntPtr handle, string text, int value2)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_knownhost_writefile(handle, text, value2);
        }
        return Native32.libssh2_knownhost_writefile(handle, text, value2);
    }

    public static KnownHostCheckResult KnownhostCheckp(IntPtr handle, string text, int value2, IntPtr handle2, int value3, int value4, IntPtr handle3)
    {
        if (_is64Bit)
        {
            return Native64.libssh2_knownhost_checkp(handle, text, value2, handle2, value3, value4, handle3);
        }
        return Native32.libssh2_knownhost_checkp(handle, text, value2, handle2, value3, value4, handle3);
    }

    public static int KnownhostAddc(IntPtr handle, string text, string text2, IntPtr handle2, int value2, string text3, int value3, int value4, IntPtr handle3)
    {
        if (LibSsh2._is64Bit)
        {
            return Native64.libssh2_knownhost_addc(handle, text, text2, handle2, value2, text3, value3, value4, handle3);
        }
        return Native32.libssh2_knownhost_addc(handle, text, text2, handle2, value2, text3, value3, value4, handle3);
    }
}
