
namespace SmarTTY.Sync;

public interface ISyncEvents
{
    IPermissionProvider PermissionProvider { get; }

    void OnLog(SyncLogMessage syncLogMessage);

    void OnProgress(SyncProgress syncProgress);

    void OnWarning(SyncWarning syncWarning);
}
