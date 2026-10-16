
namespace SmarTTY.Ssh;

public enum RemotePathProblem
{
    EmptyPath,
    EndsWithSlash,
    NotAbsolute,
    FileNotWritable,
    DirectoryNotWritable
}
