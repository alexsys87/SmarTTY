using ManagedLibSSH2;
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

    private void OnDataReceived(object obj, byte[] buffer, int value2, int value3, int value4)
    {
        if (value4 == 0)
        {
            int num = this._stdout.Length;
            Array.Resize(ref this._stdout, num + value3);
            Array.Copy(buffer, value2, this._stdout, num, value3);
        }
        else
        {
            int num2 = this._stderr.Length;
            Array.Resize(ref this._stderr, num2 + value3);
            Array.Copy(buffer, value2, this._stderr, num2, value3);
        }
    }

    private void OnFinished(object obj)
    {
        _exitCode = _channel.ExitStatus;
        _exitedEvent.Set();
        _channel.Dispose();
        _channel = null;
    }

    public bool WaitForExit(int value2)
    {
        return _exitedEvent.WaitOne(value2, exitContext: false);
    }

    private List<string> SplitLines(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            return null;
        }
        List<string> list = new List<string>();
        using StreamReader streamReader = new StreamReader(new MemoryStream(buffer));
        while (true)
        {
            string text = streamReader.ReadLine();
            if (text == null)
            {
                break;
            }
            list.Add(text);
        }
        return list;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }

    public void SendInput(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _channel.Input.Write(bytes, 0, bytes.Length);
    }

    public void Interrupt()
    {
        _channel.Input.Write(new byte[1] { 3 }, 0, 1);
    }
}
