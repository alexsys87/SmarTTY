using SmarTTY.Controls;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace VisualTTY;

public class XServerLaunchController : INotifyPropertyChanged
{
    private string _ProgressText;

    private Thread _Thread;

    private string _ErrorText;

    private string _VcxsrvPath;

    private readonly CancellationTokenSource _Cancellation = new CancellationTokenSource();

    public TcpClient SuccessfulSocket { get; private set; }

    /// <summary>Path of the installed vcxsrv.exe; null if VcXsrv is not installed.</summary>
    public string VcxsrvPath
    {
        get
        {
            return _VcxsrvPath;
        }
        private set
        {
            _VcxsrvPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("VcxsrvPath"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsVcxsrvInstalled"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("VcxsrvStatusText"));
        }
    }

    public bool IsVcxsrvInstalled => _VcxsrvPath != null;

    public string VcxsrvStatusText => VcxsrvSetup.DescribeInstallation(_VcxsrvPath);

    public string ProgressText
    {
        get
        {
            return _ProgressText;
        }
        set
        {
            _ProgressText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressText"));
        }
    }

    public bool SaveSettings { get; set; }

    public string ErrorText
    {
        get
        {
            return _ErrorText;
        }
        set
        {
            _ErrorText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ErrorText"));
        }
    }

    protected virtual XServerType InitialXServer => XServerType.Ask;

    protected virtual string InitialCustomXServer => null;

    protected virtual string XServerArguments => null;

    public event Action SignalCompletion;

    public event PropertyChangedEventHandler PropertyChanged;

    public XServerLaunchController(TcpClient initialClient, IAsyncResult pendingConnection)
    {
        XServerLaunchController xServerLaunchController = this;
        _VcxsrvPath = VcxsrvSetup.FindExecutable();
        ProgressText = "Connecting to port 6000...";
        _Thread = new Thread(() =>
        {
            xServerLaunchController.HandleInitialConnection(initialClient, pendingConnection);
        });
    }

    public void Start()
    {
        _Thread.Start();
    }

    private void HandleInitialConnection(TcpClient initialClient, IAsyncResult pendingConnection)
    {
        try
        {
            pendingConnection.AsyncWaitHandle.WaitOne();
            initialClient.EndConnect(pendingConnection);
            SuccessfulSocket = initialClient;
            SignalCompletion?.Invoke();
        }
        catch
        {
        }
        if (InitialXServer == XServerType.Ask)
        {
            string progressText = (ErrorText = null);
            ProgressText = progressText;
        }
        else
        {
            LaunchAndConnectToXServer(InitialXServer, InitialCustomXServer);
        }
    }

    private void LaunchAndConnectToXServer(XServerType xServer, string customXServerPath)
    {
        Process process = null;
        try
        {
            string text;
            switch (xServer)
            {
                case XServerType.Vcxsrv:
                    VcxsrvPath = VcxsrvSetup.FindExecutable();
                    text = VcxsrvPath ?? throw new FileNotFoundException(VcxsrvSetup.DescribeInstallation(null) + " Use the button below to download and install the latest version.");
                    break;
                case XServerType.Custom:
                    text = customXServerPath;
                    if (string.IsNullOrEmpty(text))
                    {
                        throw new Exception("Selected an invalid custom X srver");
                    }
                    break;
                default:
                    throw new Exception("Invalid X Server type");
            }
            ProgressText = "Launching X server...";
            process = Process.Start(text, XServerArguments ?? "-multiwindow");
            DateTime now = DateTime.Now;
            int itemIndex = 0;
            while (true)
            {
                try
                {
                    TcpClient tcpClient = new TcpClient();
                    tcpClient.Connect("localhost", 6000);
                    SuccessfulSocket = tcpClient;
                    if (SaveSettings)
                    {
                        DoSaveSettings(xServer, customXServerPath);
                    }
                    SignalCompletion?.Invoke();
                    break;
                }
                catch (Exception)
                {
                    if (process.HasExited || _Cancellation.IsCancellationRequested || ((DateTime.Now - now).TotalSeconds > 5.0 && itemIndex > 0))
                    {
                        throw;
                    }
                }
                itemIndex++;
                // The X server needs a moment before it listens; do not spin while waiting for it.
                Thread.Sleep(100);
            }
        }
        catch (Exception exception)
        {
            if (process != null && process.HasExited)
            {
                ErrorText = "X server exited with code " + process.ExitCode;
            }
            else
            {
                ErrorText = exception.Message;
            }
            ProgressText = null;
        }
    }

    protected virtual void DoSaveSettings(XServerType xServer, string customXServerPath)
    {
    }

    /// <summary>Stops a download or installation in progress (the window was closed).</summary>
    public void Cancel()
    {
        _Cancellation.Cancel();
    }

    /// <summary>
    /// Downloads the installer of the latest VcXsrv release, runs it and then starts the installed X server.
    /// </summary>
    public void DownloadAndInstallVcxsrv()
    {
        ProgressText = "Looking for the latest VcXsrv release...";
        ErrorText = null;
        new Thread(() =>
        {
            try
            {
                VcxsrvPath = VcxsrvSetup.DownloadAndInstall((string step) => ProgressText = step, _Cancellation.Token);
                LaunchAndConnectToXServer(XServerType.Vcxsrv, null);
            }
            catch (OperationCanceledException exception)
            {
                ErrorText = _Cancellation.IsCancellationRequested ? null : exception.Message;
                ProgressText = null;
            }
            catch (Exception exception)
            {
                ErrorText = exception.Message + " You can download VcXsrv manually from " + VcxsrvSetup.ReleasesPage + ".";
                ProgressText = null;
            }
        })
        {
            IsBackground = true,
            Name = "VcXsrv setup"
        }.Start();
    }

    public void StartXServer(XServerType type)
    {
        ProgressText = "Launching X server...";
        ErrorText = null;
        string fn = null;
        if (type == XServerType.Custom)
        {
            fn = ThemedFileDialog.OpenFile(null, "Select the X server executable", "Executable files|*.exe|All files (*.*)|*.*");
            if (fn == null)
            {
                return;
            }
        }
        new Thread(() =>
        {
            LaunchAndConnectToXServer(type, fn);
        }).Start();
    }
}
