
namespace SmarTTY.Transport;

public interface ITransportFactory
{
    IDeviceTransport CreateTransport();
}
