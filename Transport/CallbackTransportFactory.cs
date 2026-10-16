
namespace SmarTTY.Transport;

internal class CallbackTransportFactory : ITransportFactory
{
    public delegate IDeviceTransport TransportCallback();

    private readonly TransportCallback _callback;

    public CallbackTransportFactory(TransportCallback callback)
    {
        _callback = callback;
    }

    public IDeviceTransport CreateTransport()
    {
        return _callback();
    }
}
