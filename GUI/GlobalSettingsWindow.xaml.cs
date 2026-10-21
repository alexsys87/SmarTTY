using SmarTTY.Forms;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
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
            return $"{effectiveFont.Name}, Size = {effectiveFont.Size}";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public GlobalSettingsWindow(GlobalSettings settings)
    {
        InitializeComponent();
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
        _Initialized = true;
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
        FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
        if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _ClonedSettings.DefaultDownloadDirectory = folderBrowserDialog.SelectedPath;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }

    private void EditFont_Click(object sender, RoutedEventArgs e)
    {
        FontDialog fontDialog = new FontDialog();
        GlobalSettings clonedSettings = _ClonedSettings;
        fontDialog.Font = clonedSettings.EffectiveFont;
        if (fontDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }
        try
        {
            using (Graphics graphics = Graphics.FromHwnd(new WindowInteropHelper((Window)this).Handle))
            {
                string[] strings = new string[5] { "M", "|", ".", "l", "L" };
                float value = graphics.MeasureString(strings[0], fontDialog.Font).Width;
                float currentValue = value;
                string[] items = strings;
                foreach (string text in items)
                {
                    SizeF sizeF = graphics.MeasureString(text, fontDialog.Font, 32767, StringFormat.GenericTypographic);
                    value = Math.Min(value, sizeF.Width);
                    currentValue = Math.Max(value, sizeF.Width);
                }
                if (Math.Abs(value - currentValue) > 1f)
                {
                    MessageBoxes.Error("The font you have selected is not a monospace font. The console window requires a monospace font, e.g. Courier New.");
                    return;
                }
            }
            Font font = fontDialog.Font;
            _ClonedSettings.FontSize = font.Size;
            _ClonedSettings.FontName = font.Name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SelectedFont"));
        }
        catch
        {
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
            ColorDialog colorDialog = new ColorDialog();
            colorDialog.Color = colorWrapper;
            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DeviceTerminalSettings.ColorWrapper selectedColor = colorDialog.Color;
                property.SetValue(dataContext, selectedColor, new object[0]);
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
        using PaletteEditForm paletteEditForm = new PaletteEditForm(_ClonedSettings.PaletteOverride ?? ConsolePalette.PuTTY);
        if (paletteEditForm.ShowModal() == System.Windows.Forms.DialogResult.OK)
        {
            _ClonedSettings.PaletteOverride = paletteEditForm.Palette;
        }
    }
}
