using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

// Regression harness for the content templates of the XAML files. A ContentPresenter without its own Content inside a
// ContentTemplate takes the content and the template of the element that uses the template, so it applies the very
// same template inside itself again and again: WPF measures ever deeper StackPanel/ContentPresenter pairs until the
// stack overflows. The "Abort connection" button of HostKeyMismatchWindow.xaml did that, and the program vanished
// (a StackOverflowException cannot be caught or logged) as soon as a server presented a host key of another type.
// Run with: dotnet run --project tests/XamlTemplateTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        try
        {
            // The rule itself, on markup that is known to be good or bad.
            Check(FindRecursivePresenters(Wrap(
                "<Button Content=\"Abort\"><Button.ContentTemplate><DataTemplate><StackPanel><ContentPresenter /></StackPanel></DataTemplate></Button.ContentTemplate></Button>")).Count == 1,
                "a bare ContentPresenter in a ContentTemplate is found");
            Check(FindRecursivePresenters(Wrap(
                "<Button Content=\"Abort\"><Button.ContentTemplate><DataTemplate><StackPanel><TextBlock Text=\"{Binding}\" /></StackPanel></DataTemplate></Button.ContentTemplate></Button>")).Count == 0,
                "a TextBlock bound to the content is fine");
            Check(FindRecursivePresenters(Wrap(
                "<Button><Button.ContentTemplate><DataTemplate><ContentPresenter Content=\"{Binding Icon}\" /></DataTemplate></Button.ContentTemplate></Button>")).Count == 0,
                "a ContentPresenter with content of its own is fine");
            Check(FindRecursivePresenters(Wrap(
                "<Button><Button.Template><ControlTemplate><Border><ContentPresenter /></Border></ControlTemplate></Button.Template></Button>")).Count == 0,
                "a ContentPresenter in a ControlTemplate is what it is meant for");

            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
            {
                root = Path.GetDirectoryName(root);
            }
            if (root == null)
            {
                Console.WriteLine("SKIP: repository root not found, the XAML templates are not checked");
                return 0;
            }

            var offenders = new List<string>();
            int scanned = 0;
            foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                {
                    continue;
                }
                scanned++;
                XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
                foreach (XElement presenter in FindRecursivePresenters(document))
                {
                    offenders.Add(Path.GetRelativePath(root, file) + ":" + ((System.Xml.IXmlLineInfo)presenter).LineNumber);
                }
            }

            Check(scanned > 0, "the project has XAML files to check");
            Check(offenders.Count == 0,
                "no ContentTemplate holds a ContentPresenter that would apply the same template again" +
                (offenders.Count == 0 ? "" : " (found: " + string.Join(", ", offenders) + ")"));

            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }

    private static XDocument Wrap(string markup)
    {
        return XDocument.Parse("<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" + markup + "</Window>");
    }

    // ContentPresenters inside a "*.ContentTemplate" property element (directly in its template, not in a ControlTemplate
    // nested there) that set neither Content nor ContentSource: they repeat the template they are part of.
    private static List<XElement> FindRecursivePresenters(XDocument document)
    {
        var found = new List<XElement>();
        foreach (XElement template in document.Descendants().Where(element => element.Name.LocalName.EndsWith(".ContentTemplate", StringComparison.Ordinal)))
        {
            foreach (XElement presenter in template.Descendants().Where(element => element.Name.LocalName == "ContentPresenter"))
            {
                bool inControlTemplate = presenter.Ancestors().TakeWhile(ancestor => ancestor != template).Any(ancestor => ancestor.Name.LocalName == "ControlTemplate");
                bool hasOwnContent = presenter.Attributes().Any(attribute => attribute.Name.LocalName == "Content" || attribute.Name.LocalName == "ContentSource")
                    || presenter.Elements().Any(element => element.Name.LocalName == "ContentPresenter.Content");
                if (!inControlTemplate && !hasOwnContent)
                {
                    found.Add(presenter);
                }
            }
        }
        return found;
    }
}
