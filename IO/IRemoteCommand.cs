using System;
using System.Collections.Generic;

namespace SmarTTY.IO;

public interface IRemoteCommand : IDisposable
{
    int ExitCode { get; }

    List<string> OutputLines { get; }

    string ErrorText { get; }

    bool WaitForExit(int value);

    void SendInput(string text);
}
