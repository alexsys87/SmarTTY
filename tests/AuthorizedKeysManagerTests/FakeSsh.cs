using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh
{
    /// <summary>How the remote "sh -c AppendScript" ends in the stand-in below.</summary>
    public enum AppendOutcome
    {
        /// <summary>The line is stored and the script exits with 0.</summary>
        Success,

        /// <summary>The flash is full (OpenWrt overlay): the script fails with a message and exit status 1.</summary>
        DiskFull,

        /// <summary>As <see cref="DiskFull"/>, but the first bytes of the line were written before the space ran out.</summary>
        DiskFullPartial,

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
        /// <summary>Called with the whole upload when the stream is closed, like the server storing the file.</summary>
        internal Action<byte[]> OnClosed;

        internal static string QuotePath(string path)
        {
            return "'" + path.Replace("'", "'\\''") + "'";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && OnClosed != null)
            {
                Action<byte[]> onClosed = OnClosed;
                OnClosed = null;
                onClosed(ToArray());
            }
            base.Dispose(disposing);
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
        private readonly bool _replace;
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

        internal SshShellChannel(SshSession session, string path, bool replace)
        {
            _session = session;
            _path = path;
            _replace = replace;
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
            AppendOutcome outcome = _session.OutcomeOnlyFor == null || _path.Contains(_session.OutcomeOnlyFor) ? _session.AppendOutcome : AppendOutcome.Success;
            switch (outcome)
            {
                case AppendOutcome.DiskFullPartial:
                    string written = Encoding.UTF8.GetString(((MemoryStream)Input).ToArray());
                    string before = _session.Files.TryGetValue(_path, out string content) ? content : "";
                    if (before.Length > 0 && !before.EndsWith("\n"))
                    {
                        before += "\n";
                    }
                    _session.Files[_path] = before + written.Substring(0, 25);
                    _message = "cat: write error: No space left on device";
                    _exitStatus = 1;
                    _finished = true;
                    break;
                case AppendOutcome.Success:
                case AppendOutcome.StoredWithoutStatus:
                    string text = Encoding.UTF8.GetString(((MemoryStream)Input).ToArray());
                    if (_replace)
                    {
                        // The script writes a temporary file and moves it over the original: all or nothing.
                        _session.ReplacedPaths.Add(_path);
                        _session.Files[_path] = text;
                    }
                    else
                    {
                        _session.AppendedPaths.Add(_path);
                        string existing = _session.Files.TryGetValue(_path, out string current) ? current : "";
                        if (existing.Length > 0 && !existing.EndsWith("\n"))
                        {
                            existing += "\n";
                        }
                        _session.Files[_path] = existing + text;
                    }
                    _exitStatus = outcome == AppendOutcome.Success ? 0 : (int?)null;
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

        /// <summary>Files replaced by the remote script that works without scp.</summary>
        public readonly List<string> ReplacedPaths = new List<string>();

        /// <summary>Files stored through scp.</summary>
        public readonly List<string> ScpUploads = new List<string>();

        /// <summary>Files that exist but cannot be read (no permission).</summary>
        public readonly HashSet<string> Unreadable = new HashSet<string>();

        public string Home = "/root";

        public bool IsDropbearRoot;

        public bool FailMkdir;

        public bool FailChmod;

        /// <summary>The server has no scp program, as on OpenWrt: files are read with cat.</summary>
        public bool NoScp = true;

        public AppendOutcome AppendOutcome = AppendOutcome.Success;

        /// <summary>When set, the outcome above applies only to appends to paths that contain this text.</summary>
        public string OutcomeOnlyFor;

        /// <summary>The shell that runs the commands that need one (the removal of an incomplete line).</summary>
        public string[] Shell = new[] { "sh" };

        public bool IsConnectionClosed { get; set; }

        /// <summary>The Windows path (SFTP) is checked by tests/SshNetIntegrationTests against a real server.</summary>
        public SmarTTY.Ssh.RemoteOsKind RemoteOs => SmarTTY.Ssh.RemoteOsKind.Unknown;

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
            if (command.StartsWith("test -d "))
            {
                string directory = command.Substring(8).Trim('\'');
                return Files.Keys.Any(path => path.StartsWith(directory + "/")) ? new List<string>() : null;
            }
            if (command.StartsWith("test -e "))
            {
                return Files.ContainsKey(command.Substring(8).Trim('\'')) ? new List<string>() : null;
            }
            if (command.StartsWith("mv '"))
            {
                string[] paths = command.Substring(4, command.Length - 5).Split(new[] { "' '" }, StringSplitOptions.None);
                if (!Files.TryGetValue(paths[0], out string moved))
                {
                    return null;
                }
                Files.Remove(paths[0]);
                Files[paths[1]] = moved;
                return new List<string>();
            }
            if (command.StartsWith("chmod "))
            {
                return FailChmod ? null : new List<string>();
            }
            if (command.StartsWith("sh -c ") && command.Contains("truncate -s"))
            {
                return RunRemovalWithRealShell(command);
            }
            if (command.StartsWith("cat '"))
            {
                string path = command.Substring(5, command.Length - 6);
                if (Unreadable.Contains(path) || !Files.TryGetValue(path, out string content))
                {
                    return null;
                }
                // Like the line reader of the real session: no empty line after the last line break.
                List<string> lines = new List<string>();
                using StringReader reader = new StringReader(content);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
                return lines;
            }
            throw new InvalidOperationException("Unexpected command: " + command);
        }

        // The path is the first quoted word after the script; the file of the stand-in is run through a real shell on a copy.
        private List<string> RunRemovalWithRealShell(string command)
        {
            int start = command.IndexOf("' sh '") + 6;
            string path = command.Substring(start, command.IndexOf("'", start) - start);
            string temp = System.IO.Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(Files[path]));
                string local = command.Replace(ScpChannelStream.QuotePath(path), ScpChannelStream.QuotePath(temp));
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(Shell[0]) { UseShellExecute = false, RedirectStandardError = true };
                foreach (string argument in Shell.Skip(1))
                {
                    info.ArgumentList.Add(argument);
                }
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add(local);
                using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info);
                process.WaitForExit();
                Files[path] = new UTF8Encoding(false).GetString(File.ReadAllBytes(temp));
                return process.ExitCode == 0 ? new List<string>() : null;
            }
            finally
            {
                File.Delete(temp);
            }
        }

        public ScpDownloadStream OpenDownload(string path)
        {
            if (NoScp)
            {
                throw new RemoteOperationException("scp: not found while trying to download " + path);
            }
            if (Unreadable.Contains(path) || !Files.TryGetValue(path, out string content))
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
            if (NoScp)
            {
                throw new RemoteOperationException("scp: not found while trying to upload " + path);
            }
            return new ScpChannelStream
            {
                OnClosed = data =>
                {
                    ScpUploads.Add(path);
                    Files[path] = new UTF8Encoding(false).GetString(data);
                }
            };
        }

        public SshShellChannel OpenShellChannel(string command = null, string terminalType = null, ExtendedDataMode extendedDataMode = ExtendedDataMode.Normal, bool utf8Tty = true)
        {
            // "sh -c '<script>' sh '<path>' [length]": the path is the quoted word after the script.
            int start = command.LastIndexOf(" sh '") + 5;
            string path = command.Substring(start, command.IndexOf('\'', start) - start);
            return new SshShellChannel(this, path, command.Contains("mv "));
        }
    }
}
