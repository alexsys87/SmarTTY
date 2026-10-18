using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Windows;
using System;
using Application = System.Windows.Application;

namespace SmarTTY;

public partial class App 
{
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "4.0.0.0")]
	public void InitializeComponent()
	{
		((Application)this).StartupUri = new Uri("GUI/ConnectionSelectionWindow.xaml", UriKind.Relative);
	}

}
