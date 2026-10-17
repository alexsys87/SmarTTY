using SmarTTY.Ssh.QuickSync;
using System;
using System.Collections.Generic;

namespace SmarTTY.Sync;

internal interface ISyncEngine : IDisposable
{
    IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> actions);

    RemoteFileSet GetRemoteFileSet();

    void Upload(List<SyncPlan.SyncAction> actions, List<LocalPath> localPaths, ref TransferStatistics transferStatistics);
}
