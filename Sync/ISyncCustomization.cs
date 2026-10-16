
namespace SmarTTY.Sync;

public interface ISyncCustomization
{
    bool IsFileIgnored(LocalPath localPath, string text);

    bool IsDirectoryIgnored(LocalPath localPath, string text);

    IShellLauncher GetLauncher(string text);
}
