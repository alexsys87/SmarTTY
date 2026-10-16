using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Markup;

namespace SmarTTY;

[DebuggerNonUserCode]
[GeneratedCode("PresentationBuildTasks", "4.0.0.0")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedInternalTypeHelper : InternalTypeHelper
{
    protected override object CreateInstance(Type type, CultureInfo culture)
    {
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.CreateInstance, null, null, culture);
    }

    protected override object GetPropertyValue(PropertyInfo propertyInfo, object obj, CultureInfo culture)
    {
        return propertyInfo.GetValue(obj, BindingFlags.Default, null, null, culture);
    }

    protected override void SetPropertyValue(PropertyInfo propertyInfo, object obj, object obj2, CultureInfo culture)
    {
        propertyInfo.SetValue(obj, obj2, BindingFlags.Default, null, null, culture);
    }

    protected override Delegate CreateDelegate(Type delegateType, object obj, string handler)
    {
        return (Delegate)obj.GetType().InvokeMember("_CreateDelegate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.InvokeMethod, null, obj, new object[2] { delegateType, handler }, null);
    }

    protected override void AddEventHandler(EventInfo eventInfo, object obj, Delegate handler)
    {
        eventInfo.AddEventHandler(obj, handler);
    }
}
