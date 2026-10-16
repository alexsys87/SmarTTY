
namespace SmarTTY.Sync;

public interface IPermissionProvider
{
    int GetFilePermissions(LocalPath localPath);
}
