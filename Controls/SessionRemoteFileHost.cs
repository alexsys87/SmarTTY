using SmarTTY.CodeEditing;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Controls;

/// <summary>
/// <see cref="IRemoteFileWriteHost"/> (and so <see cref="IRemoteFileHost"/>) over the SSH session of a tab. The session is read from the context on every call: it is replaced
/// when the tab reconnects, and the editor must use the live one.
/// </summary>
internal sealed class SessionRemoteFileHost : IRemoteFileWriteHost
{
    private readonly ITerminalSessionContext _context;

    public SessionRemoteFileHost(ITerminalSessionContext context)
    {
        _context = context;
    }

    public bool IsWindows => _context.Session.RemoteOs == RemoteOsKind.Windows;

    public List<string> RunCommandChecked(string command)
    {
        return _context.Session.RunCommandChecked(command, requireSuccess: true);
    }

    public void ThrowIfConnectionLost()
    {
        _context.Session.ThrowIfConnectionLost();
    }

    public Stream OpenScpDownload(string path, out long length)
    {
        ScpDownloadStream stream = _context.Session.OpenDownload(path);
        length = stream.Length;
        return stream;
    }

    public Stream OpenSftpRead(string path, out long length)
    {
        return WindowsRemoteFileEditing.OpenRead(_context.Session, path, out length);
    }

    public bool SftpExists(string path)
    {
        return WindowsRemoteFileEditing.Exists(_context.Session, path);
    }

    public bool? SftpCanWrite(string path)
    {
        return WindowsRemoteFileEditing.CanWrite(_context.Session, path);
    }

    public string SftpSignature(string path)
    {
        return WindowsRemoteFileEditing.Signature(_context.Session, path);
    }

    public void UploadScp(string path, byte[] bytes, int mode)
    {
        using (ScpChannelStream scpChannelStream = _context.Session.OpenUpload(path, bytes.Length, mode: mode))
        {
            scpChannelStream.Write(bytes, 0, bytes.Length);
        }
    }

    public List<string> RunCommand(string command, out int? exitStatus, TimeSpan? timeout)
    {
        return _context.Session.RunCommand(command, out exitStatus, timeout: timeout);
    }

    public void SftpSave(string path, byte[] bytes, bool backup)
    {
        WindowsRemoteFileEditing.Save(_context.Session, path, bytes, backup);
    }

    public SudoRunResult RunSudo(string sudoCommand)
    {
        SshShellChannel sshShellChannel = _context.Session.OpenShellChannel(sudoCommand, "xterm");
        RemoteFileEditor.SudoConfirmationPrompt?.Invoke(sshShellChannel, sudoCommand);
        try
        {
            sshShellChannel.Input.Write(new byte[1] { 3 }, 0, 1);
        }
        catch
        {
        }
        sshShellChannel.WaitClosed();
        int sudoExitStatus = sshShellChannel.ExitStatus;
        return new SudoRunResult
        {
            ExitStatus = sudoExitStatus,
            HasExitStatus = sshShellChannel.HasExitStatus,
            HasExitSignal = sshShellChannel.ExitSignal != null
        };
    }
}
