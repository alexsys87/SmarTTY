
namespace SmarTTY.Terminal;

internal enum CompletionTarget
{
    Command,
    Path,
    Directory,
    // $NAME or ${NAME}
    Variable,
    // the host of ssh, sftp, ping and the like (with an optional user@)
    Host,
    // an argument of scp or rsync: a local path or "host:"
    HostOrPath
}
