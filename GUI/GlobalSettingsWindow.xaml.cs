using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using VisualTTY;
using VisualTTY.DeviceTerminal;
using Binding = System.Windows.Data.Binding;

namespace SmarTTY.GUI;

public partial class GlobalSettingsWindow : Window, INotifyPropertyChanged, IComponentConnector, IStyleConnector
{
    private GlobalSettings _OriginalSettings;

    private GlobalSettings _ClonedSettings;


    private bool _Initialized;

    public string SelectedFont
    {
        get
        {
            Font effectiveFont = _ClonedSettings.EffectiveFont;
            string style = (effectiveFont.Bold ? " Bold" : string.Empty) + (effectiveFont.Italic ? " Italic" : string.Empty);
            return $"{effectiveFont.Name}{style}, Size = {effectiveFont.Size}";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public GlobalSettingsWindow(GlobalSettings settings)
    {
        InitializeComponent();
        // The window keeps one height for every tab (it no longer sizes itself to the current tab), so cap it to
        // the screen: a shorter display would otherwise push the OK/Cancel buttons off the bottom.
        MaxHeight = SystemParameters.WorkArea.Height;
        _OriginalSettings = settings;
        this.DataContext = (_ClonedSettings = settings.Clone());
        if (settings.LargeBlockCutoffPoint == 0)
        {
            ((ToggleButton)cbCutoff).IsChecked = false;
        }
        else
        {
            ((ToggleButton)cbCutoff).IsChecked = true;
            if (settings.LargeBlockCutoffPoint % 1048576 == 0)
            {
                txtCutoffValue.Text = (settings.LargeBlockCutoffPoint / 1048576).ToString();
                ((Selector)lbCutoffUnit).SelectedIndex = 2;
            }
            else if (settings.LargeBlockCutoffPoint % 1024 == 0)
            {
                txtCutoffValue.Text = (settings.LargeBlockCutoffPoint / 1024).ToString();
                ((Selector)lbCutoffUnit).SelectedIndex = 1;
            }
            else
            {
                txtCutoffValue.Text = settings.LargeBlockCutoffPoint.ToString();
                ((Selector)lbCutoffUnit).SelectedIndex = 0;
            }
        }
        txtVcxsrvStatus.Text = VcxsrvSetup.DescribeInstallation(VcxsrvSetup.FindExecutable());
        // An installed VcXsrv has nothing to download.
        btnInstallVcxsrv.IsEnabled = VcxsrvSetup.FindExecutable() == null;
        _Initialized = true;
    }

    private readonly System.Threading.CancellationTokenSource _VcxsrvSetupCancellation = new System.Threading.CancellationTokenSource();

    private void InstallVcxsrv_Click(object sender, RoutedEventArgs e)
    {
        btnInstallVcxsrv.IsEnabled = false;
        System.Threading.CancellationToken cancellation = _VcxsrvSetupCancellation.Token;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string status;
            try
            {
                string executable = VcxsrvSetup.DownloadAndInstall((string step) => Dispatcher.BeginInvoke((Action)(() => txtVcxsrvStatus.Text = step)), cancellation);
                status = VcxsrvSetup.DescribeInstallation(executable);
            }
            catch (OperationCanceledException exception)
            {
                status = cancellation.IsCancellationRequested ? null : exception.Message + " " + VcxsrvSetup.DescribeInstallation(VcxsrvSetup.FindExecutable());
            }
            catch (Exception exception)
            {
                status = exception.Message + " You can download VcXsrv manually from " + VcxsrvSetup.ReleasesPage + ".";
            }
            if (status != null)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    txtVcxsrvStatus.Text = status;
                    btnInstallVcxsrv.IsEnabled = VcxsrvSetup.FindExecutable() == null;
                }));
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _VcxsrvSetupCancellation.Cancel();
        base.OnClosed(e);
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsArchiveDialogs.Export(this);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsArchive.ImportResult result = SettingsArchiveDialogs.Import(this);
        if (result != null && result.SettingsImported)
        {
            // Apply the imported values to the running settings object; otherwise the next
            // Save() of the old in-memory values would overwrite what was just imported.
            GlobalSettings.Load().CopyValuesTo(_OriginalSettings);
            this.DialogResult = true;
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
        _ClonedSettings.CopyValuesTo(_OriginalSettings);
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        string selected = LocalFolderBrowserWindow.Browse(_ClonedSettings.DefaultDownloadDirectory, "Select download directory");
        if (selected != null)
        {
            _ClonedSettings.DefaultDownloadDirectory = selected;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }

    private void EditFont_Click(object sender, RoutedEventArgs e)
    {
        Font current = _ClonedSettings.EffectiveFont;
        FontSelection selection = FontPickerWindow.Pick(this, current.Name, current.Size, current.Bold, current.Italic);
        if (selection == null)
        {
            return;
        }
        _ClonedSettings.FontName = selection.FamilyName;
        _ClonedSettings.FontSize = (float)selection.Size;
        _ClonedSettings.FontBold = selection.Bold;
        _ClonedSettings.FontItalic = selection.Italic;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SelectedFont"));
    }

    private void ResetEditorColors_Click(object sender, RoutedEventArgs e)
    {
        GlobalSettings.EditorColors colors = _ClonedSettings.EditorWrapper.Colors;
        GlobalSettings.EditorColors defaults = _ClonedSettings.Theme.IsLight() ? GlobalSettings.EditorColors.LightColors : GlobalSettings.EditorColors.DarkColors;
        foreach (PropertyInfo property in typeof(GlobalSettings.EditorColors).GetProperties())
        {
            if (property.CanWrite && property.PropertyType == typeof(DeviceTerminalSettings.ColorWrapper))
            {
                property.SetValue(colors, property.GetValue(defaults, null), null);
            }
        }
        // The color swatches are siblings of the button; refresh them from the reset values.
        if (((FrameworkElement)sender).Parent is System.Windows.Controls.Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                (child as Border)?.GetBindingExpression(Border.BackgroundProperty)?.UpdateTarget();
            }
        }
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Border border = sender as Border;
        if (border != null)
        {
            EditColor(border);
        }
    }

    public void EditColor(Border b)
    {
        BindingExpression bindingExpression = ((FrameworkElement)b).GetBindingExpression(Border.BackgroundProperty);
        object sender;
        if (bindingExpression == null)
        {
            sender = null;
        }
        else
        {
            Binding parentBinding = bindingExpression.ParentBinding;
            if (parentBinding == null)
            {
                sender = null;
            }
            else
            {
                PropertyPath path = parentBinding.Path;
                sender = ((path != null) ? path.Path : null);
            }
        }
        string text = (string)sender;
        object dataContext = ((FrameworkElement)b).DataContext;
        if (text == null || dataContext == null)
        {
            return;
        }
        string[] parts = text.Split('.');
        for (int arrayIndex = 0; arrayIndex < parts.Length - 1; arrayIndex++)
        {
            object propertyValue = dataContext.GetType().GetProperty(parts[arrayIndex])?.GetValue(dataContext, new object[0]);
            if (propertyValue == null)
            {
                propertyValue = dataContext.GetType().GetField(parts[arrayIndex])?.GetValue(dataContext);
            }
            if (propertyValue == null)
            {
                return;
            }
            dataContext = propertyValue;
        }
        PropertyInfo property = dataContext.GetType().GetProperty(parts.Last());
        if (property != null && property.GetValue(dataContext, new object[0]) is DeviceTerminalSettings.ColorWrapper colorWrapper)
        {
            DeviceTerminalSettings.ColorWrapper? selectedColor = ColorPickerWindow.Pick(this, colorWrapper);
            if (selectedColor != null)
            {
                property.SetValue(dataContext, selectedColor.Value, new object[0]);
                ((BindingExpressionBase)bindingExpression).UpdateTarget();
            }
        }
    }

    private void ApplyCutoffSetting(object sender, RoutedEventArgs e)
    {
        ApplyCutoffSetting();
    }

    private void ApplyCutoffSetting(object sender, SelectionChangedEventArgs e)
    {
        ApplyCutoffSetting();
    }

    private void ApplyCutoffSetting(object sender, TextChangedEventArgs e)
    {
        ApplyCutoffSetting();
    }

    private void ApplyCutoffSetting()
    {
        if (!_Initialized)
        {
            return;
        }
        int.TryParse(txtCutoffValue.Text, out var cutoffKilobytes);
        if (((ToggleButton)cbCutoff).IsChecked != true)
        {
            _ClonedSettings.LargeBlockCutoffPoint = 0;
            return;
        }
        switch (((Selector)lbCutoffUnit).SelectedIndex)
        {
            case 0:
                _ClonedSettings.LargeBlockCutoffPoint = cutoffKilobytes;
                break;
            case 1:
                _ClonedSettings.LargeBlockCutoffPoint = cutoffKilobytes * 1024;
                break;
            case 2:
                _ClonedSettings.LargeBlockCutoffPoint = cutoffKilobytes * 1024 * 1024;
                break;
        }
    }

    private void EditPalette_Click(object sender, RoutedEventArgs e)
    {
        ConsolePalette palette = PaletteEditWindow.Ask(this, _ClonedSettings.PaletteOverride ?? ConsolePalette.PuTTY);
        if (palette != null)
        {
            _ClonedSettings.PaletteOverride = palette;
        }
    }
}
