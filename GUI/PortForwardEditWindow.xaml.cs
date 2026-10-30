using SmarTTY.Ssh;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;

namespace SmarTTY.GUI;

/// <summary>Adds or edits one forwarding of Tools > Port forwarding.</summary>
public partial class PortForwardEditWindow : Window
{
    private bool _Loading;

    private PortForwardRule _Result;

    private PortForwardEditWindow(PortForwardRule rule, bool isNew)
    {
        _Loading = true;
        InitializeComponent();
        Title = isNew ? "Add port forwarding" : "Edit port forwarding";
        LocalRadio.IsChecked = rule.Kind == PortForwardKind.Local;
        RemoteRadio.IsChecked = rule.Kind == PortForwardKind.Remote;
        BindAddressBox.Text = rule.BindAddress;
        BindPortBox.Text = isNew && rule.BindPort == 0 ? "" : rule.BindPort.ToString(CultureInfo.InvariantCulture);
        TargetHostBox.Text = rule.TargetHost;
        TargetPortBox.Text = rule.TargetPort == 0 ? "" : rule.TargetPort.ToString(CultureInfo.InvariantCulture);
        DescriptionBox.Text = rule.Description ?? "";
        StartOnConnectBox.IsChecked = rule.StartOnConnect;
        _Loading = false;
        UpdateLabels();
    }

    /// <summary>Shows the dialog; returns the edited rule, or null when it was cancelled.</summary>
    public static PortForwardRule Edit(Window owner, PortForwardRule rule, bool isNew)
    {
        PortForwardEditWindow window = new PortForwardEditWindow(rule, isNew) { Owner = owner };
        return window.ShowDialog() == true ? window._Result : null;
    }

    private PortForwardKind Kind => RemoteRadio.IsChecked == true ? PortForwardKind.Remote : PortForwardKind.Local;

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLabels();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdateLabels()
    {
        if (_Loading)
        {
            return;
        }
        bool local = Kind == PortForwardKind.Local;
        BindLabel.Text = local ? "Listen on this computer:" : "Listen on the server:";
        TargetLabel.Text = local ? "Destination (from the server):" : "Destination (from here):";
        TargetHostBox.ToolTip = local
            ? "The host the connections go to, as the SSH server sees it: localhost is the server itself, or a name or address of its network."
            : "The host the connections go to, as this computer sees it: localhost is this computer, or a name or address of its network.";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_Loading)
        {
            return;
        }
        PortForwardRule rule = ReadRule(out _);
        PreviewBox.Text = rule == null ? "" : "ssh " + rule.SshOption + " user@server";
    }

    // The rule as entered; null with a message when a number cannot be read.
    private PortForwardRule ReadRule(out string problem)
    {
        problem = null;
        PortForwardRule rule = new PortForwardRule
        {
            Kind = Kind,
            BindAddress = BindAddressBox.Text.Trim(),
            TargetHost = TargetHostBox.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            StartOnConnect = StartOnConnectBox.IsChecked == true
        };
        if (!int.TryParse(BindPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rule.BindPort))
        {
            problem = "Enter the listening port as a number.";
            return null;
        }
        if (!int.TryParse(TargetPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rule.TargetPort))
        {
            problem = "Enter the destination port as a number.";
            return null;
        }
        return rule;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        PortForwardRule rule = ReadRule(out string problem);
        problem = problem ?? rule.Validate();
        if (problem != null)
        {
            MessageBoxes.Error(problem);
            return;
        }
        if (rule.Kind == PortForwardKind.Local && IPAddress.TryParse(rule.BindAddress, out IPAddress address) && !IPAddress.IsLoopback(address)
            && !MessageBoxes.AskWarn("With " + rule.BindAddress + " every computer that can reach this port of your computer can use it, and through it " + PortForwardRule.FormatEndpoint(rule.TargetHost, rule.TargetPort) + " behind the SSH server, without a password.\n\nUse this address anyway?"))
        {
            return;
        }
        _Result = rule;
        DialogResult = true;
    }
}
