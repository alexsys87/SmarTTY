
namespace SmarTTY.Ssh.QuickSync;

internal enum SyncRecordType
{
    None,
    FileSetStart,
    DirectoryBody,
    DirectoryError,
    Padding,
    File,
    Directory,
    DirectorySymlink,
    QueuedSymlink,
    UnreadableSymlink,
    FileSymlink,
    DirectorySymlinkText,
    EndOfDirectory,
    ResolvedSymlinkToFile,
    ResolvedSymlinkInTree,
    ResolvedSymlinkToDirectory,
    ResolvedSymlinkDirectoryBody,
    EndOfFileSet
}
