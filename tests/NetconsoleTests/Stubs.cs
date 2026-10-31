// Stand-ins for the Windows-only types the settings mention, so the real classes can run on any machine.
namespace System.IO.Ports
{
    public enum Parity { None, Odd, Even, Mark, Space }

    public enum StopBits { None, One, Two, OnePointFive }

    public enum Handshake { None, XOnXOff, RequestToSend, RequestToSendXOnXOff }
}
