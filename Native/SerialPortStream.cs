using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Native;

public class SerialPortStream : Stream, IDisposable
{
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;

        public uint ReadTotalTimeoutMultiplier;

        public uint ReadTotalTimeoutConstant;

        public uint WriteTotalTimeoutMultiplier;

        public uint WriteTotalTimeoutConstant;
    }

    public enum DcbParity : byte
    {
        None,
        Odd,
        Even,
        Mark,
        Space
    }

    public enum DcbStopBits : byte
    {
        One,
        OnePointFive,
        Two
    }

    public enum RtsControl
    {
        Disable,
        Enable,
        Handshake,
        Toggle
    }

    public enum DtrControl
    {
        Disable,
        Enable,
        Handshake
    }

    internal struct Dcb
    {
        internal uint DcbLength;

        internal uint BaudRate;

        private BitVector32 _flags;

        private ushort _reserved;

        internal ushort XonLim;

        internal ushort XoffLim;

        internal byte ByteSize;

        internal DcbParity Parity;

        internal DcbStopBits StopBits;

        internal sbyte XonChar;

        internal sbyte XoffChar;

        internal sbyte ErrorChar;

        internal sbyte EofChar;

        internal sbyte EvtChar;

        private ushort _reserved1;

        private static readonly int _binaryMask;

        private static readonly int _parityMask;

        private static readonly int _outxCtsFlowMask;

        private static readonly int _outxDsrFlowMask;

        private static readonly BitVector32.Section _dtrControlSection;

        private static readonly int _dsrSensitivityMask;

        private static readonly int _txContinueOnXoffMask;

        private static readonly int _outXMask;

        private static readonly int _inXMask;

        private static readonly int _errorCharMask;

        private static readonly int _nullMask;

        private static readonly BitVector32.Section _rtsControlSection;

        private static readonly int _abortOnErrorMask;

        public bool Binary
        {
            get
            {
                return _flags[_binaryMask];
            }
            set
            {
                _flags[_binaryMask] = value;
            }
        }

        public bool ParityCheck
        {
            get
            {
                return _flags[_parityMask];
            }
            set
            {
                _flags[_parityMask] = value;
            }
        }

        public bool OutxCtsFlow
        {
            get
            {
                return _flags[_outxCtsFlowMask];
            }
            set
            {
                _flags[_outxCtsFlowMask] = value;
            }
        }

        public bool OutxDsrFlow
        {
            get
            {
                return _flags[_outxDsrFlowMask];
            }
            set
            {
                _flags[_outxDsrFlowMask] = value;
            }
        }

        public DtrControl DtrControlMode
        {
            get
            {
                return (DtrControl)_flags[_dtrControlSection];
            }
            set
            {
                _flags[_dtrControlSection] = (int)value;
            }
        }

        public bool DsrSensitivity
        {
            get
            {
                return _flags[_dsrSensitivityMask];
            }
            set
            {
                _flags[_dsrSensitivityMask] = value;
            }
        }

        public bool TxContinueOnXoff
        {
            get
            {
                return _flags[_txContinueOnXoffMask];
            }
            set
            {
                _flags[_txContinueOnXoffMask] = value;
            }
        }

        public bool OutX
        {
            get
            {
                return _flags[_outXMask];
            }
            set
            {
                _flags[_outXMask] = value;
            }
        }

        public bool InX
        {
            get
            {
                return _flags[_inXMask];
            }
            set
            {
                _flags[_inXMask] = value;
            }
        }

        public bool ErrorCharEnabled
        {
            get
            {
                return _flags[_errorCharMask];
            }
            set
            {
                _flags[_errorCharMask] = value;
            }
        }

        public bool NullDiscard
        {
            get
            {
                return _flags[_nullMask];
            }
            set
            {
                _flags[_nullMask] = value;
            }
        }

        public RtsControl RtsControlMode
        {
            get
            {
                return (RtsControl)_flags[_rtsControlSection];
            }
            set
            {
                _flags[_rtsControlSection] = (int)value;
            }
        }

        public bool AbortOnError
        {
            get
            {
                return _flags[_abortOnErrorMask];
            }
            set
            {
                _flags[_abortOnErrorMask] = value;
            }
        }

        static Dcb()
        {
            _binaryMask = BitVector32.CreateMask();
            _parityMask = BitVector32.CreateMask(_binaryMask);
            _outxCtsFlowMask = BitVector32.CreateMask(_parityMask);
            _outxDsrFlowMask = BitVector32.CreateMask(_outxCtsFlowMask);
            _dsrSensitivityMask = BitVector32.CreateMask(BitVector32.CreateMask(BitVector32.CreateMask(_outxDsrFlowMask)));
            _txContinueOnXoffMask = BitVector32.CreateMask(_dsrSensitivityMask);
            _outXMask = BitVector32.CreateMask(_txContinueOnXoffMask);
            _inXMask = BitVector32.CreateMask(_outXMask);
            _errorCharMask = BitVector32.CreateMask(_inXMask);
            _nullMask = BitVector32.CreateMask(_errorCharMask);
            _abortOnErrorMask = BitVector32.CreateMask(BitVector32.CreateMask(BitVector32.CreateMask(_nullMask)));
            BitVector32.Section previous = BitVector32.CreateSection(1);
            previous = BitVector32.CreateSection(1, previous);
            previous = BitVector32.CreateSection(1, previous);
            previous = BitVector32.CreateSection(1, previous);
            _dtrControlSection = BitVector32.CreateSection(2, previous);
            previous = BitVector32.CreateSection(1, _dtrControlSection);
            previous = BitVector32.CreateSection(1, previous);
            previous = BitVector32.CreateSection(1, previous);
            previous = BitVector32.CreateSection(1, previous);
            previous = BitVector32.CreateSection(1, previous);
            previous = BitVector32.CreateSection(1, previous);
            _rtsControlSection = BitVector32.CreateSection(3, previous);
            BitVector32.CreateSection(1, _rtsControlSection);
        }
    }

    public enum CommFunction : uint
    {
        ClearBreak = 9u,
        ClearDtr = 6u,
        ClearRts = 4u,
        SetBreak = 8u,
        SetDtr = 5u,
        SetRts = 3u,
        SetXoff = 1u,
        SetXon = 2u
    }

    private const int ErrorIoPending = 997;

    private const int ErrorOperationAborted = 995;

    // OVERLAPPED.Internal while the driver still owns the operation.
    private const long StatusPending = 0x103;

    // How long Close waits for a cancelled read or write to finish before closing the handle under it.
    private const int CloseWaitMs = 2000;

    private const sbyte XonCharacter = 0x11;

    private const sbyte XoffCharacter = 0x13;

    private SafeFileHandle _handle;

    private readonly string _portName;

    private int _closed;

    // The state of the output lines as this program last set them (Windows cannot read them back). A port opened without
    // explicit settings keeps the driver's behavior, which raises both lines.
    private volatile bool _dtr = true;

    private volatile bool _rts = true;

    private volatile bool _rtsIsAutomatic;

    private object _readLock = new object();

    private object _writeLock = new object();

    private ManualResetEvent _readEvent = new ManualResetEvent(initialState: false);

    private ManualResetEvent _writeEvent = new ManualResetEvent(initialState: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length
    {
        get
        {
            throw new NotSupportedException();
        }
    }

    public override long Position
    {
        get
        {
            throw new NotSupportedException();
        }
        set
        {
            throw new NotSupportedException();
        }
    }

    public bool ReturnOnEmptyRead { get; set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern SafeFileHandle CreateFile([MarshalAs(UnmanagedType.LPTStr)] string portName, [MarshalAs(UnmanagedType.U4)] FileAccess fileAccess, [MarshalAs(UnmanagedType.U4)] FileShare fileShare, IntPtr securityAttributes, [MarshalAs(UnmanagedType.U4)] FileMode fileMode, [MarshalAs(UnmanagedType.U4)] FileAttributes fileAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeHandle safeHandle, [In] IntPtr overlapped, out int bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeHandle safeHandle, IntPtr buffer, int count, out int bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeHandle safeHandle, IntPtr buffer, int count, out int bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeHandle safeHandle, ref CommTimeouts commTimeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle safeFileHandle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle safeFileHandle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommModemStatus(SafeFileHandle safeFileHandle, out uint modemStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ClearCommError(SafeFileHandle safeFileHandle, out uint errors, IntPtr comStat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeHandle safeHandle, IntPtr overlapped);

    public SerialPortStream(string portName, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings = null)
    {
        _portName = portName;
        // COM10 and above can only be opened through the device namespace; a name that already has the prefix is kept.
        string devicePath = portName.StartsWith("\\\\.\\", StringComparison.Ordinal) ? portName : "\\\\.\\" + portName;
        _handle = CreateFile(devicePath, FileAccess.ReadWrite, FileShare.None, IntPtr.Zero, FileMode.Open, (FileAttributes)1073741952, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Cannot open " + portName + ": " + new Win32Exception(error).Message);
        }
        try
        {
            if (advancedSerialPortSettings != null)
            {
                _dtr = advancedSerialPortSettings.DtrOnOpen;
                _rts = advancedSerialPortSettings.RtsOnOpen;
                ApplyAdvancedSettings(advancedSerialPortSettings);
            }
            ApplyTimeouts();
        }
        catch
        {
            // The port is opened exclusively: a handle left open by a failed setup would lock it until the finalizer runs
            // and every retry would then fail with "access denied".
            _handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies new line settings to the open port without closing it. The DTR and RTS lines keep their state, so a change
    /// of the baud rate does not restart a board that resets on DTR.
    /// </summary>
    public void Reconfigure(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings)
    {
        if (advancedSerialPortSettings == null)
        {
            throw new ArgumentNullException(nameof(advancedSerialPortSettings));
        }
        ApplyAdvancedSettings(advancedSerialPortSettings);
        ApplyTimeouts();
    }

    /// <summary>Whether DTR is raised, as last set by this program.</summary>
    public bool DtrAsserted => _dtr;

    /// <summary>Whether RTS is raised, as last set by this program. Meaningless while <see cref="RtsIsAutomatic"/>.</summary>
    public bool RtsAsserted => _rts;

    /// <summary>True while RTS/CTS flow control owns the RTS line, so it cannot be set by hand.</summary>
    public bool RtsIsAutomatic => _rtsIsAutomatic;

    public void SetDtr(bool asserted)
    {
        EscapeFunction(asserted ? CommFunction.SetDtr : CommFunction.ClearDtr);
        _dtr = asserted;
    }

    public void SetRts(bool asserted)
    {
        if (_rtsIsAutomatic)
        {
            throw new InvalidOperationException("RTS is controlled by hardware flow control; choose another flow control setting to set it by hand.");
        }
        EscapeFunction(asserted ? CommFunction.SetRts : CommFunction.ClearRts);
        _rts = asserted;
    }

    /// <summary>Holds the transmit line in the break state for the given time (blocks the calling thread).</summary>
    public void SendBreak(int durationMs)
    {
        EscapeFunction(CommFunction.SetBreak);
        try
        {
            Thread.Sleep(Math.Max(10, Math.Min(durationMs, 5000)));
        }
        finally
        {
            EscapeFunction(CommFunction.ClearBreak);
        }
    }

    /// <summary>
    /// Reads the input modem lines and collects (and clears) the driver's pending line errors. False when the port is
    /// closed or the driver refuses, which is not worth reporting from a periodic check.
    /// </summary>
    public bool TryReadLineStatus(out SerialModemLines lines, out uint errorFlags)
    {
        lines = default(SerialModemLines);
        errorFlags = 0;
        try
        {
            if (_handle == null || _handle.IsClosed || !GetCommModemStatus(_handle, out uint modemStatus))
            {
                return false;
            }
            lines = SerialModemLines.FromFlags(modemStatus);
            if (!ClearCommError(_handle, out errorFlags, IntPtr.Zero))
            {
                errorFlags = 0;
            }
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static SerialStopBitsKind StopBitsKindOf(DcbStopBits stopBits)
    {
        switch (stopBits)
        {
            case DcbStopBits.Two:
                return SerialStopBitsKind.Two;
            case DcbStopBits.OnePointFive:
                return SerialStopBitsKind.OnePointFive;
            default:
                return SerialStopBitsKind.One;
        }
    }

    private void ApplyAdvancedSettings(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings)
    {
        SerialStopBitsKind stopBits = SerialPortRules.StopBitsKindFrom((int)advancedSerialPortSettings.StopBits);
        string problem = SerialPortRules.ValidateFrame(advancedSerialPortSettings.DataBits, stopBits);
        if (problem == null && (advancedSerialPortSettings.BaudRate < SerialPortRules.MinBaudRate || advancedSerialPortSettings.BaudRate > SerialPortRules.MaxBaudRate))
        {
            problem = "The baud rate must be between " + SerialPortRules.MinBaudRate + " and " + SerialPortRules.MaxBaudRate + ".";
        }
        if (problem != null)
        {
            throw new ArgumentException("Invalid serial port settings for " + _portName + ": " + problem);
        }

        Dcb dcb = new Dcb
        {
            DcbLength = (uint)Marshal.SizeOf(typeof(Dcb))
        };
        if (!GetCommState(_handle, ref dcb))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the settings of " + _portName + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        dcb.BaudRate = (uint)advancedSerialPortSettings.BaudRate;
        dcb.ByteSize = (byte)advancedSerialPortSettings.DataBits;
        dcb.Parity = (DcbParity)advancedSerialPortSettings.Parity;
        switch (stopBits)
        {
            case SerialStopBitsKind.Two:
                dcb.StopBits = DcbStopBits.Two;
                break;
            case SerialStopBitsKind.OnePointFive:
                dcb.StopBits = DcbStopBits.OnePointFive;
                break;
            default:
                dcb.StopBits = DcbStopBits.One;
                break;
        }

        Handshake flow = advancedSerialPortSettings.FlowControl;
        bool software = flow == Handshake.XOnXOff || flow == Handshake.RequestToSendXOnXOff;
        bool hardware = flow == Handshake.RequestToSend || flow == Handshake.RequestToSendXOnXOff;
        dcb.Binary = true;
        // A line error must not abort reading: it is counted by the status check instead of dropping the connection.
        dcb.AbortOnError = false;
        dcb.ErrorCharEnabled = false;
        dcb.NullDiscard = false;
        dcb.ParityCheck = advancedSerialPortSettings.Parity != Parity.None;
        dcb.OutX = software;
        dcb.InX = software;
        dcb.TxContinueOnXoff = software;
        if (software)
        {
            // Some drivers report zero for both characters; SetCommState refuses XON/XOFF with equal characters.
            dcb.XonChar = XonCharacter;
            dcb.XoffChar = XoffCharacter;
        }
        dcb.OutxCtsFlow = hardware;
        dcb.OutxDsrFlow = false;
        // Left on by another program, it would make the driver drop everything received while DSR is low.
        dcb.DsrSensitivity = false;
        dcb.DtrControlMode = _dtr ? DtrControl.Enable : DtrControl.Disable;
        dcb.RtsControlMode = hardware ? RtsControl.Handshake : (_rts ? RtsControl.Enable : RtsControl.Disable);

        if (!SetCommState(_handle, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            string frame = SerialPortRules.FrameName(advancedSerialPortSettings.DataBits, (int)advancedSerialPortSettings.Parity, stopBits);
            throw new Win32Exception(error, "Cannot apply the settings to " + _portName + " (" + advancedSerialPortSettings.BaudRate + " baud, " + frame + "): "
                + new Win32Exception(error).Message + ". The adapter or driver may not support them.");
        }
        // Only now: while the driver still works by the old settings, the RTS state shown must be the old one, too.
        _rtsIsAutomatic = hardware;
    }

    /// <summary>
    /// The line settings the port works with now (read from the driver), for a port that was opened without explicit
    /// settings: changing one value then keeps the rest instead of replacing it with defaults.
    /// </summary>
    public DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings ReadCurrentSettings()
    {
        Dcb dcb = new Dcb
        {
            DcbLength = (uint)Marshal.SizeOf(typeof(Dcb))
        };
        if (!GetCommState(_handle, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Cannot query the settings of " + _portName + ": " + new Win32Exception(error).Message);
        }
        bool hardware = dcb.OutxCtsFlow || dcb.RtsControlMode == RtsControl.Handshake;
        bool software = dcb.InX || dcb.OutX;
        Handshake flow = hardware ? (software ? Handshake.RequestToSendXOnXOff : Handshake.RequestToSend) : (software ? Handshake.XOnXOff : Handshake.None);
        StopBits stopBits = dcb.StopBits == DcbStopBits.Two ? StopBits.Two : (dcb.StopBits == DcbStopBits.OnePointFive ? StopBits.OnePointFive : StopBits.One);
        return new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings
        {
            BaudRate = dcb.BaudRate > 0 ? (int)dcb.BaudRate : 115200,
            DataBits = dcb.ByteSize > 0 ? dcb.ByteSize : 8,
            Parity = (Parity)(int)dcb.Parity,
            StopBits = stopBits,
            FlowControl = flow,
            DtrOnOpen = _dtr,
            RtsOnOpen = _rts
        };
    }

    /// <summary>
    /// Reads return as soon as bytes arrive; a write fails after the time its bytes need on the wire plus a stall allowance,
    /// instead of blocking forever on a port that stopped accepting data.
    /// </summary>
    private void ApplyTimeouts()
    {
        Dcb dcb = new Dcb
        {
            DcbLength = (uint)Marshal.SizeOf(typeof(Dcb))
        };
        int writeTimeoutMs;
        if (GetCommState(_handle, ref dcb) && dcb.BaudRate > 0)
        {
            writeTimeoutMs = SerialPortRules.WriteTimeoutMs((int)dcb.BaudRate, dcb.ByteSize, dcb.Parity != DcbParity.None, StopBitsKindOf(dcb.StopBits),
                SerialPortRules.WriteChunkBytes, SerialPortRules.WriteStallTimeoutMs);
        }
        else
        {
            writeTimeoutMs = SerialPortRules.WriteTimeoutMs(0, 8, false, SerialStopBitsKind.One, SerialPortRules.WriteChunkBytes, SerialPortRules.WriteStallTimeoutMs);
        }
        CommTimeouts commTimeouts = new CommTimeouts
        {
            ReadIntervalTimeout = 1u,
            WriteTotalTimeoutConstant = (uint)writeTimeoutMs
        };
        if (!SetCommTimeouts(_handle, ref commTimeouts))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Cannot set the timeouts of " + _portName + ": " + new Win32Exception(error).Message);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    private static void CheckBufferArguments(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }
        if (offset < 0 || count < 0 || count > buffer.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The offset and count do not describe a range of the buffer.");
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new ObjectDisposedException(GetType().Name, _portName + " is closed.");
        }
    }

    /// <summary>
    /// Waits for an overlapped operation that the driver accepted and returns its byte count. The OVERLAPPED structure and
    /// the data buffer belong to the driver until the operation completes; <paramref name="ownedByDriver"/> stays true when
    /// the port was closed under an operation the driver never finished, and the caller must then not free that memory.
    /// </summary>
    private int CompleteOverlapped(IntPtr overlappedPtr, ManualResetEvent completionEvent, bool isWrite, out bool ownedByDriver)
    {
        ownedByDriver = true;
        completionEvent.WaitOne();
        if (Marshal.ReadIntPtr(overlappedPtr).ToInt64() == StatusPending)
        {
            // Woken by Close, not by the driver.
            throw new ObjectDisposedException(GetType().Name, _portName + " is closed.");
        }
        ownedByDriver = false;
        if (!GetOverlappedResult(_handle, overlappedPtr, out int bytesTransferred, wait: false))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorOperationAborted && Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(GetType().Name, _portName + " is closed.");
            }
            throw isWrite ? WriteFailure(error) : ReadFailure(error);
        }
        return bytesTransferred;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        CheckBufferArguments(buffer, offset, count);
        if (count == 0)
        {
            return 0;
        }
        lock (_readLock)
        {
            ThrowIfClosed();
            NativeOverlapped nativeOverlapped = new NativeOverlapped
            {
                EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
            };
            IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            IntPtr bufferPtr = Marshal.AllocHGlobal(count);
            bool ownedByDriver = false;
            try
            {
                int bytesRead;
                do
                {
                    Marshal.StructureToPtr((object)nativeOverlapped, overlappedPtr, false);
                    if (!ReadFile(_handle, bufferPtr, count, out bytesRead, overlappedPtr) && Marshal.GetLastWin32Error() != ErrorIoPending)
                    {
                        throw ReadFailure(Marshal.GetLastWin32Error());
                    }
                    bytesRead = CompleteOverlapped(overlappedPtr, _readEvent, isWrite: false, out ownedByDriver);
                }
                while (bytesRead == 0 && !ReturnOnEmptyRead);
                Marshal.Copy(bufferPtr, buffer, offset, bytesRead);
                return bytesRead;
            }
            finally
            {
                // Freeing memory the driver may still write into would corrupt the heap: leak it instead (only happens
                // when a driver ignores the cancellation on close).
                if (!ownedByDriver)
                {
                    Marshal.FreeHGlobal(overlappedPtr);
                    Marshal.FreeHGlobal(bufferPtr);
                }
            }
        }
    }

    private int WriteChunk(byte[] buffer, int offset, int count)
    {
        lock (_writeLock)
        {
            ThrowIfClosed();
            NativeOverlapped nativeOverlapped = new NativeOverlapped
            {
                EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle()
            };
            IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            IntPtr bufferPtr = Marshal.AllocHGlobal(count);
            bool ownedByDriver = false;
            try
            {
                Marshal.StructureToPtr((object)nativeOverlapped, overlappedPtr, false);
                Marshal.Copy(buffer, offset, bufferPtr, count);
                if (!WriteFile(_handle, bufferPtr, count, out _, overlappedPtr) && Marshal.GetLastWin32Error() != ErrorIoPending)
                {
                    throw WriteFailure(Marshal.GetLastWin32Error());
                }
                return CompleteOverlapped(overlappedPtr, _writeEvent, isWrite: true, out ownedByDriver);
            }
            finally
            {
                if (!ownedByDriver)
                {
                    Marshal.FreeHGlobal(overlappedPtr);
                    Marshal.FreeHGlobal(bufferPtr);
                }
            }
        }
    }

    private Win32Exception ReadFailure(int error)
    {
        return new Win32Exception(error, "Cannot read from " + _portName + ": " + new Win32Exception(error).Message + " (the adapter may have been unplugged)");
    }

    private Win32Exception WriteFailure(int error)
    {
        return new Win32Exception(error, "Cannot write to " + _portName + ": " + new Win32Exception(error).Message + " (the adapter may have been unplugged)");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckBufferArguments(buffer, offset, count);
        int chunkWritten;
        for (int written = 0; written < count; written += chunkWritten)
        {
            // Small chunks: each has its own write timeout, so a stalled port is noticed within seconds and a long
            // transfer is not cut off by the time a big buffer legitimately needs.
            chunkWritten = WriteChunk(buffer, offset + written, Math.Min(SerialPortRules.WriteChunkBytes, count - written));
            if (chunkWritten <= 0)
            {
                throw new IOException("Cannot write to " + _portName + ": the device did not accept any data for " + SerialPortRules.WriteStallTimeoutMs / 1000
                    + " s. Check the cable, whether the device is powered, and the flow control setting (a device that holds CTS low or sent XOFF blocks the port).");
            }
        }
    }

    /// <summary>
    /// Cancels the pending read and write, waits for the driver to finish them and only then closes the handle, so a
    /// completion can never land in memory that has already been freed.
    /// </summary>
    public override void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            bool readIdle = WaitUntilIdle(_readLock);
            bool writeIdle = WaitUntilIdle(_writeLock);
            try
            {
                _handle.Close();
                if (readIdle && writeIdle)
                {
                    _readEvent.Dispose();
                    _writeEvent.Dispose();
                }
                else
                {
                    // A driver that ignored the cancellation: wake the waiting thread, which sees the operation is still
                    // pending and leaves its memory (and these events) to the driver.
                    _readEvent.Set();
                    _writeEvent.Set();
                }
            }
            finally
            {
                if (writeIdle)
                {
                    Monitor.Exit(_writeLock);
                }
                if (readIdle)
                {
                    Monitor.Exit(_readLock);
                }
            }
        }
        base.Close();
    }

    /// <summary>Takes the lock of a read or write once its operation has ended, cancelling it meanwhile; false on timeout.</summary>
    private bool WaitUntilIdle(object operationLock)
    {
        int start = Environment.TickCount;
        while (true)
        {
            try
            {
                // Repeated: an operation that started just after the previous cancellation is cancelled as well.
                CancelIoEx(_handle, IntPtr.Zero);
            }
            catch (ObjectDisposedException)
            {
            }
            if (Monitor.TryEnter(operationLock, 50))
            {
                return true;
            }
            if (Environment.TickCount - start >= CloseWaitMs)
            {
                return false;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle safeFileHandle, CommFunction commFunction);

    public void EscapeFunction(CommFunction commFunction)
    {
        if (!EscapeCommFunction(_handle, commFunction))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot " + commFunction.ToString() + " on serial port");
        }
    }
}
