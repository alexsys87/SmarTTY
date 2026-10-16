using ManagedLibSSH2.QuickSync;
using System;
using System.Collections.Generic;

namespace SmarTTY.Sync;

internal interface ISyncEngine : IDisposable
{
    IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> items);

    RemoteFileSet GetRemoteFileSet();

    void Upload(List<SyncPlan.SyncAction> list, List<LocalPath> list2, ref TransferStatistics transferStatistics);
}
