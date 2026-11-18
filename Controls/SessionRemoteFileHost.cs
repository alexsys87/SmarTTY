using SmarTTY.CodeEditing;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.Transport;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Controls;

/// <summary>
/// <see cref="IRemoteFileHost"/> over the SSH session of a tab. The session is read from the context on every call: it is replaced
/// when the tab reconnects, and the editor must use the live one.
/// </summary>
internal sealed class SessionRemoteFileHost : IRemoteFileHost
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
}
