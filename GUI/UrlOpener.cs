using SmarTTY.Utilities;
using System;
using System.Diagnostics;

namespace SmarTTY.GUI;

/// <summary>
/// Opens an address that was clicked (a terminal link, Ctrl+click in the editor) with the program Windows has for its scheme: the
/// default browser for http and https. Only http, https and ftp are opened (<see cref="UrlLinks.IsOpenable"/>): the text comes from
/// the remote side, and other schemes start other programs (ms-settings:, search-ms:, file:).
/// </summary>
internal static class UrlOpener
{
    public static void Open(string url)
    {
        if (!UrlLinks.IsOpenable(url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot open " + url + ": " + exception.Message);
        }
    }
}
