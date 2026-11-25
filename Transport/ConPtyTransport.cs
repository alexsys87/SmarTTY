using SmarTTY.Native;
using System;
using System.IO;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Transport;

/// <summary>
/// A local console: the program of <see cref="DeviceTerminalSettings.ConnectionSettings.LocalConsole"/> run in a Windows pseudo console.
/// The bytes typed in the tab go to its console, what it shows comes back as UTF-8 with VT sequences, and the size of the tab is the size
/// of its console. The connection ends when the program ends; closing it ends the program.
/// </summary>
internal sealed class ConPtyTransport : IDeviceTransport, ITerminalSizeAwareTransport
{
    private readonly DeviceTerminalSettings.ConnectionSettings.LocalConsole _settings;

    private readonly object _lock = new object();

    private PseudoConsole _console;

    private ConsoleStream _stream;

    private int _columns = 80;

    private int _rows = 25;

    private bool _closed;

    public ConPtyTransport(DeviceTerminalSettings.ConnectionSettings.LocalConsole settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string ConnectingMessage => "Starting " + _settings.DisplayName + "...";

    public void Open(IHostNameResolver hostNameResolver)
    {
        string program = Environment.ExpandEnvironmentVariables((_settings.Program ?? "").Trim());
        string commandLine = LocalConsoleProfiles.BuildCommandLine(program, _settings.Arguments)
            ?? throw new InvalidOperationException("No program is set for the console " + _settings.DisplayName + ".");
        string folder = LocalConsoleProfiles.ResolveWorkingDirectory(_settings.WorkingDirectory, Environment.ExpandEnvironmentVariables,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException("The start folder of " + _settings.DisplayName + " does not exist: " + folder);
        }
        int columns;
        int rows;
        lock (_lock)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(ConPtyTransport), "The console is closed.");
            }
            columns = _columns;
            rows = _rows;
        }
        PseudoConsole console;
        try
        {
            console = PseudoConsole.Start(commandLine, folder, columns, rows);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new IOException("Could not start " + commandLine + ": " + exception.Message, exception);
        }
        lock (_lock)
        {
            if (_closed)
            {
                console.Dispose();
                throw new ObjectDisposedException(nameof(ConPtyTransport), "The console is closed.");
            }
            _console = console;
            _stream = new ConsoleStream(console);
            // The size may have changed while the program was being started.
            if (_columns != columns || _rows != rows)
            {
                console.Resize(_columns, _rows);
            }
        }
    }

    public void Close()
    {
        PseudoConsole console;
        lock (_lock)
        {
            _closed = true;
            console = _console;
            _console = null;
        }
        console?.Dispose();
    }

    public void Dispose()
    {
        Close();
    }

    public Stream GetStream()
    {
        lock (_lock)
        {
            return _stream ?? throw new InvalidOperationException("The console is not started.");
        }
    }

    public string GetDisplayName(bool includeParameters)
    {
        if (!includeParameters)
        {
            return _settings.DisplayName;
        }
        string line = LocalConsoleProfiles.BuildCommandLine(_settings.Program, _settings.Arguments);
        return line == null ? _settings.DisplayName : _settings.DisplayName + " (" + line + ")";
    }

    public void SetTerminalSize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }
        lock (_lock)
        {
            if (columns == _columns && rows == _rows)
            {
                return;
            }
            _columns = columns;
            _rows = rows;
            _console?.Resize(columns, rows);
        }
    }

    /// <summary>The input and the output of a console as one stream, as the connection reads and writes a transport.</summary>
    private sealed class ConsoleStream : Stream
    {
        private readonly PseudoConsole _console;

        public ConsoleStream(PseudoConsole console)
        {
            _console = console;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _console.Output.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _console.Input.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            _console.Input.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}
