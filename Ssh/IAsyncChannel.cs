
namespace SmarTTY.Ssh;

public interface IAsyncChannel
{
    bool PollingEnabled { get; set; }

    event DataReceivedHandler DataReceived;

    event ChannelEventHandler Finished;
}
