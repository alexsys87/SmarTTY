using ManagedLibSSH2.QuickSync;

namespace SmarTTY.Sync;

internal interface ISyncDiagnostics : ISyncEvents
{
    void OnPlanCreated(SyncPlan syncPlan);

    void OnRemoteFileSet(ISyncEngine syncEngine, RemoteFileSet remoteFileSet);

    void OnScanFinished();
}
