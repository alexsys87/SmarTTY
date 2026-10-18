
namespace SmarTTY.Sync;

public interface ICachedDirectory
{
    bool IsFileUpToDate(string text, LocalFileSystem.FileStamp fileStamp);

    bool IsSymlinkUpToDate(string text, string value);

    string[] GetEntryNames();

    void Register(int value, string text);
}
