using System.Collections.Generic;
using System.Threading;

namespace ManagedLibSSH2;

internal interface IChannelPoller
{
    void PollChannel(ref byte[] buffer, ref bool flag, ref int value2, List<ThreadStart> list);
}
