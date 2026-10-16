
namespace SmarTTY.Sync;

public interface IHelperLocator
{
    string FindHelper(string text);

    void InstallHelper(IHelperInstaller helperInstaller);
}
