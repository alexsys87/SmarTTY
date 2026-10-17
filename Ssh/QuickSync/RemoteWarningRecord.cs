namespace SmarTTY.Ssh.QuickSync;

public struct RemoteWarningRecord(FileOperationFailure fileOperationFailure, string path, int errorCode)
{
    public FileOperationFailure Code = fileOperationFailure;

    public string Path = path;

    public int ErrorCode = errorCode;
}
