namespace SmarTTY.Ssh.QuickSync;

public struct QuickSyncWarning
{
    public QuickSyncWarningType Type;

    public string Path;

    public int ErrorCode;

    public string ErrorSuffix
    {
        get
        {
            if (ErrorCode == 0)
            {
                return "";
            }
            return $", error code {ErrorCode}";
        }
    }

    private string Description
    {
        get
        {
            switch (Type)
            {
                case QuickSyncWarningType.UnreadableRemoteDirectory:
                    return "Unreadable remote directory";
                case QuickSyncWarningType.UnreadableSourceSymlink:
                    return "Unreadable symbolic link";
                case QuickSyncWarningType.CannotCreateTargetDirectory:
                    return "Failed to create target directory";
                case QuickSyncWarningType.CannotDeleteTargetFile:
                    return "Failed to delete target file";
                case QuickSyncWarningType.CannotRemoveTargetDirectory:
                    return "Failed to remove target directory";
                case QuickSyncWarningType.CannotExpandRecursiveDirectorySymlink:
                    return "Failed to expand directory symlink";
                case QuickSyncWarningType.CreateSymlinkFailed:
                    return "Failed to create symlink";
                case QuickSyncWarningType.CreateHardlinkFailed:
                    return "Failed to create hardlink";
                case QuickSyncWarningType.CopyFileFailed:
                    return "Failed to copy file";
                case QuickSyncWarningType.UnreadableLocalDirectory:
                    return "Unreadable local directory";
                case QuickSyncWarningType.CannotUpdateLocalFile:
                    return "Failed to update file";
                case QuickSyncWarningType.CannotCreateTargetFile:
                    return "Failed to create file";
                case QuickSyncWarningType.CannotReadRemoteFile:
                case QuickSyncWarningType.CannotReadLocalFile:
                    return "Failed to read file";
                case QuickSyncWarningType.StderrLine:
                    return "Stderr line received";
                case QuickSyncWarningType.UnknownRemoteAction:
                    return "Unknown remote action";
                case QuickSyncWarningType.CannotStatLocalSymlink:
                    return "Failed to resolve local symlink";
                case QuickSyncWarningType.RemoteOutputCorrupt:
                    return "Remote output is corrupt";
                case QuickSyncWarningType.RemoteCommandFailed:
                    return "Remote command failed";
                case QuickSyncWarningType.SkippingUnsupportedPath:
                    return "Skipping unsupported path";
                case QuickSyncWarningType.UsingSlowEngine:
                    return "Using slow synchronization engine";
                default:
                    return Type.ToString();
            }
        }
    }

    public QuickSyncWarning(RemoteWarningRecord remoteWarningRecord)
    {
        Path = remoteWarningRecord.Path;
        ErrorCode = remoteWarningRecord.ErrorCode;
        Type = (QuickSyncWarningType)remoteWarningRecord.Code;
    }

    public QuickSyncWarning(QuickSyncWarningType quickSyncWarningType, string path, int errorCode)
    {
        Type = quickSyncWarningType;
        this.Path = path;
        this.ErrorCode = errorCode;
    }

    public override string ToString()
    {
        return Description + " '" + Path + "'" + ErrorSuffix;
    }
}
