using SmarTTY.Controls;
using System;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// The application's message boxes. They are drawn by <see cref="MessageDialog"/> instead of the WinForms
/// <c>MessageBox</c>, so they follow the light/dark theme like the rest of the interface.
/// </summary>
internal static class MessageBoxes
{
    private const string Caption = "SmarTTY";

    public static void Exception(Exception exception)
    {
        Error(exception.Message);
    }

    internal static bool Ask(string text)
    {
        return MessageDialog.Show(text, Caption, DialogIcon.Question, DialogButtons.YesNo) == MessageBoxResult.Yes;
    }

    internal static void Info(string text)
    {
        MessageDialog.Show(text, Caption, DialogIcon.Information, DialogButtons.Ok);
    }

    internal static bool AskWarn(string message)
    {
        return MessageDialog.Show(message, Caption, DialogIcon.Warning, DialogButtons.YesNo) == MessageBoxResult.Yes;
    }

    internal static void Warning(string text)
    {
        MessageDialog.Show(text, Caption, DialogIcon.Warning, DialogButtons.Ok);
    }

    internal static void Error(string text)
    {
        MessageDialog.Show(text, Caption, DialogIcon.Error, DialogButtons.Ok);
    }

    internal static MessageBoxResult YesNoCancel(string text)
    {
        return MessageDialog.Show(text, Caption, DialogIcon.Question, DialogButtons.YesNoCancel);
    }
}
