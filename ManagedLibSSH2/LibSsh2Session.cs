using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ManagedLibSSH2;

public class LibSsh2Session : IDisposable
{
    public struct ChannelDelaySettings
    {
        public int MinimalDelayMs;

        public int ExtraDelayMs;

        public static ChannelDelaySettings Default = new ChannelDelaySettings
        {
            MinimalDelayMs = 500
        };
    }

    [Flags]
    public enum AuthMethods
    {
        None = 0,
        Password = 1,
        KeyboardInteractive = 2,
        PublicKey = 4
    }

    public struct KeyboardInteractivePrompt
    {
        public string Text;

        public bool Echo;

        public override string ToString()
        {
            return Text;
        }
    }

    public delegate void KeyboardInteractiveHandler(string text, string text2, KeyboardInteractivePrompt[] items, string[] items2);

    internal delegate _Type LIBSSH2Action<_Type>();

    private class SocketWaitCoordinator
    {
        private class EventPool
        {
            private LinkedList<ManualResetEvent> _pool = new LinkedList<ManualResetEvent>();

            public ManualResetEvent Acquire()
            {
                LinkedListNode<ManualResetEvent> first = _pool.First;
                if (first != null)
                {
                    _pool.RemoveFirst();
                    first.Value.Reset();
                    return first.Value;
                }
                return new ManualResetEvent(initialState: false);
            }

            public void Release(ManualResetEvent manualResetEvent)
            {
                _pool.AddLast(manualResetEvent);
            }
        }

        public interface ISocketWait : IDisposable
        {
            void Wait();
        }

        private class SocketPollWait : ISocketWait, IDisposable
        {
            private Socket _socket;

            private SelectMode _selectMode;

            public ManualResetEvent Event;

            private EventPool _pool;

            private int _refCount = 1;

            private readonly uint _pollRepeatCount;

            public SocketPollWait(Socket socket, SelectMode selectMode, EventPool eventPool, uint value2)
            {
                _socket = socket;
                this._selectMode = selectMode;
                this._pool = eventPool;
                _pollRepeatCount = value2;
                this.Event = this._pool.Acquire();
            }

            public void Wait()
            {
                try
                {
                    for (uint num = 0u; _pollRepeatCount == 0 || num < _pollRepeatCount; num++)
                    {
                        if (_socket.Poll(1000000, _selectMode))
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    Event.Set();
                }
            }

            public void AddRef()
            {
                Interlocked.Increment(ref _refCount);
            }

            public void ReleaseRef()
            {
                if (Interlocked.Decrement(ref _refCount) == 0)
                {
                    _pool.Release(Event);
                }
            }

            public void Dispose()
            {
                ReleaseRef();
            }
        }

        private class SocketWaitReference : ISocketWait, IDisposable
        {
            private SocketPollWait _target;

            public SocketWaitReference(SocketPollWait socketPollWait)
            {
                this._target = socketPollWait;
                socketPollWait.AddRef();
            }

            public void Wait()
            {
                _target.Event.WaitOne();
            }

            public void Dispose()
            {
                _target.ReleaseRef();
            }
        }

        private SelectMode _selectMode;

        private EventPool _pool = new EventPool();

        private SocketPollWait _current;

        public SocketWaitCoordinator(SelectMode selectMode)
        {
            _selectMode = selectMode;
        }

        public ISocketWait BeginWait(Socket socket, uint value2)
        {
            if (_current == null)
            {
                return _current = new SocketPollWait(socket, _selectMode, this._pool, value2);
            }
            return new SocketWaitReference(_current);
        }

        public void EndWait(ISocketWait socketWait)
        {
            if (socketWait is SocketPollWait)
            {
                _current = null;
            }
            socketWait.Dispose();
        }
    }

    private class NativeBuffer : IDisposable
    {
        public readonly IntPtr Pointer;

        public NativeBuffer(int value2)
        {
            this.Pointer = Marshal.AllocCoTaskMem(value2);
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    public class HostKeyInfo
    {
        public string Algorithm;

        public byte[] KeyData;
    }

    public struct SessionStats
    {
        public long BytesReceived;

        public long BytesSent;
    }

    private static int _initState;

    private static Mutex _initMutex = new Mutex(initiallyOwned: false, "B82BC5ED-2D5A-4622-81AB-91CB6EC77781-" + Process.GetCurrentProcess().Id);

    protected Socket _socket;

    protected IntPtr _sessionHandle;

    private readonly object _sessionLock = new object();

    private uint _pollRepeatCount = 1u;

    public readonly string HostName;

    public readonly int Port;

    public Func<string> tempDirectoryProvider;

    public ChannelDelaySettings DelaySettings = ChannelDelaySettings.Default;

    protected bool _x11Enabled;

    private object _channelOpenLock = new object();

    private SocketWaitCoordinator _socketWaiter = new SocketWaitCoordinator(SelectMode.SelectRead);

    private object _waiterLock = new object();

    public bool UseCatInsteadOfScp;

    public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    private int _connectionLostRaised;

    internal LinkedList<IChannelPoller> _pollers = new LinkedList<IChannelPoller>();

    protected bool _pollersChanged;

    protected ManualResetEvent _pollerEvent = new ManualResetEvent(initialState: false);

    private bool _disposeRequested;

    private DateTime _lastPtyRequestTime;

    private bool _keepAliveEnabled;

    public string UserName { get; private set; }

    public uint PollRepeatCount
    {
        get
        {
            return _pollRepeatCount;
        }
        set
        {
            _pollRepeatCount = value;
        }
    }

    private bool IsSendBlocked => (LibSsh2.SessionBlockDirections(_sessionHandle) & 2) != 0;

    public string Banner
    {
        get
        {
            lock (_sessionLock)
            {
                return LibSsh2.SessionBannerGet(_sessionHandle);
            }
        }
    }

    public static string UserSshDirectory
    {
        get
        {
            StringBuilder stringBuilder = new StringBuilder(260);
            SHGetSpecialFolderPath(IntPtr.Zero, stringBuilder, 40, flag: false);
            return Path.Combine(stringBuilder.ToString(), ".ssh");
        }
    }

    public SessionStats Statistics
    {
        get
        {
            SessionStats result = default;
            LibSsh2.SessionStatsGet(_sessionHandle, out result.BytesReceived, out result.BytesSent);
            return result;
        }
    }

    internal object PollersSyncRoot => _pollers;

    public int PollerCount
    {
        get
        {
            lock (_pollers)
            {
                return _pollers.Count;
            }
        }
    }

    public bool KeepAliveEnabled => _keepAliveEnabled;

    public event EventHandler ConnectionLost;

    public string GetTempPath(string text)
    {
        return (tempDirectoryProvider?.Invoke() ?? "/tmp") + "/" + text;
    }

    internal void StartShellOrExec(IntPtr handle, string text)
    {
        int num = ((text != null) ? ExecuteBlocking(() => LibSsh2.ChannelProcessStartup(handle, "exec", "exec".Length, text, text.Length)) : ExecuteBlocking(() => LibSsh2.ChannelProcessStartup(handle, "shell", "shell".Length, null, 0)));
        if (num < 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public LibSsh2Session(Socket socket, string text = null, int value2 = 22, bool flag = true)
    {
        HostName = text;
        Port = value2;
        _initMutex.WaitOne();
        try
        {
            if (Interlocked.Exchange(ref _initState, 1) == 0)
            {
                InitializeLibrary();
            }
        }
        finally
        {
            _initMutex.ReleaseMutex();
        }
        socket.NoDelay = true;
        _socket = socket;
        _sessionHandle = LibSsh2.SessionInitEx(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_sessionHandle == IntPtr.Zero)
        {
            throw new Exception("Cannot allocate a LIBSSH2 session");
        }
        if (flag)
        {
            LibSsh2.SessionFlag(_sessionHandle, LibSsh2.SessionFlag2.Compress, 1);
        }
        LibSsh2.SessionSetBlocking(_sessionHandle, 0);
        LibSsh2.SessionEnableBlockingSend(_sessionHandle);
        int num = ExecuteBlocking(() => LibSsh2.SessionHandshake(_sessionHandle, _socket.Handle));
        if (num != 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    public int SendKeepAlive()
    {
        int value2 = 0;
        if (!_keepAliveEnabled)
        {
            return 0;
        }
        int num = ExecuteBlocking(() => LibSsh2.KeepaliveSend(_sessionHandle, out value2));
        if (num != 0)
        {
            throw new LibSsh2Exception(num);
        }
        return value2;
    }

    private static void InitializeLibrary()
    {
        int num = LibSsh2.Init(0);
        if (num == 0)
        {
            return;
        }
        throw new Exception("Cannot initialize LIBSSH2: error " + num);
    }

    public bool Authenticate(string text, string text2)
    {
        AuthMethods authMethods = GetAuthMethods(text);
        if (authMethods == AuthMethods.None)
        {
            if (ExecuteBlocking(() => LibSsh2.UserauthAuthenticated(_sessionHandle)) == 1)
            {
                return true;
            }
            throw new Exception("The server reported no supported authentication protocols");
        }
        if ((authMethods & AuthMethods.Password) != 0)
        {
            return AuthenticateWithPassword(text, text2);
        }
        if ((authMethods & AuthMethods.KeyboardInteractive) != 0)
        {
            return AuthenticateKeyboardInteractiveWithPassword(text, text2);
        }
        if (!AuthenticateWithPassword(text, text2))
        {
            return AuthenticateKeyboardInteractiveWithPassword(text, text2);
        }
        return true;
    }

    public bool AuthenticateWithPassword(string text, string text2)
    {
        int num = ExecuteBlocking(() => LibSsh2.UserauthPasswordEx(_sessionHandle, text, text.Length, text2, text2.Length, IntPtr.Zero));
        switch (num)
        {
            case -18:
                return false;
            default:
                throw new LibSsh2Exception(num);
            case 0:
                this.UserName = text;
                return true;
        }
    }

    public AuthMethods GetAuthMethods(string text2)
    {
        IntPtr intPtr = ExecuteForHandle(() => LibSsh2.UserauthList(_sessionHandle, text2, text2.Length), out var num);
        if (intPtr == IntPtr.Zero)
        {
            if (num != 0)
            {
                throw new LibSsh2Exception(num);
            }
            return AuthMethods.None;
        }
        string text = Marshal.PtrToStringAnsi(intPtr);
        AuthMethods authMethods = AuthMethods.None;
        if (text.Contains("password"))
        {
            authMethods |= AuthMethods.Password;
        }
        if (text.Contains("keyboard-interactive"))
        {
            authMethods |= AuthMethods.KeyboardInteractive;
        }
        if (text.Contains("publickey"))
        {
            authMethods |= AuthMethods.PublicKey;
        }
        return authMethods;
    }

    private bool TryAuthenticatePublicKey(string text, ISshSigner sshSigner)
    {
        if (sshSigner == null)
        {
            return false;
        }
        byte[] buffer = sshSigner.GetPublicKeyBlob();
        int num = ExecuteBlocking(() => LibSsh2.UserauthPublickey(_sessionHandle, text, buffer, buffer.Length, sshSigner.Sign, IntPtr.Zero));
        switch (num)
        {
            case -19:
            case -18:
                return false;
            default:
                throw new LibSsh2Exception(num);
            case 0:
                this.UserName = text;
                return true;
        }
    }

    public bool AuthenticateWithPublicKey(string text, ISshKeyPair sshKeyPair)
    {
        ISshSigner[] array = sshKeyPair.Signers;
        foreach (ISshSigner sshSigner in array)
        {
            if (TryAuthenticatePublicKey(text, sshSigner))
            {
                return true;
            }
        }
        return false;
    }

    public bool AuthenticateKeyboardInteractiveWithPassword(string text3, string text4)
    {
        return AuthenticateKeyboardInteractive(text3, (string text, string text2, KeyboardInteractivePrompt[] array2, string[] array) =>
        {
            array[0] = text4;
        });
    }

    public bool AuthenticateKeyboardInteractive(string text3, KeyboardInteractiveHandler callback)
    {
        int num = ExecuteBlocking(() => LibSsh2.UserauthKeyboardInteractiveEx(_sessionHandle, text3, text3.Length, (string text, int num5, string text2, int num6, int num2, LibSsh2.KeyboardInteractivePrompt[] array3, LibSsh2.KeyboardInteractiveResponse[] array4, IntPtr intPtr) =>
        {
            try
            {
                KeyboardInteractivePrompt[] array = new KeyboardInteractivePrompt[num2];
                string[] array2 = new string[num2];
                for (int num3 = 0; num3 < num2; num3++)
                {
                    array[num3].Echo = array3[num3].Echo;
                    array[num3].Text = Marshal.PtrToStringAnsi(array3[num3].Text, array3[num3].Length);
                }
                callback(text, text2, array, array2);
                for (int num4 = 0; num4 < num2; num4++)
                {
                    if (array2[num4] == null)
                    {
                        array4[num4].Length = 0;
                        array4[num4].Text = IntPtr.Zero;
                    }
                    else
                    {
                        array4[num4].Length = array2[num4].Length;
                        array4[num4].Text = LibSsh2.Alloc(_sessionHandle, (IntPtr)(array2[num4].Length + 1));
                        Marshal.Copy(Encoding.UTF8.GetBytes(array2[num4]), 0, array4[num4].Text, array2[num4].Length);
                        Marshal.WriteByte(new IntPtr((long)array4[num4].Text + array2[num4].Length), 0);
                    }
                }
            }
            catch
            {
            }
        }));
        switch (num)
        {
            case -18:
                return false;
            default:
                throw new LibSsh2Exception(num);
            case 0:
                this.UserName = text3;
                return true;
        }
    }

    internal IntPtr OpenChannelHandle()
    {
        lock (_channelOpenLock)
        {
            IntPtr intPtr = ExecuteForHandle(() => LibSsh2.OpenSessionChannel(_sessionHandle), out var num);
            if (intPtr == IntPtr.Zero)
            {
                throw new LibSsh2Exception(num);
            }
            return intPtr;
        }
    }

    public SshShellChannel OpenShellChannel(string text = null, string text2 = null, ExtendedDataMode extendedDataMode = ExtendedDataMode.Normal)
    {
        lock (_channelOpenLock)
        {
            IntPtr intPtr = ExecuteForHandle(() => LibSsh2.OpenSessionChannel(_sessionHandle), out var num);
            if (intPtr == IntPtr.Zero)
            {
                throw new LibSsh2Exception(num);
            }
            return new SshShellChannel(this, intPtr, text, text2, _x11Enabled, extendedDataMode);
        }
    }

    private int SetChannelEnvironment(IntPtr handle, string text, string text2)
    {
        return ExecuteBlocking(() => LibSsh2.ChannelSetenvEx(handle, text, text.Length, text2, text2.Length));
    }

    public ScpDownloadStream OpenDownload(string text2)
    {
        if (UseCatInsteadOfScp)
        {
            string[] array = (from text in RunCommandChecked("stat '" + text2 + "' -L -c \"%s %X %Y %W\"", flag: true) ?? throw new Exception("Missing or unreadable " + text2)
                              select text.Split(' ') into array2
                              where array2.Length == 4
                              select array2).FirstOrDefault() ?? throw new Exception("Unexpected output from stat on '" + text2 + "'. Please verify that the file exists.");
            LibSsh2.ScpFileStat scpFileStat = new LibSsh2.ScpFileStat
            {
                Size = long.Parse(array[0])
            };
            ulong.TryParse(array[1], out scpFileStat.AccessTime);
            ulong.TryParse(array[2], out scpFileStat.ModifyTime);
            ulong.TryParse(array[3], out scpFileStat.ChangeTime);
            IntPtr intPtr = OpenChannelHandle();
            StartShellOrExec(intPtr, "cat '" + text2 + "'");
            return new ScpDownloadStream(this, intPtr, scpFileStat);
        }
        LibSsh2.ScpFileStat scpFileStat2 = default;
        int num = 0;
        IntPtr intPtr2 = IntPtr.Zero;
        for (int num2 = 0; num2 < 5; num2++)
        {
            intPtr2 = ExecuteForHandle(() => LibSsh2.ScpRecv2(_sessionHandle, text2, out scpFileStat2), out num);
            if (intPtr2 != IntPtr.Zero || num != -1)
            {
                break;
            }
        }
        if (intPtr2 == IntPtr.Zero)
        {
            throw new LibSsh2Exception(num, " while trying to download " + text2);
        }
        return new ScpDownloadStream(this, intPtr2, scpFileStat2);
    }

    public void Disconnect(string text = null)
    {
        int num = ExecuteBlocking(() => LibSsh2.SessionDisconnectEx(_sessionHandle, LibSsh2.DisconnectReason.ByApplication, text, null));
        if (num < 0)
        {
            throw new LibSsh2Exception(num);
        }
    }

    private static Socket[] FilterSockets(int value2, int value3, Socket[] items)
    {
        if ((value2 & value3) != 0)
        {
            return items;
        }
        return null;
    }

    internal IntPtr ExecuteForHandle(LIBSSH2Action<IntPtr> callback, out int value2)
    {
        IntPtr handle = IntPtr.Zero;
        LIBSSH2Action<int> lIBSSH2Action = () =>
        {
            handle = callback();
            return (!(handle != IntPtr.Zero)) ? LibSsh2.SessionLastErrno(_sessionHandle) : 0;
        };
        value2 = ExecuteBlocking(lIBSSH2Action);
        return handle;
    }

    private int ExecuteOnce(LIBSSH2Action<int> callback, out int value2)
    {
        value2 = 0;
        int num = callback();
        if (num != -37)
        {
            return num;
        }
        value2 = GetBlockDirections();
        if ((value2 & 2) != 0)
        {
            throw new Exception("Unexpected LIBSSH2 behavior: outbound direction should never be blocked with our patch!");
        }
        return num;
    }

    internal int GetBlockDirections()
    {
        return LibSsh2.SessionBlockDirections(_sessionHandle);
    }

    internal int ExecuteBlocking(LIBSSH2Action<int> callback)
    {
        while (true)
        {
            SocketWaitCoordinator.ISocketWait socketWait = null;
            lock (_waiterLock)
            {
                int num2;
                lock (_sessionLock)
                {
                    int num = ExecuteOnce(callback, out num2);
                    switch (num)
                    {
                        case -43:
                            OnConnectionLost();
                            break;
                        case -37:
                            goto end_IL_001c;
                    }
                    return num;
                end_IL_001c:;
                }
                if ((num2 & 1) != 0)
                {
                    socketWait = _socketWaiter.BeginWait(_socket, _pollRepeatCount);
                }
            }
            socketWait?.Wait();
            lock (_waiterLock)
            {
                if (socketWait != null)
                {
                    _socketWaiter.EndWait(socketWait);
                }
            }
        }
    }

    internal int ExecuteNonBlocking(LIBSSH2Action<int> callback)
    {
        lock (_sessionLock)
        {
            int num;
            return ExecuteOnce(callback, out num);
        }
    }

    internal int ReadChannel(IntPtr handle, int value2, byte[] buffer, int value3, int value4)
    {
        NativeBuffer nativeBuffer = new NativeBuffer(value4);
        try
        {
            int num = ExecuteBlocking(() => LibSsh2.ChannelReadEx(handle, value2, nativeBuffer.Pointer, value4));
            if (num < 0)
            {
                throw new LibSsh2Exception(num);
            }
            Marshal.Copy(nativeBuffer.Pointer, buffer, value3, value4);
            return num;
        }
        finally
        {
            if (nativeBuffer != null)
            {
                ((IDisposable)nativeBuffer).Dispose();
            }
        }
    }

    internal int ReadChannelRange(IntPtr handle, int value2, int value3, byte[] buffer, int value4, int value5, out int value6)
    {
        NativeBuffer nativeBuffer = new NativeBuffer(value5);
        try
        {
            int value7 = Math.Sign(value3 - value2);
            if (value7 == 0)
            {
                value7 = 1;
            }
            int value8 = -1;
            int num = ExecuteBlocking(() =>
            {
                bool flag = true;
                for (int num2 = value2; num2 != value3 + value7; num2 += value7)
                {
                    int num3 = LibSsh2.ChannelReadEx(handle, num2, nativeBuffer.Pointer, value5);
                    if (num3 != 0)
                    {
                        flag = false;
                    }
                    if (num3 != -37 && num3 != 0)
                    {
                        value8 = num2;
                        return num3;
                    }
                }
                return (!flag) ? (-37) : 0;
            });
            if (num < 0)
            {
                throw new LibSsh2Exception(num);
            }
            Marshal.Copy(nativeBuffer.Pointer, buffer, value4, value5);
            value6 = value8;
            return num;
        }
        finally
        {
            if (nativeBuffer != null)
            {
                ((IDisposable)nativeBuffer).Dispose();
            }
        }
    }

    internal int ReadChannelNoWait(IntPtr handle, int value2, byte[] buffer)
    {
        return LibSsh2.ChannelReadEx(handle, value2, buffer, buffer.Length);
    }

    internal int WriteChannel(IntPtr handle, int value2, byte[] buffer, int value3, int value4)
    {
        NativeBuffer nativeBuffer = new NativeBuffer(value4);
        try
        {
            Marshal.Copy(buffer, value3, nativeBuffer.Pointer, value4);
            int num = ExecuteBlocking(() => LibSsh2.ChannelWriteEx(handle, value2, nativeBuffer.Pointer, value4));
            if (num < 0)
            {
                throw new LibSsh2Exception(num);
            }
            return num;
        }
        finally
        {
            if (nativeBuffer != null)
            {
                ((IDisposable)nativeBuffer).Dispose();
            }
        }
    }

    internal void WriteChannelFully(IntPtr handle, int value2, byte[] buffer, int value3, int value4)
    {
        int num;
        int num2;
        for (num = 0; num < value4; num += num2)
        {
            num2 = WriteChannel(handle, 0, buffer, value3 + num, value4 - num);
            if (num2 < 0)
            {
                throw new LibSsh2Exception(num2);
            }
        }
        if (num != value4)
        {
            throw new IOException("Incomplete SSH send()");
        }
    }

    public List<string> RunCommand(string text2, out int? value2, string text3 = null)
    {
        try
        {
            using SshShellChannel sshShellChannel = OpenShellChannel(text2, text3, ExtendedDataMode.Merge);
            List<string> list = new List<string>();
            using (StreamReader streamReader = new StreamReader(sshShellChannel.Output))
            {
                while (true)
                {
                    string text = streamReader.ReadLine();
                    if (text != null)
                    {
                        list.Add(text);
                        continue;
                    }
                    break;
                }
            }
            sshShellChannel.WaitClosed();
            value2 = sshShellChannel.ExitStatus;
            return list;
        }
        catch
        {
            value2 = null;
            return null;
        }
    }

    public List<string> RunCommandChecked(string text, bool flag, string text2 = null)
    {
        List<string> result = RunCommand(text, out var num, text2);
        if (flag && num != 0)
        {
            return null;
        }
        return result;
    }

    public ScpChannelStream OpenUpload(string text, long value2, DateTime dateTime = default(DateTime), int? value3 = null)
    {
        if (dateTime.Equals(default))
        {
            dateTime = DateTime.Now;
        }
        if (UseCatInsteadOfScp)
        {
            IntPtr intPtr = OpenChannelHandle();
            StartShellOrExec(intPtr, "cat > '" + text + "'");
            return new ScpChannelStream(this, intPtr, flag: false, value2);
        }
        int value4 = (int)(dateTime - UnixEpoch).TotalSeconds;
        IntPtr intPtr2 = ExecuteForHandle(() => LibSsh2.ScpSend64(_sessionHandle, text, value3 ?? Convert.ToInt32("644", 8), value2, value4, value4), out var num);
        if (intPtr2 == IntPtr.Zero)
        {
            throw DiagnoseUploadError(text) ?? new LibSsh2Exception(num, " while trying to upload " + text);
        }
        return new ScpChannelStream(this, intPtr2, flag: false, value2);
    }

    private Exception DiagnoseUploadError(string text)
    {
        try
        {
            RemoteFileContext remoteFileContext = new RemoteFileContext(text, HostName, this.UserName);
            if (string.IsNullOrEmpty(text))
            {
                return new RemotePathException(remoteFileContext, RemotePathProblem.EmptyPath);
            }
            if (text.TrimEnd(' ').EndsWith("/"))
            {
                return new RemotePathException(remoteFileContext, RemotePathProblem.EndsWithSlash);
            }
            if (!text.StartsWith("/"))
            {
                return new RemotePathException(remoteFileContext, RemotePathProblem.NotAbsolute);
            }
            if (RunCommandChecked("test -e \"" + text + "\"", flag: true) != null)
            {
                if (RunCommandChecked("test -w \"" + text + "\"", flag: true) == null)
                {
                    return new RemotePathException(remoteFileContext, RemotePathProblem.FileNotWritable);
                }
            }
            else if (RunCommandChecked("test -w \"" + RemotePathUtils.GetDirectoryName(text) + "\"", flag: true) == null)
            {
                return new RemotePathException(remoteFileContext, RemotePathProblem.DirectoryNotWritable);
            }
        }
        catch
        {
        }
        return null;
    }

    [DllImport("shell32.dll")]
    private static extern bool SHGetSpecialFolderPath(IntPtr handle, [Out] StringBuilder stringBuilder, int value2, bool flag);

    public bool AuthenticateWithKeyFile(string text, out bool flag, string text2 = null, string text3 = null)
    {
        if (string.IsNullOrEmpty(text2))
        {
            text2 = Path.Combine(UserSshDirectory, "id_dsa");
        }
        if (!File.Exists(text2))
        {
            throw new IOException(text2 + " does not exist.");
        }
        int num = ExecuteBlocking(() => LibSsh2.UserauthPublickeyFromfileEx(_sessionHandle, text, text.Length, null, text2, text3));
        flag = num == -16;
        switch (num)
        {
            case -19:
            case -18:
            case -16:
                return false;
            default:
                throw new LibSsh2Exception(num);
            case 0:
                this.UserName = text;
                return true;
        }
    }

    public HostKeyInfo GetHostKey()
    {
        IntPtr intPtr = IntPtr.Zero;
        int num;
        int num2;
        lock (_sessionLock)
        {
            intPtr = LibSsh2.SessionHostkey(_sessionHandle, out num, out num2);
            if (intPtr == IntPtr.Zero)
            {
                throw new LibSsh2Exception(LibSsh2.SessionLastErrno(_sessionHandle));
            }
        }
        HostKeyInfo hostKeyInfo = new HostKeyInfo();
        hostKeyInfo.KeyData = new byte[num];
        Marshal.Copy(intPtr, hostKeyInfo.KeyData, 0, num);
        switch (num2)
        {
            case 1:
                hostKeyInfo.Algorithm = "ssh-rsa";
                break;
            case 2:
                hostKeyInfo.Algorithm = "ssh-dss";
                break;
            case 3:
                hostKeyInfo.Algorithm = "ecdsa-sha2-nistp256";
                break;
            default:
                hostKeyInfo.Algorithm = num2.ToString();
                break;
        }
        return hostKeyInfo;
    }

    public KnownHostCheckResult CheckHostKey(string text)
    {
        using KnownHostsFile knownHostsFile = new KnownHostsFile(_sessionHandle, text, flag: false);
        IntPtr intPtr;
        int num;
        lock (_sessionLock)
        {
            intPtr = LibSsh2.SessionHostkey(_sessionHandle, out num, out var _);
            if (intPtr == IntPtr.Zero)
            {
                throw new LibSsh2Exception(LibSsh2.SessionLastErrno(_sessionHandle));
            }
        }
        return knownHostsFile.CheckHost(HostName, Port, intPtr, num);
    }

    public void AddHostKeyToFile(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(text));
        using KnownHostsFile knownHostsFile = new KnownHostsFile(_sessionHandle, text, flag: false);
        IntPtr intPtr;
        int num;
        int num2;
        lock (_sessionLock)
        {
            intPtr = LibSsh2.SessionHostkey(_sessionHandle, out num, out num2);
            if (intPtr == IntPtr.Zero)
            {
                throw new LibSsh2Exception(LibSsh2.SessionLastErrno(_sessionHandle));
            }
        }
        knownHostsFile.AddHost(HostName, Port, intPtr, num, num2);
        knownHostsFile.Save();
    }

    internal void FreeChannel(ref IntPtr handle)
    {
        IntPtr handle2 = Interlocked.Exchange(ref handle, IntPtr.Zero);
        ExecuteBlocking(() => LibSsh2.ChannelFree(handle2));
    }

    protected virtual void OnConnectionLost()
    {
        if (Interlocked.Exchange(ref _connectionLostRaised, 1) == 0 && ConnectionLost != null)
        {
            ConnectionLost(this, EventArgs.Empty);
        }
    }

    internal object AddPoller(IChannelPoller channelPoller)
    {
        lock (_pollers)
        {
            LinkedListNode<IChannelPoller> result = _pollers.AddLast(channelPoller);
            _pollerEvent.Set();
            _pollersChanged = true;
            return result;
        }
    }

    internal void RemovePoller(object obj)
    {
        if (obj == null)
        {
            return;
        }
        bool flag = false;
        lock (_pollers)
        {
            _pollers.Remove((LinkedListNode<IChannelPoller>)obj);
            _pollersChanged = true;
            if (_pollers.Count == 0)
            {
                _pollerEvent.Reset();
                flag = _disposeRequested;
            }
        }
        if (flag)
        {
            Dispose();
        }
    }

    public virtual void Dispose()
    {
        if (_socket != null)
        {
            _socket.Close();
        }
        lock (_pollers)
        {
            _disposeRequested = true;
            if (_pollers.Count != 0)
            {
                foreach (IChannelPoller item in _pollers)
                {
                    if (item is X11ForwardChannel x11ForwardChannel)
                    {
                        x11ForwardChannel.Abort();
                    }
                }
                return;
            }
        }
        lock (_sessionLock)
        {
            if (_sessionHandle != IntPtr.Zero)
            {
                LibSsh2.SessionFree(_sessionHandle);
                _sessionHandle = IntPtr.Zero;
            }
        }
    }

    internal void MarkPtyRequested()
    {
        _lastPtyRequestTime = DateTime.Now;
    }

    internal void WaitBeforePtyRequest()
    {
        long num = (long)(DateTime.Now - _lastPtyRequestTime).TotalMilliseconds;
        if (num > 0 && num < DelaySettings.MinimalDelayMs)
        {
            Thread.Sleep((int)(DelaySettings.MinimalDelayMs - num));
        }
        if (DelaySettings.ExtraDelayMs > 0)
        {
            Thread.Sleep(DelaySettings.ExtraDelayMs);
        }
    }

    public void ConfigureKeepAlive(int value2, bool flag = false)
    {
        lock (_sessionLock)
        {
            LibSsh2.KeepaliveConfig(_sessionHandle, flag ? 1 : 0, value2);
        }
        _keepAliveEnabled = value2 != 0;
    }
}
