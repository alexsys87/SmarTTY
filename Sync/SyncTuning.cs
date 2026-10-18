
namespace SmarTTY.Sync;

public struct SyncTuning
{
    public int BufferSize;

    public int ExitTimeoutMs;

    public bool KeepTempScripts;

    public static SyncTuning Default = new SyncTuning
    {
        BufferSize = 1048576,
        ExitTimeoutMs = 1000
    };
}
