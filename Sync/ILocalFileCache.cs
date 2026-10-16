using System;

namespace SmarTTY.Sync;

public interface ILocalFileCache : IDisposable
{
    ICachedDirectory GetDirectory(LocalPath localPath);

    void StoreFileStamp(int value2, LocalFileSystem.FileStamp fileStamp);

    void StoreSymlink(LocalPath localPath, string text);
}
