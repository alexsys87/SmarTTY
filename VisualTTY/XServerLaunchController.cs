using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace VisualTTY;

public class XServerLaunchController : INotifyPropertyChanged
{
    private string _ProgressText;

    private Thread _Thread;

    private string _ErrorText;

    public TcpClient SuccessfulSocket { get; private set; }

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

    protected virtual string XServerMode => null;

    public event Action SignalCompletion;

    public event PropertyChangedEventHandler PropertyChanged;

    public XServerLaunchController(TcpClient initialClient, IAsyncResult pendingConnection)
    {
        XServerLaunchController xServerLaunchController = this;
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
                case XServerType.XMing:
                    text = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "XMing\\xming.exe");
                    break;
                case XServerType.Vcxsrv:
                    text = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "vcxsrv\\vcxsrv.exe");
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
            process = Process.Start(text, XServerMode ?? "-multiwindow");
            DateTime now = DateTime.Now;
            int num = 0;
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
                    if (process.HasExited || ((DateTime.Now - now).TotalSeconds > 5.0 && num > 0))
                    {
                        throw;
                    }
                }
                num++;
            }
        }
        catch (Exception ex2)
        {
            if (process != null && process.HasExited)
            {
                ErrorText = "X server exited with code " + process.ExitCode;
            }
            else
            {
                ErrorText = ex2.Message;
            }
            ProgressText = null;
        }
    }

    protected virtual void DoSaveSettings(XServerType xServer, string customXServerPath)
    {
    }

    public void StartXServer(XServerType type)
    {
        ProgressText = "Launching X server...";
        ErrorText = null;
        string fn = null;
        if (type == XServerType.Custom)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Executable files|*.exe|All files|*.*"
            };
            if (((CommonDialog)openFileDialog).ShowDialog() != true)
            {
                return;
            }
            fn = ((FileDialog)openFileDialog).FileName;
        }
        new Thread(() =>
        {
            LaunchAndConnectToXServer(type, fn);
        }).Start();
    }
}
