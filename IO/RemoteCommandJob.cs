using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace SmarTTY.IO;

public class RemoteCommandJob : IRemoteCommand, IDisposable
{
    private ManualResetEvent _exitedEvent = new ManualResetEvent(initialState: false);

    private byte[] _stdout = new byte[0];

    private byte[] _stderr = new byte[0];

    private SshShellChannel _channel;

    private int _exitCode;

    public int ExitCode => _exitCode;

    public List<string> OutputLines => SplitLines(_stdout);

    public string ErrorText
    {
        get
        {
            if (_stderr.Length == 0)
            {
                return null;
            }
            return Encoding.UTF8.GetString(_stderr);
        }
    }

    public RemoteCommandJob(SshShellChannel sshShellChannel)
    {
        _channel = sshShellChannel;
        sshShellChannel.Finished += OnFinished;
        sshShellChannel.DataReceived += OnDataReceived;
        sshShellChannel.PollingEnabled = true;
    }

    private void OnDataReceived(object sender, byte[] buffer, int offset, int length, int dataType)
    {
        if (dataType == 0)
        {
            int oldLen = this._stdout.Length;
            Array.Resize(ref this._stdout, oldLen + length);
            Array.Copy(buffer, offset, this._stdout, oldLen, length);
        }
        else
        {
            int oldLen = this._stderr.Length;
            Array.Resize(ref this._stderr, oldLen + length);
            Array.Copy(buffer, offset, this._stderr, oldLen, length);
        }
    }

    private void OnFinished(object sender)
    {
        _exitCode = _channel.ExitStatus;
        _exitedEvent.Set();
        _channel.Dispose();
        _channel = null;
    }

    public bool WaitForExit(int timeoutMs)
    {
        return _exitedEvent.WaitOne(timeoutMs, exitContext: false);
    }

    private List<string> SplitLines(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            return null;
        }
        List<string> lines = new List<string>();
        using StreamReader streamReader = new StreamReader(new MemoryStream(buffer));
        while (true)
        {
            string line = streamReader.ReadLine();
            if (line == null)
            {
                break;
            }
            lines.Add(line);
        }
        return lines;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }

    public void SendInput(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        _channel.Input.Write(bytes, 0, bytes.Length);
    }

    public void Interrupt()
    {
        _channel.Input.Write(new byte[1] { 3 }, 0, 1);
    }
}
