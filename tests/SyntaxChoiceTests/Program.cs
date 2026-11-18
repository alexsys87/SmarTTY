using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;

// Checks of CodeEditing/SyntaxChoice.cs, the syntax of a file in the remote file editor (the choice of the user, the detection from the name and the first
// line, the one that is applied now). The same code is run in the editor by tests/RemoteFileEditorIntegrationTests (the status-source scenario).
// Run with: dotnet run --project tests/SyntaxChoiceTests
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static int Main()
    {
        try
        {
            SyntaxChoice choice = new SyntaxChoice();
            Check(!choice.Override.HasValue, "no syntax is chosen at first");
            Equal(choice.Current, SyntaxHighlighting.Language.Plain, "and none is applied");
            Equal(choice.Detect("/etc/app/run.py", null), SyntaxHighlighting.DetectLanguage("/etc/app/run.py", null), "the syntax is detected from the file name");
            Equal(choice.Detect("/usr/bin/tool", "#!/bin/sh"), SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/bin/sh"), "and from the #! line");

            Check(choice.Choose(SyntaxHighlighting.Language.Python), "choosing a syntax is a change");
            Equal(choice.Override, (SyntaxHighlighting.Language?)SyntaxHighlighting.Language.Python, "which is kept");
            Equal(choice.Detect("/etc/passwd", null), SyntaxHighlighting.Language.Python, "and wins over the detection");
            Check(!choice.Choose(SyntaxHighlighting.Language.Python), "choosing the same syntax again is no change");
            Check(choice.Choose(SyntaxHighlighting.Language.Lua), "another syntax is a change");
            Equal(choice.Detect("/etc/app/run.py", "#!/bin/sh"), SyntaxHighlighting.Language.Lua, "and replaces the first");
            Check(choice.Choose(null), "going back to the detection is a change");
            Check(!choice.Override.HasValue, "the choice is gone");
            Equal(choice.Detect("/usr/bin/tool", "#!/bin/sh"), SyntaxHighlighting.DetectLanguage("/usr/bin/tool", "#!/bin/sh"), "and the file is detected again");
            Check(!choice.Choose(null), "no choice and none chosen is no change");

            choice.Report(SyntaxHighlighting.Language.Bash);
            Equal(choice.Current, SyntaxHighlighting.Language.Bash, "the syntax that was applied is kept");
            Check(!choice.Override.HasValue, "without becoming a choice");
            choice.Choose(SyntaxHighlighting.Language.Json);
            Equal(choice.Current, SyntaxHighlighting.Language.Bash, "and a choice does not change what is applied until the tokenizer reports it");
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " syntax choice checks passed.");
        return 0;
    }
}
