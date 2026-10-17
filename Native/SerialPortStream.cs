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

    private SafeFileHandle _handle;

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

    [DllImport("kernel32.dll")]
    private static extern bool GetCommState(SafeFileHandle safeFileHandle, ref Dcb dcb);

    [DllImport("kernel32.dll")]
    private static extern bool SetCommState(SafeFileHandle safeFileHandle, ref Dcb dcb);

    public SerialPortStream(string portName, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings = null)
    {
        _handle = CreateFile("\\\\.\\" + portName, FileAccess.ReadWrite, FileShare.None, IntPtr.Zero, FileMode.Open, (FileAttributes)1073741952, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open " + portName);
        }
        if (advancedSerialPortSettings != null)
        {
            ApplyAdvancedSettings(advancedSerialPortSettings);
        }
        CommTimeouts commTimeouts = new CommTimeouts
        {
            ReadIntervalTimeout = 1u
        };
        if (!SetCommTimeouts(_handle, ref commTimeouts))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set timeouts on " + portName);
        }
    }

    public void SetTimeouts(uint readInterval, uint readTotalMultiplier, uint readTotalConstant, uint writeTotalMultiplier, uint writeTotalConstant)
    {
        CommTimeouts commTimeouts = new CommTimeouts
        {
            ReadIntervalTimeout = readInterval,
            ReadTotalTimeoutMultiplier = readTotalMultiplier,
            ReadTotalTimeoutConstant = readTotalConstant,
            WriteTotalTimeoutMultiplier = writeTotalMultiplier,
            WriteTotalTimeoutConstant = writeTotalConstant
        };
        if (!SetCommTimeouts(_handle, ref commTimeouts))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set timeouts on a serial port");
        }
    }

    private void ApplyAdvancedSettings(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings)
    {
        Dcb dcb = new Dcb
        {
            DcbLength = (uint)Marshal.SizeOf(typeof(Dcb))
        };
        if (!GetCommState(_handle, ref dcb))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query comm state for serial port");
        }
        dcb.BaudRate = (uint)advancedSerialPortSettings.BaudRate;
        dcb.ByteSize = (byte)advancedSerialPortSettings.DataBits;
        dcb.Parity = (DcbParity)advancedSerialPortSettings.Parity;
        switch (advancedSerialPortSettings.StopBits)
        {
            default:
                dcb.StopBits = DcbStopBits.One;
                break;
            case StopBits.Two:
                dcb.StopBits = DcbStopBits.Two;
                break;
            case StopBits.OnePointFive:
                dcb.StopBits = DcbStopBits.OnePointFive;
                break;
        }
        switch (advancedSerialPortSettings.FlowControl)
        {
            case Handshake.None:
                dcb.TxContinueOnXoff = false;
                dcb.OutX = false;
                dcb.RtsControlMode = RtsControl.Enable;
                dcb.DtrControlMode = DtrControl.Enable;
                dcb.OutxCtsFlow = false;
                dcb.OutxDsrFlow = false;
                break;
            case Handshake.XOnXOff:
                dcb.TxContinueOnXoff = true;
                dcb.OutX = true;
                dcb.RtsControlMode = RtsControl.Enable;
                dcb.DtrControlMode = DtrControl.Enable;
                dcb.OutxCtsFlow = false;
                dcb.OutxDsrFlow = false;
                break;
            case Handshake.RequestToSend:
                dcb.TxContinueOnXoff = false;
                dcb.OutX = false;
                dcb.RtsControlMode = RtsControl.Handshake;
                dcb.DtrControlMode = DtrControl.Enable;
                dcb.OutxCtsFlow = true;
                dcb.OutxDsrFlow = false;
                break;
            case Handshake.RequestToSendXOnXOff:
                dcb.TxContinueOnXoff = true;
                dcb.OutX = true;
                dcb.RtsControlMode = RtsControl.Handshake;
                dcb.DtrControlMode = DtrControl.Enable;
                dcb.OutxCtsFlow = true;
                dcb.OutxDsrFlow = false;
                break;
        }
        if (!SetCommState(_handle, ref dcb))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query comm state for serial port");
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_readLock)
        {
            NativeOverlapped nativeOverlapped = new NativeOverlapped
            {
                EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
            };
            IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            IntPtr bufferPtr = Marshal.AllocHGlobal(count);
            try
            {
                int bytesRead;
                do
                {
                    Marshal.StructureToPtr((object)nativeOverlapped, overlappedPtr, false);
                    if (!ReadFile(_handle, bufferPtr, count, out bytesRead, overlappedPtr) && Marshal.GetLastWin32Error() != 997)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");
                    }
                    _readEvent.WaitOne();
                    if (!GetOverlappedResult(_handle, overlappedPtr, out bytesRead, wait: false))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");
                    }
                }
                while (bytesRead == 0 && !ReturnOnEmptyRead);
                Marshal.Copy(bufferPtr, buffer, offset, bytesRead);
                return bytesRead;
            }
            finally
            {
                Marshal.FreeHGlobal(overlappedPtr);
                Marshal.FreeHGlobal(bufferPtr);
            }
        }
    }

    private int WriteChunk(byte[] buffer, int offset, int count)
    {
        lock (_writeLock)
        {
            NativeOverlapped nativeOverlapped = new NativeOverlapped
            {
                EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle()
            };
            IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            IntPtr bufferPtr = Marshal.AllocHGlobal(count);
            int bytesWritten;
            try
            {
                Marshal.StructureToPtr((object)nativeOverlapped, overlappedPtr, false);
                Marshal.Copy(buffer, offset, bufferPtr, count);
                if (!WriteFile(_handle, bufferPtr, count, out bytesWritten, overlappedPtr) && Marshal.GetLastWin32Error() != 997)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");
                }
                _writeEvent.WaitOne();
                if (!GetOverlappedResult(_handle, overlappedPtr, out bytesWritten, wait: false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(overlappedPtr);
                Marshal.FreeHGlobal(bufferPtr);
            }
            return bytesWritten;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        int chunkWritten;
        for (int written = 0; written < count; written += chunkWritten)
        {
            chunkWritten = WriteChunk(buffer, offset + written, count - written);
            if (chunkWritten <= 0)
            {
                throw new Exception("Unexpected byte count written to serial port: " + chunkWritten);
            }
        }
    }

    public override void Close()
    {
        _handle.Close();
        _readEvent.Set();
        _writeEvent.Set();
        base.Close();
    }

    [DllImport("kernel32.dll")]
    private static extern bool EscapeCommFunction(SafeFileHandle safeFileHandle, CommFunction commFunction);

    public void EscapeFunction(CommFunction commFunction)
    {
        if (!EscapeCommFunction(_handle, commFunction))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot " + commFunction.ToString() + " on serial port");
        }
    }
}
