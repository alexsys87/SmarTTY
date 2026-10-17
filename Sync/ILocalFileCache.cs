using System;

namespace SmarTTY.Sync;

public interface ILocalFileCache : IDisposable
{
    ICachedDirectory GetDirectory(LocalPath localPath);

    void StoreFileStamp(int value, LocalFileSystem.FileStamp fileStamp);

    void StoreSymlink(LocalPath localPath, string text);
}
