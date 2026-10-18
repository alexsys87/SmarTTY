// The settings classes use the enums of System.IO.Ports, which modern .NET ships as a separate package. The
// application targets .NET Framework where they are built in; this harness only needs the names.
namespace System.IO.Ports
{
    public enum Parity { None, Odd, Even, Mark, Space }

    public enum StopBits { None, One, Two, OnePointFive }

    public enum Handshake { None, XOnXOff, RequestToSend, RequestToSendXOnXOff }
}
