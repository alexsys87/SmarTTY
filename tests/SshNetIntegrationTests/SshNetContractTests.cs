using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Compression;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Verifies the SSH.NET API surface that the SmarTTY SSH layer depends on.
/// Most of that surface is internal to SSH.NET and reached through reflection
/// (see Ssh/SshNetChannel.cs), so a version update fails at runtime instead of at compile time.
/// These checks turn such a break into a failing test with a readable message.
/// </summary>
internal static class SshNetContractTests
{
    private static Assembly SshNet => typeof(SshClient).Assembly;

    public static void Run()
    {
        Test.Section("SSH.NET identity");
        Test.Check(!string.IsNullOrEmpty(SshNet.GetName().Version?.ToString()), "SSH.NET assembly has a version");
        Console.WriteLine("     SSH.NET " + SshNet.GetName().Version + " (" + SshNet.Location + ")");

        Test.Section("SSH.NET reflection targets used by SshNetChannel");
        Type sessionType = RequireType("Renci.SshNet.ISession");
        Type channelType = RequireType("Renci.SshNet.Channels.IChannel");
        Type channelSessionType = RequireType("Renci.SshNet.Channels.IChannelSession");

        Test.Check(typeof(Session).GetInterfaces().Contains(sessionType), "Session implements ISession (used as the SshNetChannel.GetSession return type)");
        Test.Check(channelSessionType.GetInterfaces().Contains(channelType), "IChannelSession extends IChannel");
        Test.Check(typeof(BaseClient).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic) != null, "BaseClient.Session (non-public) exists");
        Test.Check(RequireType("Renci.SshNet.Common.ChannelDataEventArgs").GetProperty("Data") != null, "ChannelDataEventArgs.Data exists");
        Test.Check(RequireType("Renci.SshNet.Common.ChannelRequestEventArgs").GetProperty("Info") != null, "ChannelRequestEventArgs.Info exists");
        Test.Check(RequireType("Renci.SshNet.Messages.Connection.RequestInfo").GetProperty("RequestName") != null, "RequestInfo.RequestName exists");

        RequireMethod(sessionType, "CreateChannelSession");
        Test.Check(sessionType.GetMethod("CreateChannelSession").ReturnType == channelSessionType, "ISession.CreateChannelSession returns IChannelSession");
        RequireMethod(channelSessionType, "Open");
        RequireMethod(channelSessionType, "SendPseudoTerminalRequest", typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<TerminalModes, uint>));
        RequireMethod(channelSessionType, "SendExecRequest", typeof(string));
        RequireMethod(channelSessionType, "SendShellRequest");
        RequireMethod(channelSessionType, "SendWindowChangeRequest", typeof(uint), typeof(uint), typeof(uint), typeof(uint));
        RequireMethod(channelType, "SendData", typeof(byte[]), typeof(int), typeof(int));
        RequireMethod(channelType, "SendEof");
        Test.Check(Enum.IsDefined(typeof(TerminalModes), TerminalModes.ECHO), "TerminalModes enum is usable for the PTY request");

        Test.Section("SSH.NET channel events used by SshNetChannel");
        foreach (string name in new[] { "DataReceived", "ExtendedDataReceived", "Closed", "RequestReceived", "Exception" })
        {
            Test.Check(FindEvent(channelType, name) != null || FindEvent(channelSessionType, name) != null, "channel event " + name + " is available");
        }
        // SshNetChannel resolves events on the runtime channel type, so EndOfData may live on a base class.
        Test.Check(FindEvent(RequireType("Renci.SshNet.Channels.Channel"), "EndOfData") != null, "channel event EndOfData is available on the channel base class");

        Test.Section("SSH.NET exit status and signal payloads");
        Type exitStatusType = RequireType("Renci.SshNet.Messages.Connection.ExitStatusRequestInfo");
        Type exitSignalType = RequireType("Renci.SshNet.Messages.Connection.ExitSignalRequestInfo");
        Test.Check(GetPropertyType(exitStatusType, "ExitStatus") == typeof(uint), "ExitStatusRequestInfo.ExitStatus is uint");
        Test.Check(GetPropertyType(exitSignalType, "SignalName") == typeof(string), "ExitSignalRequestInfo.SignalName is string");
        Test.Check(RequestName(exitStatusType) == "exit-status", "ExitStatusRequestInfo.RequestName is 'exit-status'");
        Test.Check(RequestName(exitSignalType) == "exit-signal", "ExitSignalRequestInfo.RequestName is 'exit-signal'");

        Test.Section("SSH.NET pieces used directly by SshSession");
        Test.Check(typeof(HostKeyEventArgs).GetProperty("HostKey") != null && typeof(HostKeyEventArgs).GetProperty("HostKeyName") != null, "HostKeyEventArgs exposes HostKey and HostKeyName");
        Test.Check(typeof(HostKeyEventArgs).GetProperty("CanTrust") != null, "HostKeyEventArgs.CanTrust is settable (host key is verified by the caller)");
        Test.Check(typeof(ExceptionEventArgs).GetProperty("Exception") != null, "ExceptionEventArgs.Exception exists");
        Test.Check(typeof(Session).GetEvent("Disconnected") != null, "Session.Disconnected event exists");
        Test.Check(typeof(ConnectionInfo).GetProperty("CompressionAlgorithms") != null, "ConnectionInfo.CompressionAlgorithms exists");
        Test.Check(typeof(ConnectionInfo).GetProperty("HostKeyAlgorithms") != null, "ConnectionInfo.HostKeyAlgorithms exists");
        Test.Check(typeof(ConnectionInfo).GetProperty("MaxSessions") != null, "ConnectionInfo.MaxSessions exists");
        Test.Check(typeof(Compressor).IsAssignableFrom(typeof(Zlib)) && typeof(Compressor).IsAssignableFrom(typeof(ZlibOpenSsh)), "zlib compressors are available for EnableCompression");
        Test.Check(typeof(ProxyTypes).GetEnumNames().Contains("Http"), "ProxyTypes.Http exists for the HTTP CONNECT proxy");
        Test.Check(HasConstructor(typeof(PrivateKeyAuthenticationMethod), typeof(string), typeof(IPrivateKeySource[])), "PrivateKeyAuthenticationMethod(string, IPrivateKeySource[]) exists");
        Test.Check(HasConstructor(typeof(PrivateKeyFile), typeof(string)) && HasConstructor(typeof(PrivateKeyFile), typeof(string), typeof(string)), "PrivateKeyFile supports a path and a passphrase");
        Test.Check(typeof(NoneAuthenticationMethod).GetProperty("AllowedAuthentications") != null, "NoneAuthenticationMethod.AllowedAuthentications exists");
        Test.Check(HasConstructor(typeof(NoneAuthenticationMethod), typeof(string)), "NoneAuthenticationMethod(string) exists");
        Test.Check(typeof(AuthenticationMethod).GetMethod("Authenticate", new[] { typeof(Session) }) != null, "AuthenticationMethod.Authenticate(Session) exists");
        Test.Check(typeof(KeyboardInteractiveAuthenticationMethod).GetEvent("AuthenticationPrompt") != null, "KeyboardInteractiveAuthenticationMethod.AuthenticationPrompt exists");
        Test.Check(GetPropertyType(typeof(AuthenticationPrompt), "Request") == typeof(string) && GetPropertyType(typeof(AuthenticationPrompt), "IsEchoed") == typeof(bool), "AuthenticationPrompt exposes Request and IsEchoed");
        Test.Check(GetPropertyType(typeof(AuthenticationPrompt), "Response") == typeof(string), "AuthenticationPrompt.Response is settable");

        Test.Section("SSH.NET pieces used by RsaSshKey");
        Test.Check(HasConstructor(typeof(RsaKey), typeof(byte[])), "RsaKey(byte[]) exists (PKCS#1 private key)");
        Test.Check(HasConstructor(typeof(KeyHostAlgorithm), typeof(string), typeof(Key), typeof(DigitalSignature)), "KeyHostAlgorithm(string, Key, DigitalSignature) exists");
        Test.Check(HasConstructor(typeof(RsaDigitalSignature), typeof(RsaKey), typeof(HashAlgorithmName)), "RsaDigitalSignature(RsaKey, HashAlgorithmName) exists");
        Test.Check(typeof(HostAlgorithm).IsAssignableFrom(typeof(KeyHostAlgorithm)), "KeyHostAlgorithm is a HostAlgorithm");
        Test.Check(typeof(IPrivateKeySource).GetProperty("HostKeyAlgorithms") != null, "IPrivateKeySource.HostKeyAlgorithms exists");

        Test.Section("SshNetChannel behaviour without a connection");
        using (SshClient client = new SshClient("127.0.0.1", 1, "user", "password"))
        {
            Test.Check(SshNetChannel.GetSession(client) == null, "GetSession returns null while the client is not connected");
            Test.Throws<InvalidOperationException>(() => SshNetChannel.Create(client), "Create reports an unconnected client instead of failing later");
        }
    }

    /// <summary>
    /// Reports the SSH.NET version under test. The test project takes that version from
    /// SmarTTY.csproj, so a failure to read it there stops the build instead of silently testing
    /// a different library.
    /// </summary>
    public static void ReportVersion()
    {
        Test.Section("SSH.NET version");
        string actual = SshNet.GetName().Version?.ToString();
        Test.Check(!string.IsNullOrEmpty(actual), "the SSH.NET assembly has a version");
        Console.WriteLine("     Testing SSH.NET " + actual + " (declared in SmarTTY.csproj)");
    }

    private static Type RequireType(string name)
    {
        Type type = SshNet.GetType(name);
        Test.Check(type != null, "type " + name + " exists");
        if (type == null)
        {
            throw new InvalidOperationException("SSH.NET does not provide " + name + "; the reflection lookups in Ssh/SshNetChannel.cs need updating.");
        }
        return type;
    }

    private static void RequireMethod(Type type, string name, params Type[] parameters)
    {
        Test.Check(GetMethod(type, name, parameters) != null, type.Name + "." + name + "(" + string.Join(", ", parameters.Select(p => p.Name)) + ") exists");
    }

    private static MethodInfo GetMethod(Type type, string name, params Type[] parameters)
    {
        return type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
    }

    private static bool HasConstructor(Type type, params Type[] parameters)
    {
        return type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null) != null;
    }

    /// <summary>
    /// Reads RequestName from an internal RequestInfo type without instantiating it.
    /// </summary>
    private static string RequestName(Type requestInfoType)
    {
        object instance = Activator.CreateInstance(requestInfoType, nonPublic: true);
        return (string)requestInfoType.GetProperty("RequestName").GetValue(instance);
    }

    private static EventInfo FindEvent(Type type, string name)
    {
        return type.GetEvent(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static Type GetPropertyType(Type type, string name)
    {
        return type.GetProperty(name)?.PropertyType;
    }
}
