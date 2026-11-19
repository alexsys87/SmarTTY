using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Checks of the variables of a Smart Terminal tab (Terminal/SessionEnvironment.cs): the order, the replacement of a variable that is set again, unset,
// the names, and the commands that are put in front of every command.
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

    private static KeyValuePair<string, string> Var(string name, string value) => new KeyValuePair<string, string>(name, value);

    private static void Main()
    {
        SessionEnvironment environment = new SessionEnvironment();
        Check(!environment.Any, "a new environment has no variable");
        Equal(environment.BuildExports(), "", "and puts nothing in front of a command");
        Equal(environment.Names.Count(), 0, "and has no names");

        environment.Set(Var("A", "one"));
        Check(environment.Any, "a variable is set");
        Equal(environment.BuildExports(), "export A=one && ", "it is exported in front of a command");
        environment.Set(Var("B", "$A-two"));
        Equal(environment.BuildExports(), "export A=one && export B=$A-two && ", "the variables are in the order they were set (a later one may refer to an earlier one)");
        Equal(string.Join(",", environment.Names), "A,B", "the names are in that order");

        environment.Set(Var("A", "three"));
        Equal(environment.BuildExports(), "export B=$A-two && export A=three && ", "a variable that is set again replaces the old value and goes to the end");
        Equal(string.Join(",", environment.Names), "B,A", "also in the names");

        environment.Set(Var("B", null));
        Equal(environment.BuildExports(), "export A=three && unset B && ", "unset is a setting of its own, at the end");
        Equal(string.Join(",", environment.Names), "A", "the names are those that have a value");
        Check(environment.Any, "the environment is not empty");

        environment.Set(Var("B", "back"));
        Equal(environment.BuildExports(), "export A=three && export B=back && ", "a variable that was unset can be set again");

        environment.Set(Var("A", null));
        environment.Set(Var("B", null));
        Equal(environment.BuildExports(), "unset A && unset B && ", "all unset");
        Equal(environment.Names.Count(), 0, "no names");
        Check(environment.Any, "but the environment still holds the settings");

        environment = new SessionEnvironment();
        environment.Set(Var("EMPTY", ""));
        Equal(environment.BuildExports(), "export EMPTY= && ", "an empty value is a value");
        Equal(string.Join(",", environment.Names), "EMPTY", "with a name");
        environment.Set(Var("a", "1"));
        environment.Set(Var("A", "2"));
        Equal(environment.BuildExports(), "export EMPTY= && export a=1 && export A=2 && ", "names that differ by the case are two variables");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " session environment checks passed.");
    }
}
