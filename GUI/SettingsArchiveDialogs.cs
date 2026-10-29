using SmarTTY.Controls;
using SmarTTY.Ssh;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Windows, message boxes and file dialogs for exporting/importing settings (see <see cref="SettingsArchive"/>).
/// What goes into the archive, or comes out of it, is chosen in <see cref="SettingsArchiveSelectionWindow"/>.
/// </summary>
internal static class SettingsArchiveDialogs
{
    private const string FileFilter = "SmarTTY settings archive (*.tar.gz)|*.tar.gz|All files (*.*)|*.*";

    private const string Caption = "SmarTTY settings";

    // Same location as GuiPreferencesStore; it stays in %LOCALAPPDATA% even in portable mode.
    private static string LocalDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmarTTY");

    private static bool IsExpected(Exception exception)
    {
        return exception is IOException || exception is UnauthorizedAccessException || exception is InvalidDataException || exception is InvalidOperationException || exception is CryptographicException;
    }

    private static SettingsLocations CreateLocations()
    {
        return new SettingsLocations
        {
            DataDirectory = GlobalSettings.DataDirectory,
            LocalDataDirectory = LocalDataDirectory,
            CommandCompletionsPath = CommandCompletionDatabase.DefaultPath,
            UserSshDirectory = SshSession.UserSshDirectory,
            KeyStore = new CspSettingsKeyStore()
        };
    }

    private static bool HasSecrets(SettingsParts parts)
    {
        return (parts & (SettingsParts.SavedPasswords | SettingsParts.Keys)) != SettingsParts.None;
    }

    private static string Join(IEnumerable<string> names)
    {
        return string.Join("\n", names);
    }

    public static void Export(Window owner)
    {
        SettingsLocations locations = CreateLocations();
        SettingsContents available;
        try
        {
            available = SettingsArchive.Describe(locations);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageDialog.Show("Could not look up the settings:\n" + ex.Message, Caption, MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: owner);
            return;
        }
        if (available.Available == SettingsParts.None)
        {
            MessageDialog.Show("There are no saved SmarTTY settings to export.", Caption, MessageDialogIcon.Information, MessageDialogButtons.Ok, owner: owner);
            return;
        }

        SettingsArchiveSelectionWindow selection = new SettingsArchiveSelectionWindow(true, available);
        WindowOwnership.Attach(selection, owner);
        if (selection.ShowDialog() != true)
        {
            return;
        }
        SettingsParts parts = selection.SelectedParts;

        string path = LocalFileDialogWindow.SaveFile(owner, "Export SmarTTY settings", FileFilter,
            fileName: "SmarTTY-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".tar.gz");
        if (path == null)
        {
            return;
        }
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            path += ".tar.gz";
        }

        try
        {
            SettingsArchive.ExportResult result = SettingsArchive.Export(path, locations, parts);
            string text = "Exported " + result.FileCount + " file(s) to:\n" + path;
            if (result.NotFound.Count > 0)
            {
                text += "\n\nNot found, so not exported:\n" + Join(result.NotFound);
            }
            if (result.Skipped.Count > 0)
            {
                text += "\n\nSkipped because they are damaged or could not be read:\n" + Join(result.Skipped);
            }
            if (HasSecrets(parts))
            {
                text += "\n\nThe archive holds saved passwords or private keys and is not encrypted: keep it secret and delete it when it is no longer needed.";
            }
            MessageDialog.Show(text, Caption, MessageDialogIcon.Information, MessageDialogButtons.Ok, owner: owner);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageDialog.Show("Could not export the settings:\n" + ex.Message, Caption, MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: owner);
        }
    }

    /// <summary>Returns the import result, or null if the user cancelled or the import failed.</summary>
    public static SettingsArchive.ImportResult Import(Window owner)
    {
        string archivePath = LocalFileDialogWindow.OpenFile(owner, "Import SmarTTY settings", FileFilter);
        if (archivePath == null)
        {
            return null;
        }

        SettingsLocations locations = CreateLocations();
        SettingsContents contents;
        try
        {
            contents = SettingsArchive.Inspect(archivePath);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageDialog.Show("Could not read the settings archive:\n" + ex.Message, Caption, MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: owner);
            return null;
        }

        SettingsArchiveSelectionWindow selection = new SettingsArchiveSelectionWindow(false, contents);
        WindowOwnership.Attach(selection, owner);
        if (selection.ShowDialog() != true)
        {
            return null;
        }
        SettingsParts parts = selection.SelectedParts;

        string dataDirectory = locations.DataDirectory;
        string backupPath = SettingsArchive.DefaultBackupPath(dataDirectory);
        string question = "Import the selected settings from:\n" + archivePath + "\n\n" +
                          "Settings files, profiles and keys with the same names will be replaced. Other saved connections are kept.\n\n" +
                          "A backup of what is replaced will be saved to:\n" + backupPath;
        if ((parts & SettingsParts.Keys) != SettingsParts.None)
        {
            question += "\n\nThe backup also holds the current keys without encryption.";
        }
        MessageBoxResult confirmation = MessageDialog.Show(question, Caption, MessageDialogIcon.Question, MessageDialogButtons.OkCancel, MessageBoxResult.Cancel, owner);
        if (confirmation != MessageBoxResult.OK)
        {
            return null;
        }

        SettingsArchive.ImportResult result;
        try
        {
            result = SettingsArchive.Import(archivePath, locations, parts, backupPath);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageDialog.Show("Could not import the settings:\n" + ex.Message, Caption, MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: owner);
            return null;
        }

        if (result.SshProfiles + result.RawProfiles > 0)
        {
            try
            {
                // Make the saved connection list reload.
                SavedConnectionStore.TouchTimestamp(Path.Combine(dataDirectory, SettingsArchive.ConnectionsDirectoryName));
            }
            catch
            {
            }
        }
        if (result.GuiPreferencesImported)
        {
            // Drop the cached GUI preferences.
            GuiPreferencesStore.Reload();
        }

        List<string> done = new List<string>();
        if (result.SshProfiles > 0)
        {
            done.Add(result.SshProfiles + " SSH profile(s)");
        }
        if (result.RawProfiles > 0)
        {
            done.Add(result.RawProfiles + " COM/TCP profile(s)");
        }
        if (result.SettingsImported)
        {
            done.Add("main settings");
        }
        if (result.KnownHostsImported)
        {
            done.Add("trusted server keys");
        }
        if (result.GuiPreferencesImported)
        {
            done.Add("interface preferences");
        }
        if (result.TftpFirmwareImported)
        {
            done.Add("TFTP firmware settings");
        }
        if (result.CommandCompletionsImported)
        {
            done.Add("command completions");
        }
        if (result.SshKeyImported)
        {
            done.Add("SmarTTY SSH key");
        }
        if (result.PasswordKeyImported)
        {
            done.Add("password protection key");
        }
        if (result.OpenSshKeyFiles > 0)
        {
            done.Add(result.OpenSshKeyFiles + " OpenSSH key file(s)");
        }

        string text = "Imported " + result.FileCount + " file(s): " + string.Join(", ", done) + ".";
        if (result.SshProfiles + result.RawProfiles > 0 && (parts & SettingsParts.SavedPasswords) == SettingsParts.None)
        {
            text += "\n\nSaved passwords were not imported; enter them again on the first connection. Passwords saved on this computer were kept.";
        }
        if (result.GuiPreferencesImported || result.CommandCompletionsImported)
        {
            text += "\n\nInterface preferences and command completions are fully applied after SmarTTY is restarted.";
        }
        if (result.BackupPath != null)
        {
            text += "\n\nPrevious settings were backed up to:\n" + result.BackupPath;
        }
        if (result.NotSelected.Count > 0)
        {
            text += "\n\nNot imported (not selected): " + result.NotSelected.Count;
        }
        if (result.Ignored.Count > 0)
        {
            text += "\n\nIgnored entries that are not SmarTTY settings files: " + result.Ignored.Count;
        }
        MessageDialog.Show(text, Caption, MessageDialogIcon.Information, MessageDialogButtons.Ok, owner: owner);
        return result;
    }
}
