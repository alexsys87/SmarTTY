using SmarTTY.Ssh.QuickSync;

namespace SmarTTY.Sync;

internal interface ISyncDiagnostics : ISyncEvents
{
    void OnPlanCreated(SyncPlan syncPlan);

    void OnRemoteFileSet(ISyncEngine syncEngine, RemoteFileSet remoteFileSet);

    void OnScanFinished();
}
