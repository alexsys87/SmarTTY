
namespace SmarTTY.Ssh.QuickSync;

public struct RemoteWarningRecord(FileOperationFailure fileOperationFailure, string text, int value2)
{
    public FileOperationFailure Code = fileOperationFailure;

    public string Path = text;

    public int ErrorCode = value2;
}
