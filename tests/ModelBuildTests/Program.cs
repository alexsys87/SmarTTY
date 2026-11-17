// Compiles the logic folders (Terminal/, TextEditing/, CodeEditing/, Completion/) for plain net8.0, without any Windows
// types, to show a hidden dependency of the logic on the UI. The files of tests/model-boundary-allowlist.txt are left out.
internal static class Program
{
    private static int Main()
    {
        System.Console.WriteLine("PASS: the logic folders compile without the UI types.");
        return 0;
    }
}

// The namespaces of the code the logic folders use: an empty one would make "using" fail before the real missing file is named.
namespace SmarTTY.IO
{
    internal static class NamespaceSeed
    {
    }
}

namespace SmarTTY.Ssh
{
    internal static class NamespaceSeed
    {
    }
}
