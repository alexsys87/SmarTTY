using Microsoft.Win32;
using SmarTTY.Transport;
using System;
using System.IO;
using System.Windows;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Message boxes and file dialogs for exporting/importing settings (see <see cref="SettingsArchive"/>).
/// </summary>
internal static class SettingsArchiveDialogs
{
    private const string FileFilter = "SmarTTY settings archive (*.tar.gz)|*.tar.gz|All files (*.*)|*.*";

    private const string Caption = "SmarTTY settings";

    // Same location as GuiPreferencesStore; it stays in %LOCALAPPDATA% even in portable mode.
    private static string LocalDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY");

    private static bool IsExpected(Exception exception)
    {
        return exception is IOException || exception is UnauthorizedAccessException || exception is InvalidDataException || exception is InvalidOperationException;
    }

    public static void Export(Window owner)
    {
        MessageBoxResult includePasswords = MessageBox.Show(owner,
            "Include saved passwords in the archive?\n\n" +
            "Saved passwords are encrypted with a key that belongs to your Windows account, so they will not work on another computer. " +
            "Choose No for an archive you want to move to another computer or share.",
            Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (includePasswords == MessageBoxResult.Cancel)
        {
            return;
        }

        SaveFileDialog dialog = new SaveFileDialog
        {
            Title = "Export SmarTTY settings",
            Filter = FileFilter,
            FileName = "SmarTTY-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".tar.gz",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }
        string path = dialog.FileName;
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            path += ".tar.gz";
        }

        try
        {
            SettingsArchive.ExportResult result = SettingsArchive.Export(path, GlobalSettings.DataDirectory, LocalDataDirectory, includePasswords == MessageBoxResult.Yes);
            string text = "Exported " + result.FileCount + " file(s) to:\n" + path;
            if (result.Skipped.Count > 0)
            {
                text += "\n\nSkipped because they are not valid SmarTTY XML:\n" + string.Join("\n", result.Skipped);
            }
            MessageBox.Show(owner, text, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageBox.Show(owner, "Could not export the settings:\n" + ex.Message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Returns the import result, or null if the user cancelled or the import failed.</summary>
    public static SettingsArchive.ImportResult Import(Window owner)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Title = "Import SmarTTY settings",
            Filter = FileFilter,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        string dataDirectory = GlobalSettings.DataDirectory;
        string backupPath = SettingsArchive.DefaultBackupPath(dataDirectory);
        MessageBoxResult confirmation = MessageBox.Show(owner,
            "Import settings from:\n" + dialog.FileName + "\n\n" +
            "Settings.xml, KnownHosts.xml, GUIPreferences.xml and saved connections with the same names will be replaced. " +
            "Other saved connections are kept.\n\n" +
            "A backup of the current settings will be saved to:\n" + backupPath,
            Caption, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK)
        {
            return null;
        }

        SettingsArchive.ImportResult result;
        try
        {
            result = SettingsArchive.Import(dialog.FileName, dataDirectory, LocalDataDirectory, backupPath);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageBox.Show(owner, "Could not import the settings:\n" + ex.Message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        try
        {
            // Make the saved connection list reload, and drop the cached GUI preferences.
            SavedConnectionStore.TouchTimestamp(Path.Combine(dataDirectory, SettingsArchive.ConnectionsDirectoryName));
        }
        catch
        {
        }
        GuiPreferencesStore.Reload();

        string text = "Imported " + result.FileCount + " file(s): " + result.SshProfiles + " SSH profile(s), " + result.RawProfiles + " COM/TCP profile(s)" +
                      (result.SettingsImported ? ", main settings" : "") + (result.KnownHostsImported ? ", trusted server keys" : "") +
                      (result.GuiPreferencesImported ? ", interface preferences" : "") + ".";
        text += "\n\nSaved passwords are normally not part of an archive; enter them again on the first connection.";
        if (result.GuiPreferencesImported)
        {
            text += " Interface preferences are fully applied after SmarTTY is restarted.";
        }
        if (result.BackupPath != null)
        {
            text += "\n\nPrevious settings were backed up to:\n" + result.BackupPath;
        }
        if (result.Ignored.Count > 0)
        {
            text += "\n\nIgnored entries that are not SmarTTY settings files: " + result.Ignored.Count;
        }
        MessageBox.Show(owner, text, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        return result;
    }
}
