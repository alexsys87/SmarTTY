using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SmarTTY.Native;

/// <summary>
/// A program run in a Windows pseudo console (ConPTY, Windows 10 1809 and later): what is written to <see cref="Input"/> is typed into
/// its console (VT sequences for the keys), and <see cref="Output"/> gives what it shows, as UTF-8 text with VT sequences.
/// When the program ends, the console is closed and the output ends.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    private const uint CreateUnicodeEnvironment = 0x00000400;

    private const int StartfUseStdHandles = 0x00000100;

    private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

    private const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string lpApplicationName, System.Text.StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly object _lock = new object();

    private IntPtr _console;

    private IntPtr _process;

    private bool _closed;

    /// <summary>What is typed into the console.</summary>
    public Stream Input { get; }

    /// <summary>What the console shows; ends when the program has ended or the console was closed.</summary>
    public Stream Output { get; }

    private PseudoConsole(IntPtr console, IntPtr process, Stream input, Stream output)
    {
        _console = console;
        _process = process;
        Input = input;
        Output = output;
    }

    /// <summary>Starts <paramref name="commandLine"/> in a new pseudo console of that size; throws with the reason when it cannot.</summary>
    public static PseudoConsole Start(string commandLine, string workingDirectory, int columns, int rows)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("A local console needs Windows 10 version 1809 or later (the Windows pseudo console).");
        }
        if (!CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
        {
            int error = Marshal.GetLastWin32Error();
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(error);
        }
        IntPtr console = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            int result = CreatePseudoConsole(SizeOf(columns, rows), inputRead, outputWrite, 0, out console);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }
            // The console holds its own ends of the pipes now; ours would keep the output open after the program ended.
            inputRead.Dispose();
            outputWrite.Dispose();
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            StartupInfoEx startupInfo = new StartupInfoEx();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // No standard handles of this process for the program: it uses those of the pseudo console.
            startupInfo.StartupInfo.dwFlags = StartfUseStdHandles;
            startupInfo.lpAttributeList = attributes;
            if (!CreateProcessW(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                IntPtr.Zero, string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory, ref startupInfo, out ProcessInformation processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            CloseHandle(processInformation.hThread);
            PseudoConsole pseudoConsole = new PseudoConsole(console, processInformation.hProcess,
                new FileStream(inputWrite, FileAccess.Write, 1, false), new FileStream(outputRead, FileAccess.Read, 1, false));
            console = IntPtr.Zero;
            pseudoConsole.WatchProcess();
            return pseudoConsole;
        }
        catch
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            inputRead.Dispose();
            outputWrite.Dispose();
            if (console != IntPtr.Zero)
            {
                ClosePseudoConsole(console);
            }
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    private static Coord SizeOf(int columns, int rows)
    {
        return new Coord { X = (short)Math.Max(1, Math.Min(columns, short.MaxValue)), Y = (short)Math.Max(1, Math.Min(rows, short.MaxValue)) };
    }

    // The pseudo console does not end its output when the program ends: it is closed when the program has ended, which ends the output.
    private void WatchProcess()
    {
        IntPtr process = _process;
        new Thread(() =>
        {
            WaitForSingleObject(process, Infinite);
            CloseConsole();
            lock (_lock)
            {
                CloseHandle(_process);
                _process = IntPtr.Zero;
            }
        })
        {
            IsBackground = true,
            Name = "Local console process watcher"
        }.Start();
    }

    /// <summary>Tells the console its new size (the program gets a resize event).</summary>
    public void Resize(int columns, int rows)
    {
        lock (_lock)
        {
            if (_console != IntPtr.Zero)
            {
                ResizePseudoConsole(_console, SizeOf(columns, rows));
            }
        }
    }

    // Closing the pseudo console ends the program (as closing its window would) and its output. Before Windows 11 the call waits
    // until the output that is left has been read, so it is never made while holding the lock that the reader may need.
    private void CloseConsole()
    {
        IntPtr console;
        lock (_lock)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        if (console != IntPtr.Zero)
        {
            ClosePseudoConsole(console);
        }
    }

    /// <summary>Closes the input at once, then on a thread of its own the console (which ends the program) and the output.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
        }
        try
        {
            Input.Dispose();
        }
        catch (IOException)
        {
        }
        // Off the caller's thread: the reader of the output may be the thread that has to drain it first. The output is
        // closed after the console, so that a reader still waiting gets the end of the output first.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CloseConsole();
            try
            {
                Output.Dispose();
            }
            catch (IOException)
            {
            }
        });
    }
}
