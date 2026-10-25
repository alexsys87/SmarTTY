using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// The namespace AuthorizedKeysManager.cs imports; nothing from it is used.
namespace VisualTTY
{
    internal static class Placeholder
    {
    }
}

namespace SmarTTY.Ssh
{
    /// <summary>How the remote "sh -c AppendScript" ends in the stand-in below.</summary>
    public enum AppendOutcome
    {
        /// <summary>The line is stored and the script exits with 0.</summary>
        Success,

        /// <summary>The flash is full (OpenWrt overlay): the script fails with a message and exit status 1.</summary>
        DiskFull,

        /// <summary>The channel is closed by the server without exit status and without the line being stored.</summary>
        ClosedSilently,

        /// <summary>The connection goes away while the script runs.</summary>
        ConnectionLost,

        /// <summary>The script never finishes.</summary>
        Hangs,

        /// <summary>The line is stored, but the server closes the channel without reporting an exit status.</summary>
        StoredWithoutStatus
    }

    public class ScpDownloadStream : MemoryStream
    {
    }

    public class ScpChannelStream : MemoryStream
    {
        internal static string QuotePath(string path)
        {
            return "'" + path.Replace("'", "'\\''") + "'";
        }
    }

    /// <summary>
    /// A stand-in for SshShellChannel running the append script. Like the real channel, the script runs on the server
    /// while the client goes on: it is only completed when the client waits for it, and a channel that is disposed
    /// before that cancels it, so a caller that does not wait stores nothing.
    /// </summary>
    public class SshShellChannel : IDisposable
    {
        private sealed class InputStream : MemoryStream
        {
            private readonly SshShellChannel _channel;

            public InputStream(SshShellChannel channel)
            {
                _channel = channel;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_channel._session.IsConnectionClosed || _channel._closed)
                {
                    throw new IOException("Cannot write to a closed SSH channel");
                }
                base.Write(buffer, offset, count);
            }
        }

        private readonly SshSession _session;
        private readonly string _path;
        private bool _eofSent;
        private bool _closed;
        private bool _finished;
        private int? _exitStatus;
        private string _message = "";
        private bool _lost;

        public readonly Stream Input;

        public Stream Output => new MemoryStream(Encoding.UTF8.GetBytes(_message));

        public Exception Failure => null;

        public bool ClosedByConnectionLoss => _lost;

        public int ExitStatus => _exitStatus ?? -1;

        /// <summary>False when the server closed the channel without reporting an exit status.</summary>
        public bool HasExitStatus => _exitStatus.HasValue;

        public string ExitSignal => null;

        internal SshShellChannel(SshSession session, string path)
        {
            _session = session;
            _path = path;
            Input = new InputStream(this);
        }

        public void SendEof()
        {
            if (_closed)
            {
                return;
            }
            _eofSent = true;
        }

        public bool WaitEof(TimeSpan timeout)
        {
            RunScript();
            return _finished;
        }

        // What the server does with the script once it has the whole input.
        private void RunScript()
        {
            if (_finished || _closed || !_eofSent)
            {
                return;
            }
            switch (_session.AppendOutcome)
            {
                case AppendOutcome.Success:
                case AppendOutcome.StoredWithoutStatus:
                    _session.AppendedPaths.Add(_path);
                    string text = Encoding.UTF8.GetString(((MemoryStream)Input).ToArray());
                    string existing = _session.Files.TryGetValue(_path, out string current) ? current : "";
                    if (existing.Length > 0 && !existing.EndsWith("\n"))
                    {
                        existing += "\n";
                    }
                    _session.Files[_path] = existing + text;
                    _exitStatus = _session.AppendOutcome == AppendOutcome.Success ? 0 : (int?)null;
                    _finished = true;
                    break;
                case AppendOutcome.DiskFull:
                    _message = "cat: write error: No space left on device";
                    _exitStatus = 1;
                    _finished = true;
                    break;
                case AppendOutcome.ClosedSilently:
                    _finished = true;
                    break;
                case AppendOutcome.ConnectionLost:
                    _session.IsConnectionClosed = true;
                    _lost = true;
                    _finished = true;
                    break;
                case AppendOutcome.Hangs:
                    break;
            }
        }

        public void Dispose()
        {
            _closed = true;
        }
    }

    /// <summary>A stand-in for SshSession with a small remote file system and the commands AuthorizedKeysManager runs.</summary>
    public class SshSession
    {
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>();

        public readonly List<string> Commands = new List<string>();

        public readonly List<string> AppendedPaths = new List<string>();

        public string Home = "/root";

        public bool IsDropbearRoot;

        public bool FailMkdir;

        public bool FailChmod;

        /// <summary>The server has no scp program, as on OpenWrt: files are read with cat.</summary>
        public bool NoScp = true;

        public AppendOutcome AppendOutcome = AppendOutcome.Success;

        public bool IsConnectionClosed { get; set; }

        public string ServerVersion => IsDropbearRoot ? "SSH-2.0-dropbear_2022.83" : "SSH-2.0-OpenSSH_9.6";

        public void ThrowIfConnectionLost()
        {
            if (IsConnectionClosed)
            {
                throw new IOException("The connection to the server was lost.");
            }
        }

        public List<string> RunCommandChecked(string command, bool requireSuccess, string terminalType = null, TimeSpan? timeout = null)
        {
            Commands.Add(command);
            if (IsConnectionClosed)
            {
                return null;
            }
            if (command == "pwd")
            {
                return new List<string> { Home };
            }
            if (command == "id -u")
            {
                return new List<string> { IsDropbearRoot ? "0" : "1000" };
            }
            if (command == "test -d /etc/dropbear")
            {
                return IsDropbearRoot ? new List<string>() : null;
            }
            if (command.StartsWith("test -d ") && command.Contains("mkdir"))
            {
                return FailMkdir ? null : new List<string>();
            }
            if (command.StartsWith("chmod "))
            {
                return FailChmod ? null : new List<string>();
            }
            if (command.StartsWith("cat '"))
            {
                string path = command.Substring(5, command.Length - 6);
                return Files.TryGetValue(path, out string content) ? content.Split('\n').ToList() : null;
            }
            throw new InvalidOperationException("Unexpected command: " + command);
        }

        public ScpDownloadStream OpenDownload(string path)
        {
            if (NoScp)
            {
                throw new RemoteOperationException("scp: not found while trying to download " + path);
            }
            if (!Files.TryGetValue(path, out string content))
            {
                throw new RemoteOperationException("No such file while trying to download " + path);
            }
            ScpDownloadStream stream = new ScpDownloadStream();
            byte[] data = Encoding.UTF8.GetBytes(content);
            stream.Write(data, 0, data.Length);
            stream.Position = 0;
            return stream;
        }

        public ScpChannelStream OpenUpload(string path, long length, DateTime modifyTime = default(DateTime), int? mode = null)
        {
            throw new NotSupportedException();
        }

        public SshShellChannel OpenShellChannel(string command = null, string terminalType = null, ExtendedDataMode extendedDataMode = ExtendedDataMode.Normal, bool utf8Tty = true)
        {
            // "sh -c '<script>' sh '<path>'": the path is the last quoted word.
            int start = command.LastIndexOf(" sh '") + 5;
            string path = command.Substring(start, command.Length - start - 1);
            return new SshShellChannel(this, path);
        }
    }
}
