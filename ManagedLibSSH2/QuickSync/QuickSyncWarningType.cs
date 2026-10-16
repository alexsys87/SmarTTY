
namespace ManagedLibSSH2.QuickSync;

public enum QuickSyncWarningType
{
    CannotCreateTargetDirectory = 0,
    CannotDeleteTargetFile = 1,
    CannotRemoveTargetDirectory = 2,
    CannotExpandRecursiveDirectorySymlink = 3,
    CreateSymlinkFailed = 4,
    CreateHardlinkFailed = 5,
    CopyFileFailed = 6,
    UnreadableSourceSymlink = 128,
    UnreadableRemoteDirectory = 129,
    UnreadableLocalDirectory = 130,
    CannotUpdateLocalFile = 131,
    CannotCreateTargetFile = 132,
    CannotReadRemoteFile = 133,
    CannotReadLocalFile = 134,
    StderrLine = 135,
    UnknownRemoteAction = 136,
    CannotStatLocalSymlink = 137,
    RemoteOutputCorrupt = 138,
    RemoteCommandFailed = 139,
    SkippingUnsupportedPath = 140,
    UsingSlowEngine = 141
}
