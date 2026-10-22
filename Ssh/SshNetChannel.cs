using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages;
using Renci.SshNet.Messages.Connection;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SmarTTY.Ssh;

/// <summary>
/// Wrapper over the session channel of SSH.NET (Renci.SshNet.Channels.IChannelSession).
/// SSH.NET exposes only high-level classes (SshCommand without a PTY, ShellStream without exec),
/// while SmarTTY needs exec+PTY, separate stderr and the exit status on one channel.
/// The channel interface is internal in SSH.NET, so it is accessed via reflection; all lookups are
/// validated once and fail with a clear message if a future SSH.NET version changes them.
/// </summary>
internal sealed class SshNetChannel : IDisposable
{
    private static class Api
    {
        public static readonly PropertyInfo ClientSession;
        public static readonly MethodInfo CreateChannelSession;
        public static readonly MethodInfo Open;
        public static readonly MethodInfo SendPseudoTerminalRequest;
        public static readonly MethodInfo SendExecRequest;
        public static readonly MethodInfo SendShellRequest;
        public static readonly MethodInfo SendSubsystemRequest;
        public static readonly MethodInfo SendWindowChangeRequest;
        public static readonly MethodInfo SendX11ForwardingRequest;
        public static readonly MethodInfo SendData;
        public static readonly MethodInfo SendEof;
        public static readonly PropertyInfo DataEventData;
        public static readonly PropertyInfo RequestEventInfo;
        public static readonly MethodInfo SessionSendMessage;
        public static readonly PropertyInfo SessionNextChannelNumber;
        public static readonly Exception LoadError;

        static Api()
        {
            try
            {
                Assembly assembly = typeof(SshClient).Assembly;
                Type sessionType = GetType(assembly, "Renci.SshNet.ISession");
                Type channelType = GetType(assembly, "Renci.SshNet.Channels.IChannel");
                Type channelSessionType = GetType(assembly, "Renci.SshNet.Channels.IChannelSession");
                ClientSession = typeof(BaseClient).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw Missing("BaseClient.Session");
                CreateChannelSession = GetMethod(sessionType, "CreateChannelSession");
                Open = GetMethod(channelSessionType, "Open");
                SendPseudoTerminalRequest = GetMethod(channelSessionType, "SendPseudoTerminalRequest", typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<TerminalModes, uint>));
                SendExecRequest = GetMethod(channelSessionType, "SendExecRequest", typeof(string));
                SendShellRequest = GetMethod(channelSessionType, "SendShellRequest");
                SendSubsystemRequest = GetMethod(channelSessionType, "SendSubsystemRequest", typeof(string));
                SendWindowChangeRequest = GetMethod(channelSessionType, "SendWindowChangeRequest", typeof(uint), typeof(uint), typeof(uint), typeof(uint));
                SendX11ForwardingRequest = GetMethod(channelSessionType, "SendX11ForwardingRequest", typeof(bool), typeof(string), typeof(byte[]), typeof(uint));
                SendData = GetMethod(channelType, "SendData", typeof(byte[]), typeof(int), typeof(int));
                SendEof = GetMethod(channelType, "SendEof");
                DataEventData = GetType(assembly, "Renci.SshNet.Common.ChannelDataEventArgs").GetProperty("Data") ?? throw Missing("ChannelDataEventArgs.Data");
                RequestEventInfo = GetType(assembly, "Renci.SshNet.Common.ChannelRequestEventArgs").GetProperty("Info") ?? throw Missing("ChannelRequestEventArgs.Info");
                // Channels that the server opens (X11, see X11Forwarding.cs) are implemented here, on top of the public
                // channel message events of Session; only sending a message and taking a channel number are internal.
                SessionSendMessage = typeof(Session).GetMethod("SendMessage", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(Message) }, null) ?? throw Missing("Session.SendMessage");
                SessionNextChannelNumber = typeof(Session).GetProperty("NextChannelNumber", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw Missing("Session.NextChannelNumber");
            }
            catch (Exception exception)
            {
                LoadError = exception;
            }
        }

        private static Type GetType(Assembly assembly, string name)
        {
            return assembly.GetType(name) ?? throw Missing(name);
        }

        private static MethodInfo GetMethod(Type type, string name, params Type[] parameterTypes)
        {
            return type.GetMethod(name, parameterTypes) ?? throw Missing(type.Name + "." + name);
        }

        private static Exception Missing(string member)
        {
            return new MissingMemberException("SSH.NET " + typeof(SshClient).Assembly.GetName().Version + " does not provide " + member + ". SmarTTY requires a compatible SSH.NET version.");
        }
    }

    /// <summary>
    /// Adapts an event with an internal EventArgs type to a handler taking the base EventArgs.
    /// </summary>
    private sealed class EventSink
    {
        private static readonly MethodInfo HandleMethod = typeof(EventSink).GetMethod(nameof(Handle));

        private readonly Action<EventArgs> _handler;

        private EventSink(Action<EventArgs> handler)
        {
            _handler = handler;
        }

        public void Handle(object sender, EventArgs e)
        {
            _handler(e);
        }

        public static Delegate Create(Type delegateType, Action<EventArgs> handler)
        {
            return Delegate.CreateDelegate(delegateType, new EventSink(handler), HandleMethod);
        }
    }

    // IUTF8 (RFC 8160): tell the remote tty that input is UTF-8, so a program using the kernel line discipline
    // (cat, read, passwd, shells without their own line editor) erases a whole multi-byte character on Backspace
    // instead of a single byte. OpenSSH sends the same flag; Dropbear and OpenSSH servers understand it.
    private static readonly IDictionary<TerminalModes, uint> PtyTerminalModes = new Dictionary<TerminalModes, uint>
    {
        { TerminalModes.IUTF8, 1u }
    };

    // IUTF8 turned off, for a shell whose own line editor counts bytes (BusyBox without Unicode support, OpenWrt):
    // the terminal then repeats Backspace once per byte, and the kernel line discipline must count bytes as well,
    // or a doubled Backspace in cat or read would erase two letters.
    private static readonly IDictionary<TerminalModes, uint> ByteTtyTerminalModes = new Dictionary<TerminalModes, uint>
    {
        { TerminalModes.IUTF8, 0u }
    };

    private readonly object _channel;

    private readonly List<KeyValuePair<EventInfo, Delegate>> _subscriptions = new List<KeyValuePair<EventInfo, Delegate>>();

    private readonly object _sendLock = new object();

    private SshNetChannel(object channel)
    {
        _channel = channel;
    }

    /// <summary>
    /// Creates a new (not yet opened) session channel on a connected client.
    /// </summary>
    public static SshNetChannel Create(SshClient client)
    {
        if (Api.LoadError != null)
        {
            throw Api.LoadError;
        }
        object session = Api.ClientSession.GetValue(client) ?? throw new InvalidOperationException("The SSH session is not connected");
        return new SshNetChannel(InvokeOn(session, Api.CreateChannelSession));
    }

    /// <summary>
    /// Returns the transport session of a client (null while it is not connected).
    /// </summary>
    public static Session GetSession(SshClient client)
    {
        if (Api.LoadError != null)
        {
            throw Api.LoadError;
        }
        return Api.ClientSession.GetValue(client) as Session;
    }

    /// <summary>
    /// Sends a message of a channel implemented by SmarTTY itself (it has no SSH.NET channel object).
    /// Must not be called on SSH.NET's receive thread: during a key re-exchange the call waits for the
    /// exchange to finish, which only that thread can complete.
    /// </summary>
    public static void SendSessionMessage(Session session, Message message)
    {
        if (Api.LoadError != null)
        {
            throw Api.LoadError;
        }
        InvokeOn(session, Api.SessionSendMessage, message);
    }

    /// <summary>
    /// Takes a local channel number from the same counter as SSH.NET's own channels, so the numbers never collide.
    /// </summary>
    public static uint AllocateChannelNumber(Session session)
    {
        if (Api.LoadError != null)
        {
            throw Api.LoadError;
        }
        return (uint)Api.SessionNextChannelNumber.GetValue(session);
    }

    /// <summary>
    /// Subscribes to the channel events. The handlers run on SSH.NET's receive thread; the data segments
    /// are only valid during the call.
    /// </summary>
    public void Subscribe(Action<ArraySegment<byte>> dataReceived, Action<ArraySegment<byte>> extendedDataReceived, Action endOfData, Action closed, Action<int> exitStatusReceived, Action<string> exitSignalReceived, Action<Exception> exception)
    {
        AddHandler("DataReceived", (EventArgs e) => dataReceived((ArraySegment<byte>)Api.DataEventData.GetValue(e)));
        AddHandler("ExtendedDataReceived", (EventArgs e) => extendedDataReceived((ArraySegment<byte>)Api.DataEventData.GetValue(e)));
        AddHandler("EndOfData", (EventArgs e) => endOfData());
        AddHandler("Closed", (EventArgs e) => closed());
        AddHandler("RequestReceived", (EventArgs e) =>
        {
            // The request info classes are internal as well; only their names and the needed values are read.
            object info = Api.RequestEventInfo.GetValue(e);
            switch (((RequestInfo)info).RequestName)
            {
                case "exit-status":
                    exitStatusReceived((int)(uint)info.GetType().GetProperty("ExitStatus").GetValue(info));
                    break;
                case "exit-signal":
                    exitSignalReceived((string)info.GetType().GetProperty("SignalName").GetValue(info) ?? "");
                    break;
            }
        });
        AddHandler("Exception", (EventArgs e) => exception(((ExceptionEventArgs)e).Exception));
    }

    private void AddHandler(string name, Action<EventArgs> handler)
    {
        EventInfo eventInfo = _channel.GetType().GetEvent(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException("SSH.NET channel does not provide the " + name + " event");
        Delegate eventHandler = EventSink.Create(eventInfo.EventHandlerType, handler);
        eventInfo.AddEventHandler(_channel, eventHandler);
        _subscriptions.Add(new KeyValuePair<EventInfo, Delegate>(eventInfo, eventHandler));
    }

    public void Open()
    {
        Invoke(Api.Open);
    }

    public bool SendPseudoTerminalRequest(string terminalType, uint columns, uint rows, bool utf8Tty = true)
    {
        return (bool)Invoke(Api.SendPseudoTerminalRequest, terminalType, columns, rows, 0u, 0u, utf8Tty ? PtyTerminalModes : ByteTtyTerminalModes);
    }

    public bool SendExecRequest(string command)
    {
        return (bool)Invoke(Api.SendExecRequest, command);
    }

    public bool SendShellRequest()
    {
        return (bool)Invoke(Api.SendShellRequest);
    }

    public bool SendSubsystemRequest(string subsystem)
    {
        return (bool)Invoke(Api.SendSubsystemRequest, subsystem);
    }

    /// <summary>
    /// Sends "x11-req" (RFC 4254, 6.3.1); <paramref name="cookie"/> is the hexadecimal cookie text. Must precede
    /// the shell or exec request. Returns false if the server refuses X11 forwarding.
    /// </summary>
    public bool SendX11ForwardingRequest(bool singleConnection, string protocol, string cookie, uint screenNumber)
    {
        return (bool)Invoke(Api.SendX11ForwardingRequest, singleConnection, protocol, System.Text.Encoding.ASCII.GetBytes(cookie), screenNumber);
    }

    public void SendWindowChangeRequest(uint columns, uint rows)
    {
        Invoke(Api.SendWindowChangeRequest, columns, rows, 0u, 0u);
    }

    public void SendData(byte[] buffer, int offset, int count)
    {
        lock (_sendLock)
        {
            Invoke(Api.SendData, buffer, offset, count);
        }
    }

    public void SendEof()
    {
        lock (_sendLock)
        {
            Invoke(Api.SendEof);
        }
    }

    public void Dispose()
    {
        foreach (KeyValuePair<EventInfo, Delegate> subscription in _subscriptions)
        {
            subscription.Key.RemoveEventHandler(_channel, subscription.Value);
        }
        _subscriptions.Clear();
        ((IDisposable)_channel).Dispose();
    }

    private object Invoke(MethodInfo method, params object[] args)
    {
        return InvokeOn(_channel, method, args);
    }

    private static object InvokeOn(object target, MethodInfo method, params object[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
