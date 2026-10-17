using System;

namespace SmarTTY.Sync;

internal interface IRemoteFileSource : IDisposable
{
    IRemoteFileStream OpenFile(int value);
}
