using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Checks of the variables of a Smart Terminal tab (Terminal/SessionEnvironment.cs): the order, a variable that is set again (the earlier setting stays while
// something set after it refers to it), unset, the names, the commands that are put in front of every command, and reading while another thread sets.
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
        Equal(environment.BuildExports(), "export A=one && export B=$A-two && export A=three && ", "a variable that is set again keeps the earlier setting while another one refers to it: B still gets the value A had when B was set");
        Equal(string.Join(",", environment.Names), "B,A", "the names are in the order of the last setting, each once");

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

        // References: what keeps an earlier setting.
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "${A}x"));
        environment.Set(Var("A", "2"));
        Equal(environment.BuildExports(), "export A=1 && export B=${A}x && export A=2 && ", "${A} refers to A");
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "${A:-d}"));
        environment.Set(Var("A", "2"));
        Equal(environment.BuildExports(), "export A=1 && export B=${A:-d} && export A=2 && ", "${A:-d} refers to A");
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "x$A"));
        environment.Set(Var("A", "2"));
        Equal(environment.BuildExports(), "export A=1 && export B=x$A && export A=2 && ", "$A at the end of a value refers to A");
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "$AB $A_B ${AB} $a"));
        environment.Set(Var("A", "2"));
        Equal(environment.BuildExports(), "export B=$AB $A_B ${AB} $a && export A=2 && ", "$AB, $A_B, ${AB} and $a are other variables: the earlier setting of A goes");
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "cost 5$"));
        environment.Set(Var("A", "2"));
        Equal(environment.BuildExports(), "export B=cost 5$ && export A=2 && ", "a dollar sign that is not followed by the name is not a reference");

        // The PATH pattern: a value that refers to the variable it sets needs the setting before it.
        environment = new SessionEnvironment();
        environment.Set(Var("PATH", "/a"));
        environment.Set(Var("PATH", "$PATH:/b"));
        Equal(environment.BuildExports(), "export PATH=/a && export PATH=$PATH:/b && ", "export PATH=$PATH:/b needs the earlier PATH");
        environment.Set(Var("PATH", "/z"));
        Equal(environment.BuildExports(), "export PATH=/z && ", "a setting that does not refer to it ends the chain: all the earlier ones go");
        Equal(string.Join(",", environment.Names), "PATH", "with its name once");

        // A chain: B refers to A, C refers to B.
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "$A"));
        environment.Set(Var("C", "$B"));
        environment.Set(Var("A", "2"));
        environment.Set(Var("B", "$A"));
        Equal(environment.BuildExports(), "export A=1 && export B=$A && export C=$B && export A=2 && export B=$A && ", "a chain keeps what the later ones need: C gets the first B, the new B gets the new A");
        environment.Set(Var("C", "none"));
        Equal(environment.BuildExports(), "export A=2 && export B=$A && export C=none && ", "when nothing refers to the old settings any more they go");

        // Unset of a variable something refers to.
        environment = new SessionEnvironment();
        environment.Set(Var("A", "1"));
        environment.Set(Var("B", "$A"));
        environment.Set(Var("A", null));
        Equal(environment.BuildExports(), "export A=1 && export B=$A && unset A && ", "unset A keeps the setting that B refers to");
        Equal(string.Join(",", environment.Names), "B", "A has no value any more");

        // Threads that read while another sets: the completion builds commands while the tab changes the variables.
        environment = new SessionEnvironment();
        bool done = false;
        Exception failure = null;
        List<System.Threading.Thread> readers = new List<System.Threading.Thread>();
        for (int reader = 0; reader < 3; reader++)
        {
            System.Threading.Thread thread = new System.Threading.Thread(() =>
            {
                try
                {
                    while (!System.Threading.Volatile.Read(ref done))
                    {
                        string exports = environment.BuildExports();
                        int names = environment.Names.Count();
                        bool any = environment.Any;
                        if (exports.Length > 0 && !exports.EndsWith(" && ", StringComparison.Ordinal))
                        {
                            throw new Exception("a command was cut: " + exports);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.Start();
            readers.Add(thread);
        }
        for (int i = 0; i < 20000; i++)
        {
            environment.Set(Var("V" + (i % 7), i % 5 == 0 ? null : "x" + i + "$V" + ((i + 1) % 7)));
        }
        System.Threading.Volatile.Write(ref done, true);
        foreach (System.Threading.Thread thread in readers)
        {
            thread.Join();
        }
        Check(failure == null, "reading while another thread sets never throws and never sees a cut command" + (failure == null ? "" : ": " + failure.Message));

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " session environment checks passed.");
    }
}
