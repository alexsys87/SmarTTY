using SmarTTY.Ssh;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// <see cref="IRemoteShell"/> over the SSH session of a window: every call opens an exec channel without a PTY (so the output is
/// the bytes of the command, and stderr stays apart), registered with the PTY gate like the other helper commands.
/// <see cref="Abandon"/> ends the running call at once (Stop).
/// </summary>
internal sealed class SshRemoteShell : IRemoteShell
{
    private readonly SshSession _session;

    private SshShellChannel _channel;

    public SshRemoteShell(SshSession session)
    {
        _session = session;
    }

    /// <summary>Closes the channel of the running call: its reading and writing end, and the server ends the command.</summary>
    public void Abandon()
    {
        try
        {
            _channel?.Abandon();
        }
        catch
        {
        }
    }

    public RemoteCommandResult Run(string command)
    {
        return Execute(command, null, null, null, null);
    }

    public RemoteCommandResult RunWithInput(string command, Stream input, Action<long> progress, Func<bool> isCancelled)
    {
        return Execute(command, input, null, progress, isCancelled);
    }

    public RemoteCommandResult RunToStream(string command, Stream output, Action<long> progress, Func<bool> isCancelled)
    {
        return Execute(command, null, output, progress, isCancelled);
    }

    private RemoteCommandResult Execute(string command, Stream input, Stream output, Action<long> progress, Func<bool> isCancelled)
    {
        RemoteCommandResult result = new RemoteCommandResult();
        _session.PtyGate.BeginPlainHelper();
        try
        {
            using SshShellChannel channel = _session.OpenShellChannel(command, null, ExtendedDataMode.Normal);
            _channel = channel;
            if (input == null)
            {
                // Nothing to send: a command that reads its standard input must not wait for it.
                try
                {
                    channel.SendEof();
                }
                catch
                {
                }
            }
            StringBuilder error = new StringBuilder();
            Thread errorReader = new Thread(() => ReadText(channel.Error, error)) { IsBackground = true, Name = "Remote shell stderr" };
            errorReader.Start();
            List<string> lines = new List<string>();
            Thread outputReader = null;
            byte[] buffer = new byte[1 << 16];
            long bytes = 0;
            if (output == null)
            {
                StringBuilder text = new StringBuilder();
                outputReader = new Thread(() => ReadText(channel.Output, text)) { IsBackground = true, Name = "Remote shell stdout" };
                outputReader.Start();
                if (input != null)
                {
                    try
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (isCancelled?.Invoke() == true)
                            {
                                channel.Abandon();
                                throw new OperationCanceledException();
                            }
                            if (channel.TryWaitClosed())
                            {
                                // The command ended before it read everything: its exit status and messages say why.
                                break;
                            }
                            channel.Input.Write(buffer, 0, read);
                            bytes += read;
                            progress?.Invoke(bytes);
                        }
                    }
                    catch (IOException) when (channel.TryWaitClosed() || channel.IsEof)
                    {
                    }
                    channel.SendEof();
                }
                channel.WaitClosed();
                outputReader.Join(TimeSpan.FromSeconds(30));
                lines.AddRange(text.ToString().Replace("\r\n", "\n").Split('\n'));
            }
            else
            {
                int read;
                while ((read = channel.Output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    bytes += read;
                    progress?.Invoke(bytes);
                    if (isCancelled?.Invoke() == true)
                    {
                        channel.Abandon();
                        throw new OperationCanceledException();
                    }
                }
                channel.WaitClosed();
            }
            errorReader.Join(TimeSpan.FromSeconds(10));
            if (isCancelled?.Invoke() == true)
            {
                throw new OperationCanceledException();
            }
            result.Output = lines;
            result.Bytes = bytes;
            lock (error)
            {
                result.Error = error.ToString();
            }
            result.ConnectionLost = channel.ClosedByConnectionLoss || _session.IsConnectionClosed;
            if (channel.HasExitStatus)
            {
                result.ExitStatus = channel.ExitStatus;
            }
            else if (channel.ExitSignal != null)
            {
                result.ExitStatus = -1;
                result.Error += "ended by signal " + channel.ExitSignal;
            }
            return result;
        }
        catch (Exception) when (isCancelled?.Invoke() == true)
        {
            throw new OperationCanceledException();
        }
        finally
        {
            _channel = null;
            _session.PtyGate.EndPlainHelper();
        }
    }

    private static void ReadText(Stream stream, StringBuilder into)
    {
        try
        {
            using StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false);
            char[] buffer = new char[4096];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (into)
                {
                    into.Append(buffer, 0, read);
                }
            }
        }
        catch
        {
        }
    }
}
