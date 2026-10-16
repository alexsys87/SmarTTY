
namespace SmarTTY.Sync;

public class SyncProgress
{
    public string CurrentFile;

    public long Transferred;

    public long Total;

    public SyncStage Stage;

    public double Fraction
    {
        get
        {
            if (Total == 0L)
            {
                return -1.0;
            }
            return (double)Transferred / (double)Total;
        }
    }

    public string StageDescription => Stage switch
    {
        SyncStage.Initializing => "Initializing...",
        SyncStage.BuildingFileList => "Building file list...",
        SyncStage.Transferring => "Transferring files...",
        SyncStage.UpdatingSymlinks => "Updating symlinks...",
        SyncStage.DeletingOldContents => "Deleting previous contents...",
        _ => "Synchronizing...",
    };
}
