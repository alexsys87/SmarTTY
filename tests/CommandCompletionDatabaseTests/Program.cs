using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Serialization;
using VisualTTY;

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

    private static void ExpectEmpty(string path, string name, bool warning = true)
    {
        CommandCompletionDatabase database = CommandCompletionDatabase.Load(path);
        Check(database != null && database.Completions != null && database.Completions.Length == 0 &&
              database.LookupCommand("ls -") == null && (!warning || !string.IsNullOrEmpty(database.LoadWarning)), name);
    }

    private static void Save(string path, CommandCompletionDatabase database)
    {
        using (FileStream file = File.Create(path))
        {
            new XmlSerializer(typeof(CommandCompletionDatabase)).Serialize(file, database);
        }
    }

    private static void Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "smartty-completions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "CommandCompletions.xml");
        try
        {
            Check(!File.Exists(path), "xml does not exist initially");
            CommandCompletionDatabase autoCreated = CommandCompletionDatabase.Load(path);
            Check(File.Exists(path), "missing XML auto-creates file");
            Check(autoCreated != null && autoCreated.Completions != null && autoCreated.Completions.Length == 0 && autoCreated.LoadWarning == null, "auto-created database is valid and empty");

            Check(new CommandCompletionDatabase().LookupCommand("ls -") == null, "default database is safe");
            Check(new CommandCompletionDatabase { Completions = null }.LookupCommand("ls -") == null, "explicit null array is safe");
            Check(new CommandCompletionDatabase { Completions = new CommandCompletion[] { null } }.LookupCommand("ls -") == null, "null command is safe");
            ExpectEmpty(directory, "unreadable source falls back safely");

            foreach (string xml in new[] { "", "<CommandCompletionDatabase>", "<WrongRoot />", "<CommandCompletionDatabase xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\" />" })
            {
                File.WriteAllText(path, xml);
                ExpectEmpty(path, "invalid XML: " + xml);
            }

            File.WriteAllText(path, "<!DOCTYPE CommandCompletionDatabase [<!ENTITY test SYSTEM 'file:///not-read'>]><CommandCompletionDatabase>&test;</CommandCompletionDatabase>");
            ExpectEmpty(path, "DTD is prohibited");

            File.WriteAllText(path, "<CommandCompletionDatabase />");
            ExpectEmpty(path, "missing Completions is safe", false);
            Save(path, new CommandCompletionDatabase { Completions = null });
            ExpectEmpty(path, "serialized null Completions is safe", false);

            CommandCompletion valid = new CommandCompletion
            {
                Command = "ls",
                Description = "list files",
                Options = new[] { new CommandCompletion.Option { Value = "-a", Aliases = "--all", ShortDescription = "all files" } }
            };
            Save(path, new CommandCompletionDatabase { Completions = new[] { valid } });
            CommandCompletionDatabase loaded = CommandCompletionDatabase.Load(path);
            Check(loaded.LoadWarning == null && loaded.LookupCommand("ls -").Options[0].Aliases == "--all", "valid dictionary retains options and aliases");
            Check(loaded.LookupCommand(null) == null && loaded.LookupCommand("unknown -") == null, "no-match and null input are safe");

            Save(path, new CommandCompletionDatabase
            {
                Completions = new[]
                {
                    null,
                    new CommandCompletion(),
                    new CommandCompletion { Command = "cd", Options = null },
                    new CommandCompletion { Command = "ls", Options = new[] { null, new CommandCompletion.Option(), new CommandCompletion.Option { Value = " " }, valid.Options[0] } }
                }
            });
            loaded = CommandCompletionDatabase.Load(path);
            Check(loaded.Completions.Length == 2 && loaded.Completions.All(command => command != null), "invalid command entries are removed");
            Check(loaded.LookupCommand("cd ").Options.Length == 0, "missing Options is normalized");
            Check(loaded.LookupCommand("ls -").Options.Length == 1, "null and empty options are removed");

            File.WriteAllText(path, "<CommandCompletionDatabase><Completions><CommandCompletion><Command>ls</Command><CommandLineRegex>[</CommandLineRegex></CommandCompletion><CommandCompletion><Command>cd</Command></CommandCompletion></Completions></CommandCompletionDatabase>");
            loaded = CommandCompletionDatabase.Load(path);
            Check(loaded.LookupCommand("ls -") == null && loaded.LookupCommand("cd ") != null, "invalid regex disables only its rule");
            Check(!new CommandCompletion().IsMatch(" unrelated"), "missing command cannot match leading space");
            CommandCompletion regex = new CommandCompletion { CommandLineRegex = "^git (status|diff) " };
            Check(regex.IsMatch("git status ") && !regex.IsMatch("git log "), "valid regex still works");
            CommandCompletion slow = new CommandCompletion { CommandLineRegex = "^(a+)+$" };
            Check(!slow.IsMatch(new string('a', 10000) + "!"), "pathological regex times out safely");

            Save(path, new CommandCompletionDatabase { Completions = new[] { valid } });
            using (FileStream output = File.Create(path + ".gz"))
            using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
            using (FileStream input = File.OpenRead(path))
            {
                input.CopyTo(gzip);
            }
            File.WriteAllText(path, "<CommandCompletionDatabase />");
            ExpectEmpty(path, "plain XML takes precedence over gzip", false);
            File.Delete(path);
            Check(CommandCompletionDatabase.Load(path).LookupCommand("ls -") != null, "gzip fallback works");
            Check(CommandCompletionDatabase.Load(path + ".gz").LookupCommand("ls -") != null, "explicit gzip path works");
            File.WriteAllText(path + ".gz", "not gzip");
            ExpectEmpty(path, "damaged gzip is safe");

            File.Delete(path + ".gz");
            File.WriteAllText(path, "<CommandCompletionDatabase>" + new string(' ', 16 * 1024 * 1024) + "</CommandCompletionDatabase>");
            ExpectEmpty(path, "oversized XML is rejected");
            Console.WriteLine("All " + _passed + " regression checks passed.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
