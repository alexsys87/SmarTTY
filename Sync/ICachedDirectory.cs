
namespace SmarTTY.Sync;

public interface ICachedDirectory
{
    bool IsFileUpToDate(string text, LocalFileSystem.FileStamp fileStamp);

    bool IsSymlinkUpToDate(string text, string text2);

    string[] GetEntryNames();

    void Register(int value2, string text);
}
