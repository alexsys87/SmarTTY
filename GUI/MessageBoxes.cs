using System;
using System.Windows.Forms;

namespace SmarTTY.GUI;

internal static class MessageBoxes
{
    public static void Exception(Exception ex)
    {
        MessageBox.Show(ex.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
    }

    internal static bool Ask(string text)
    {
        return MessageBox.Show(text, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    internal static void Info(string text)
    {
        MessageBox.Show(text, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
    }

    internal static bool AskWarn(string message)
    {
        return MessageBox.Show(message, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes;
    }

    internal static void Error(string text)
    {
        MessageBox.Show(text, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
    }

    internal static DialogResult YesNoCancel(string text)
    {
        return MessageBox.Show(text, "SmarTTY", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
    }
}
