using System;

namespace SmarTTY.Sync;

internal interface IRemoteFileStream : IDisposable
{
    int ErrorCode { get; }

    long ModifiedTime { get; }

    int ReadChunk(ref byte[] buffer);
}
