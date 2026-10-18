
namespace VisualTTY;

public interface ISSHCommandRunner
{
    string[] RunCommand(string command);
}
