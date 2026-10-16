
namespace ManagedLibSSH2;

public interface IAsyncChannel
{
    bool PollingEnabled { get; set; }

    event DataReceivedHandler DataReceived;

    event ChannelEventHandler Finished;
}
