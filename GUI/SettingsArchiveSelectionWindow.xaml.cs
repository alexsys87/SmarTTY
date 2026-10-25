using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Lets the user choose which settings and authentication keys go into a settings archive (export)
/// or are taken from it (import). Parts that do not exist (on this computer for export, in the archive for import)
/// are shown disabled. See <see cref="SettingsArchive"/>.
/// </summary>
public partial class SettingsArchiveSelectionWindow : Window
{
    private readonly bool _Exporting;

    private readonly bool _PasswordsExist;

    private readonly List<KeyValuePair<CheckBox, SettingsParts>> _Boxes = new List<KeyValuePair<CheckBox, SettingsParts>>();

    /// <summary>The parts the user ticked; valid after the dialog returned true.</summary>
    public SettingsParts SelectedParts { get; private set; }

    /// <param name="exporting">True for export, false for import.</param>
    /// <param name="available">What exists on this computer (export) or in the archive (import).</param>
    public SettingsArchiveSelectionWindow(bool exporting, SettingsContents available)
    {
        InitializeComponent();
        _Exporting = exporting;

        Title = exporting ? "Export SmarTTY settings" : "Import SmarTTY settings";
        IntroText.Text = exporting
            ? "Choose what to put into the archive. Only the things that exist on this computer can be selected."
            : "Choose what to restore from the archive. Only the things that the archive contains can be selected. The current files and keys that are replaced are backed up first.";

        Add(MainSettingsBox, SettingsParts.MainSettings, available, null);
        Add(KnownHostsBox, SettingsParts.KnownHosts, available, null);
        Add(GuiPreferencesBox, SettingsParts.GuiPreferences, available, null);
        Add(SshProfilesBox, SettingsParts.SshProfiles, available, available.SshProfiles);
        Add(RawProfilesBox, SettingsParts.RawProfiles, available, available.RawProfiles);
        Add(PasswordsBox, SettingsParts.SavedPasswords, available, null);
        Add(CompletionsBox, SettingsParts.CommandCompletions, available, null);
        Add(SshKeyBox, SettingsParts.SshKey, available, null);
        Add(PasswordKeyBox, SettingsParts.PasswordKey, available, null);
        Add(OpenSshKeysBox, SettingsParts.OpenSshKeys, available, null);

        if (available.OpenSshFiles.Count > 0)
        {
            OpenSshFilesText.Text = string.Join(", ", available.OpenSshFiles);
            OpenSshFilesText.Visibility = Visibility.Visible;
        }

        _PasswordsExist = (available.Available & SettingsParts.SavedPasswords) != SettingsParts.None;
        foreach (KeyValuePair<CheckBox, SettingsParts> pair in _Boxes)
        {
            pair.Key.Checked += (s, e) => UpdateState();
            pair.Key.Unchecked += (s, e) => UpdateState();
        }
        UpdateState();
    }

    private void Add(CheckBox box, SettingsParts part, SettingsContents available, int? count)
    {
        bool exists = (available.Available & part) != SettingsParts.None;
        box.IsEnabled = exists;
        // Keys, saved passwords and command completions are only offered, never ticked on their own.
        box.IsChecked = exists && (SettingsParts.Default & part) != SettingsParts.None;
        if (count.HasValue && exists)
        {
            box.Content = box.Content + " (" + count.Value + ")";
        }
        else if (!exists)
        {
            box.Content = box.Content + (_Exporting ? " - not found" : " - not in the archive");
        }
        _Boxes.Add(new KeyValuePair<CheckBox, SettingsParts>(box, part));
    }

    private static bool AnyChecked(params CheckBox[] boxes)
    {
        foreach (CheckBox box in boxes)
        {
            if (box.IsChecked == true)
            {
                return true;
            }
        }
        return false;
    }

    private SettingsParts Collect()
    {
        SettingsParts parts = SettingsParts.None;
        foreach (KeyValuePair<CheckBox, SettingsParts> pair in _Boxes)
        {
            if (pair.Key.IsEnabled && pair.Key.IsChecked == true)
            {
                parts |= pair.Value;
            }
        }
        return parts;
    }

    // Keeps the password option consistent with the profiles, shows the warning, and enables OK.
    private void UpdateState()
    {
        // Saved passwords live inside profiles: without a ticked profile kind there is nothing to keep them in.
        bool hasProfiles = AnyChecked(SshProfilesBox, RawProfilesBox);
        PasswordsBox.IsEnabled = _PasswordsExist && hasProfiles;
        if (!hasProfiles)
        {
            PasswordsBox.IsChecked = false;
        }

        SettingsParts parts = Collect();
        string warning = null;
        if (_Exporting)
        {
            if ((parts & (SettingsParts.SshKey | SettingsParts.PasswordKey | SettingsParts.OpenSshKeys)) != SettingsParts.None)
            {
                warning = "The archive will contain private keys without any encryption. Anyone who gets the archive can log in with these keys: keep it secret and delete it when it is no longer needed.";
            }
            else if ((parts & SettingsParts.SavedPasswords) != SettingsParts.None)
            {
                warning = "Saved passwords only work on this computer unless the password protection key is exported too. Do not pass the archive to anybody.";
            }
        }
        else
        {
            if ((parts & SettingsParts.PasswordKey) != SettingsParts.None)
            {
                warning = "Importing the password protection key makes the passwords saved on this computer unreadable, unless they come from the archive too. Tick the saved passwords as well.";
            }
            else if ((parts & SettingsParts.SshKey) != SettingsParts.None)
            {
                warning = "Importing the SmarTTY SSH key replaces the current one: servers that trust the current key will ask for a password again.";
            }
            else if ((parts & SettingsParts.OpenSshKeys) != SettingsParts.None)
            {
                warning = "Key files with the same names in your .ssh folder will be replaced.";
            }
        }
        WarningText.Text = warning;
        WarningText.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = parts != SettingsParts.None;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetAll(true);
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        SetAll(false);
    }

    private void SetAll(bool value)
    {
        // Profiles first, so that the saved passwords box is enabled when it is set.
        foreach (KeyValuePair<CheckBox, SettingsParts> pair in _Boxes)
        {
            if (pair.Value != SettingsParts.SavedPasswords && pair.Key.IsEnabled)
            {
                pair.Key.IsChecked = value;
            }
        }
        if (PasswordsBox.IsEnabled)
        {
            PasswordsBox.IsChecked = value;
        }
        UpdateState();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedParts = Collect();
        DialogResult = true;
    }
}
