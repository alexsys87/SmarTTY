using System;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI;

public partial class XServerLaunchWindow : Window, IComponentConnector
{
    public class ControllerImpl : XServerLaunchController
    {
        private GlobalSettings _Settings;

        protected override XServerType InitialXServer => _Settings.XServer;

        protected override string InitialCustomXServer => _Settings.CustomXServerPath;

        protected override string XServerMode => _Settings.XMingMode;

        public ControllerImpl(TcpClient initialClient, IAsyncResult pendingConnection, GlobalSettings settings)
            : base(initialClient, pendingConnection)
        {
            _Settings = settings;
        }

        protected override void DoSaveSettings(XServerType xServer, string customXServerPath)
        {
            _Settings.XServer = xServer;
            _Settings.CustomXServerPath = customXServerPath;
            _Settings.Save();
        }
    }

    private ResourceDictionary _ThemeDictionary;

    private bool _Loaded;

    public ControllerImpl Controller { get; }

    public XServerLaunchWindow(ControllerImpl controller)
    {
        InitializeComponent();
        this.DataContext = (Controller = controller);
        controller.SignalCompletion += Controller_SignalCompletion;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_Loaded)
        {
            _Loaded = true;
            Controller.Start();
        }
    }

    private void Controller_SignalCompletion()
    {
        this.Dispatcher.BeginInvoke((Delegate)(Action)(() =>
        {
            try
            {
                this.Close();
            }
            catch
            {
            }
        }), new object[0]);
    }

    public static TcpClient CreateX11ConnectionFromArbitraryThread(Dispatcher dispatcher, GlobalSettings settings, Window parent)
    {
        TcpClient client = new TcpClient();
        IAsyncResult result = client.BeginConnect("localhost", 6000, null, null);
        if (result.AsyncWaitHandle.WaitOne(300, exitContext: false))
        {
            try
            {
                client.EndConnect(result);
                return client;
            }
            catch
            {
                result = null;
            }
        }
        return (TcpClient)dispatcher.Invoke((Delegate)(Func<TcpClient>)(() =>
        {
            ControllerImpl controllerImpl = new ControllerImpl(client, result, settings);
            XServerLaunchWindow xServerLaunchWindow = new XServerLaunchWindow(controllerImpl);
            xServerLaunchWindow.Owner = parent;
            XServerLaunchWindow xServerLaunchWindow2 = xServerLaunchWindow;
            MainWindow.ApplyTheme((Window)xServerLaunchWindow2, settings.Theme, ref xServerLaunchWindow2._ThemeDictionary);
            xServerLaunchWindow2.ShowDialog();
            return controllerImpl.SuccessfulSocket;
        }), new object[0]);
    }

    private void VCXSRV_Click(object sender, RoutedEventArgs e)
    {
        Controller.StartXServer(XServerType.Vcxsrv);
    }

    private void XMING_Click(object sender, RoutedEventArgs e)
    {
        Controller.StartXServer(XServerType.XMing);
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        Controller.StartXServer(XServerType.Custom);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
