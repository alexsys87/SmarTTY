
namespace SmarTTY.Sync;

public enum SyncStage
{
    Initializing,
    BuildingFileList,
    Transferring,
    UpdatingSymlinks,
    DeletingOldContents
}
