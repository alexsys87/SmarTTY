using ManagedLibSSH2.QuickSync;

namespace SmarTTY.Sync;

public struct SyncSettings
{
    public SyncDirection Direction;

    public string RemotePath;

    public string LocalPath;

    public QuickSyncFilter[] DirectoryFilters;

    public QuickSyncFilter[] FileFilters;

    public SyncOptions Options;

    public QuickSyncOverwriteTrigger QuickSyncOverwriteTrigger;

    public ClockOffsetInfo ClockOffset;
}
