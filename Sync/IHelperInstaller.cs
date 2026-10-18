using System;

namespace SmarTTY.Sync;

public interface IHelperInstaller
{
    string HostName { get; }

    void Install(Action<string> action);
}
