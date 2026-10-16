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

    internal ResourceDictionary _ThemeDictionary;

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
                string[] array = new string[5] { "M", "|", ".", "l", "L" };
                float num = graphics.MeasureString(array[0], fontDialog.Font).Width;
                float num2 = num;
                string[] array2 = array;
                foreach (string text in array2)
                {
                    SizeF sizeF = graphics.MeasureString(text, fontDialog.Font, 32767, StringFormat.GenericTypographic);
                    num = Math.Min(num, sizeF.Width);
                    num2 = Math.Max(num, sizeF.Width);
                }
                if (Math.Abs(num - num2) > 1f)
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
        object obj;
        if (bindingExpression == null)
        {
            obj = null;
        }
        else
        {
            Binding parentBinding = bindingExpression.ParentBinding;
            if (parentBinding == null)
            {
                obj = null;
            }
            else
            {
                PropertyPath path = parentBinding.Path;
                obj = ((path != null) ? path.Path : null);
            }
        }
        string text = (string)obj;
        object obj2 = ((FrameworkElement)b).DataContext;
        if (text == null || obj2 == null)
        {
            return;
        }
        string[] array = text.Split('.');
        for (int num = 0; num < array.Length - 1; num++)
        {
            object obj3 = obj2.GetType().GetProperty(array[num])?.GetValue(obj2, new object[0]);
            if (obj3 == null)
            {
                obj3 = obj2.GetType().GetField(array[num])?.GetValue(obj2);
            }
            if (obj3 == null)
            {
                return;
            }
            obj2 = obj3;
        }
        PropertyInfo property = obj2.GetType().GetProperty(array.Last());
        if (property != null && property.GetValue(obj2, new object[0]) is DeviceTerminalSettings.ColorWrapper colorWrapper)
        {
            ColorDialog colorDialog = new ColorDialog();
            colorDialog.Color = colorWrapper;
            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DeviceTerminalSettings.ColorWrapper colorWrapper2 = colorDialog.Color;
                property.SetValue(obj2, colorWrapper2, new object[0]);
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
        int.TryParse(txtCutoffValue.Text, out var result);
        if (((ToggleButton)cbCutoff).IsChecked != true)
        {
            _ClonedSettings.LargeBlockCutoffPoint = 0;
            return;
        }
        switch (((Selector)lbCutoffUnit).SelectedIndex)
        {
            case 0:
                _ClonedSettings.LargeBlockCutoffPoint = result;
                break;
            case 1:
                _ClonedSettings.LargeBlockCutoffPoint = result * 1024;
                break;
            case 2:
                _ClonedSettings.LargeBlockCutoffPoint = result * 1024 * 1024;
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
