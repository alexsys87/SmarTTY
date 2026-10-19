using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SmarTTY.GUI.Styles;
using Application = System.Windows.Application;

namespace SmarTTY;

public partial class App : Application
{
    public App()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        // Windows load their XAML (and its DynamicResource lookups) before MainWindow.ApplyTheme merges the
        // theme dictionary, which logged "Resource not found" warnings for the theme brushes. These application-level
        // defaults only act as a fallback: a theme dictionary merged into a window always takes precedence.
        Resources[StyleKeys.MainWindowText] = SystemColors.WindowTextBrush;
        Resources[StyleKeys.ActiveTabBackground] = Brushes.Transparent;
    }

    [STAThread]
    [DebuggerNonUserCode]
    [GeneratedCode("PresentationBuildTasks", "4.0.0.0")]
    public static void Main()
    {
        App app = new App();
        app.InitializeComponent();
        ((Application)app).Run();
    }
}
