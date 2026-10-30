using SmarTTY.IO;
using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Transport;

/// <summary>
/// What <see cref="RemoteFileTools.GetInfo"/> found out about a remote path.
/// </summary>
public sealed class RemoteFileInfo
{
    public bool Exists;

    public bool IsDirectory;

    /// <summary>
    /// The path itself is a symbolic link (the other fields describe its target).
    /// </summary>
    public bool IsSymbolicLink;

    public long Size;

    /// <summary>
    /// Permission bits (including setuid/setgid/sticky), e.g. 0755.
    /// </summary>
    public int Permissions;

    public string Owner;

    public string Group;
}

/// <summary>
/// Remote file operations done through the remote shell. They only use commands that BusyBox (OpenWrt) provides as
/// well, and only shell syntax that also works when the login shell is fish or csh: ";", "&&", "||" and "&lt;".
/// </summary>
public static class RemoteFileTools
{
    private static readonly Regex LsLine = new Regex(@"^([-dlcbps])([-rwxsStT]{9})\S*\s+\d+\s+(\S+)\s+(\S+)\s+(\d+)\s");

    private static readonly Regex Sha256Line = new Regex(@"^([0-9a-fA-F]{64})(\s|$)");

    private static readonly Regex Md5Line = new Regex(@"^([0-9a-fA-F]{32})(\s|$)");

    public static string Quote(string path)
    {
        return RemotePathUtils.ShellQuote(path);
    }

    /// <summary>
    /// Describes <paramref name="path"/> ("ls -ldnL", which BusyBox has, unlike "stat"); null if the command could not run.
    /// </summary>
    public static RemoteFileInfo GetInfo(SshSession session, string path)
    {
        if (session.RemoteOs == RemoteOsKind.Windows)
        {
            try
            {
                using SftpChannel sftp = SftpChannel.Open(session);
                return WindowsRemoteFileTools.GetInfo(sftp, path);
            }
            catch (Exception exception) when (exception is RemoteOperationException || exception is SftpStatusException)
            {
                // Like a command that could not run: the upload goes straight to the target.
                return null;
            }
        }
        List<string> output = session.RunCommand("test -L " + Quote(path) + " && echo L; ls -ldnL " + Quote(path) + "; echo .", out _);
        if (output == null || !output.Contains("."))
        {
            return null;
        }
        RemoteFileInfo info = new RemoteFileInfo { IsSymbolicLink = output.Contains("L") };
        foreach (string line in output)
        {
            Match match = LsLine.Match(line);
            if (!match.Success)
            {
                continue;
            }
            info.Exists = true;
            info.IsDirectory = match.Groups[1].Value == "d";
            info.Permissions = ParsePermissions(match.Groups[2].Value);
            info.Owner = match.Groups[3].Value;
            info.Group = match.Groups[4].Value;
            info.Size = long.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
            break;
        }
        // A dangling symbolic link: "ls -L" cannot describe it, but the name is taken.
        info.Exists |= info.IsSymbolicLink;
        return info;
    }

    /// <summary>
    /// Converts the "rwxr-xr-x" part of an "ls -l" line to permission bits.
    /// </summary>
    public static int ParsePermissions(string text)
    {
        int mode = 0;
        for (int i = 0; i < 9; i++)
        {
            char c = text[i];
            int bit = 1 << (8 - i);
            if (c == 'r' || c == 'w' || c == 'x' || c == 's' || c == 't')
            {
                mode |= bit;
            }
            if (c == 's' || c == 'S')
            {
                mode |= (i == 2) ? 0x800 : 0x400;
            }
            if (c == 't' || c == 'T')
            {
                mode |= 0x200;
            }
        }
        return mode;
    }

    /// <summary>
    /// Runs a command and throws with its output if it fails.
    /// </summary>
    public static void Run(SshSession session, string command, string what)
    {
        List<string> output = session.RunCommand(command, out int? exitStatus);
        if (output == null || exitStatus != 0)
        {
            string details = (output == null) ? "the command could not be run" : string.Join(" ", output).Trim();
            throw new RemoteOperationException("Cannot " + what + ((details.Length != 0) ? ": " + details : ""));
        }
    }

    public static void Remove(SshSession session, string path)
    {
        if (session.RemoteOs == RemoteOsKind.Windows)
        {
            using SftpChannel sftp = SftpChannel.Open(session);
            WindowsRemoteFileTools.Remove(sftp, path);
            return;
        }
        Run(session, "rm -f " + Quote(path), "delete " + path);
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="source"/> in one step (a rename within the directory).
    /// </summary>
    public static void Replace(SshSession session, string source, string target)
    {
        if (session.RemoteOs == RemoteOsKind.Windows)
        {
            using SftpChannel sftp = SftpChannel.Open(session);
            WindowsRemoteFileTools.Replace(sftp, source, target);
            return;
        }
        Run(session, "mv -f " + Quote(source) + " " + Quote(target), "replace " + target);
    }

    /// <summary>
    /// Gives <paramref name="path"/> the permissions and, where allowed, the owner of a file it is going to replace.
    /// </summary>
    public static void CopyAttributes(SshSession session, RemoteFileInfo from, string path)
    {
        if (session.RemoteOs == RemoteOsKind.Windows)
        {
            // No chmod or chown there: a file replaced through a temporary one keeps the access rights of its folder.
            return;
        }
        Run(session, "chmod " + Convert.ToString(from.Permissions & 0xFFF, 8) + " " + Quote(path), "set the permissions of " + path);
        if (from.Owner != null && from.Group != null)
        {
            // Only root (or the owner, for the group) may do this; otherwise the new file belongs to the uploading user,
            // as with any editor that saves through a temporary file.
            session.RunCommand("chown " + from.Owner + ":" + from.Group + " " + Quote(path), out _);
        }
    }

    /// <summary>
    /// Hashes a remote file with sha256sum, or md5sum where there is no sha256sum. Returns null if neither exists.
    /// </summary>
    public static FileChecksum ComputeRemoteChecksum(SshSession session, string path)
    {
        if (session.RemoteOs == RemoteOsKind.Windows)
        {
            string command = WindowsRemoteFileTools.ChecksumCommand(path);
            return command == null ? null : WindowsRemoteFileTools.ParseChecksum(session.RunCommand(command, out _));
        }
        // Reading from stdin keeps the file name out of the output, whatever characters it contains.
        List<string> output = session.RunCommand("sha256sum < " + Quote(path) + " || md5sum < " + Quote(path), out _);
        if (output == null)
        {
            return null;
        }
        foreach (string line in output)
        {
            Match match = Sha256Line.Match(line.Trim());
            if (match.Success)
            {
                return new FileChecksum("SHA-256", match.Groups[1].Value.ToLowerInvariant());
            }
        }
        foreach (string line in output)
        {
            Match match = Md5Line.Match(line.Trim());
            if (match.Success)
            {
                return new FileChecksum("MD5", match.Groups[1].Value.ToLowerInvariant());
            }
        }
        return null;
    }

    /// <summary>
    /// Hashes a local file; <paramref name="checkCancelled"/> runs between blocks and stops the work by throwing.
    /// </summary>
    public static FileChecksum ComputeLocalChecksum(string path, string algorithm, Action checkCancelled = null)
    {
        using HashAlgorithm hash = (algorithm == "MD5") ? (HashAlgorithm)MD5.Create() : SHA256.Create();
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = new byte[262144];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            checkCancelled?.Invoke();
            hash.TransformBlock(buffer, 0, read, null, 0);
        }
        hash.TransformFinalBlock(buffer, 0, 0);
        StringBuilder text = new StringBuilder();
        foreach (byte b in hash.Hash)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return new FileChecksum(algorithm, text.ToString());
    }

    /// <summary>
    /// Reads <paramref name="path"/> from <paramref name="offset"/> on ("tail -c +N", also in BusyBox): resuming a
    /// download over SCP, which itself always starts at the beginning.
    /// </summary>
    public static Stream OpenReadFrom(SshSession session, string path, long offset, long length)
    {
        return new ExecStream(session.OpenShellChannel("tail -c +" + (offset + 1).ToString(CultureInfo.InvariantCulture) + " " + Quote(path)), length, forWriting: false, path);
    }

    /// <summary>
    /// Appends to <paramref name="path"/> ("cat &gt;&gt;"): resuming an upload over SCP.
    /// </summary>
    public static Stream OpenAppend(SshSession session, string path, long length)
    {
        return new ExecStream(session.OpenShellChannel("cat >> " + Quote(path), extendedDataMode: ExtendedDataMode.Merge), length, forWriting: true, path);
    }

    /// <summary>
    /// The stdin or stdout of a remote command as a stream of a known length; disposing it checks the exit status.
    /// </summary>
    private sealed class ExecStream : Stream
    {
        private readonly SshShellChannel _channel;

        private readonly long _length;

        private readonly bool _forWriting;

        private readonly string _path;

        private long _done;

        private bool _closed;

        public ExecStream(SshShellChannel channel, long length, bool forWriting, string path)
        {
            _channel = channel;
            _length = length;
            _forWriting = forWriting;
            _path = path;
        }

        public override bool CanRead => !_forWriting;

        public override bool CanSeek => false;

        public override bool CanWrite => _forWriting;

        public override long Length => _length;

        public override long Position
        {
            get => _done;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, _length - _done);
            if (count <= 0)
            {
                return 0;
            }
            int read = _channel.Output.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new IOException("The transfer was interrupted: " + _done + " of " + _length + " bytes of " + _path + " were received." + ErrorText());
            }
            _done += read;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _channel.Input.Write(buffer, offset, count);
            _done += count;
        }

        private string ErrorText()
        {
            try
            {
                using StreamReader reader = new StreamReader(_forWriting ? (Stream)_channel.Output : _channel.Error);
                string text = reader.ReadToEnd().Trim();
                return (text.Length != 0) ? " " + text : "";
            }
            catch
            {
                return "";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    if (_done == _length)
                    {
                        _channel.SendEof();
                        string error = _forWriting ? ErrorText() : "";
                        _channel.WaitClosed();
                        if (_channel.ExitStatus != 0)
                        {
                            throw new RemoteOperationException("The remote command failed for " + _path + ":" + ((error.Length != 0) ? error : (_forWriting ? "" : ErrorText())));
                        }
                    }
                }
                finally
                {
                    _channel.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}

/// <summary>
/// A file hash and the algorithm that produced it.
/// </summary>
public sealed class FileChecksum
{
    public readonly string Algorithm;

    public readonly string Value;

    public FileChecksum(string algorithm, string value)
    {
        Algorithm = algorithm;
        Value = value;
    }

    public override string ToString()
    {
        return Algorithm + " " + Value;
    }
}
