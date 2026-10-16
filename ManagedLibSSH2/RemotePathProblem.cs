
namespace ManagedLibSSH2;

public enum RemotePathProblem
{
    EmptyPath,
    EndsWithSlash,
    NotAbsolute,
    FileNotWritable,
    DirectoryNotWritable
}
