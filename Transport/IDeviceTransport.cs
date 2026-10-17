using System;
using System.IO;

namespace SmarTTY.Transport;

public interface IDeviceTransport : IDisposable
{
    string ConnectingMessage { get; }

    void Open(IHostNameResolver hostNameResolver);

    void Close();

    Stream GetStream();

    string GetDisplayName(bool includeParameters);
}
