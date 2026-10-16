using System;

namespace SmarTTY.Sync;

[Flags]
public enum SyncOptions
{
    None = 0,
    OneFileSystem = 1,
    DeleteExtraneous = 2,
    SkipSymlinks = 8,
    SkipHardlinks = 0x10,
    DisableHelper = 0x20,
    DisableTar = 0x40,
    UseLauncher = 0x80
}
