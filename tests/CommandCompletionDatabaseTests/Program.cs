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

    private static void RunEditorChecks(string root)
    {
        string directory = Path.Combine(root, "editor");
        Directory.CreateDirectory(directory);
        string xml = Path.Combine(directory, "CommandCompletions.xml");
        string gz = xml + ".gz";

        CommandCompletion ls = new CommandCompletion
        {
            Command = "ls",
            Description = "list files",
            SuppressAutoTriggers = true,
            Options = new[]
            {
                new CommandCompletion.Option
                {
                    Value = "-a",
                    Aliases = "--all",
                    ShortDescription = "all files",
                    LongDescription = new CommandCompletion.OptionDescription { Summary = "do not hide", Info = "line 1\nline 2" }
                }
            }
        };
        CommandCompletionDatabase original = new CommandCompletionDatabase { Completions = new[] { ls, new CommandCompletion { CommandLineRegex = "^git (status|diff) ", Options = new[] { new CommandCompletion.Option { Value = "-s" } } } } };

        // saving as gzip replaces the plain file, and back
        original.SaveTo(xml, false);
        Check(File.Exists(xml) && !File.Exists(gz) && !CommandCompletionDatabase.IsStoredCompressed(xml), "SaveTo writes plain XML");
        original.SaveTo(xml, true);
        Check(!File.Exists(xml) && File.Exists(gz) && CommandCompletionDatabase.IsStoredCompressed(xml) && !File.Exists(gz + ".tmp"), "SaveTo compressed writes only the gzip file");
        CommandCompletionDatabase fromGz = CommandCompletionDatabase.Load(xml);
        Check(fromGz.LoadWarning == null && fromGz.Completions.Length == 2 && fromGz.LookupCommand("ls -").Options[0].LongDescription.Info == "line 1\nline 2" && fromGz.LookupCommand("git diff ") != null,
            "the gzip file loads back with everything in it");
        File.WriteAllText(xml, "<CommandCompletionDatabase />");
        original.SaveTo(xml, true);
        Check(!File.Exists(xml) && CommandCompletionDatabase.Load(xml).Completions.Length == 2, "a stale plain file cannot hide the saved gzip file");
        original.SaveTo(xml, false);
        Check(File.Exists(xml) && !File.Exists(gz) && CommandCompletionDatabase.Load(xml).Completions.Length == 2, "saving plain removes the stale gzip file");
        int counter = CommandCompletionDatabase.ChangeCounter;
        CommandCompletionDatabase.NotifyChanged();
        Check(CommandCompletionDatabase.ChangeCounter == counter + 1, "NotifyChanged bumps the counter");

        // edit model
        CompletionEditModel model = CompletionEditModel.FromDatabase(original);
        Check(model.Entries.Count == 2 && model.Entries[0].Command == "ls" && model.Entries[0].SuppressAutoTriggers && model.Entries[0].Options[0].Summary == "do not hide" && model.Entries[1].CommandLineRegex == "^git (status|diff) ",
            "the model copies the database");
        CommandCompletionDatabase roundTrip = model.ToDatabase();
        roundTrip.SaveTo(xml, true);
        CommandCompletionDatabase again = CommandCompletionDatabase.Load(xml);
        Check(again.Completions.Length == 2 && again.LookupCommand("ls -").Options[0].Aliases == "--all" && again.LookupCommand("ls -").Options[0].LongDescription.Summary == "do not hide" && again.LookupCommand("ls -").SuppressAutoTriggers,
            "database -> model -> gzip -> database keeps all fields");
        Check(model.Validate(new System.Collections.Generic.List<string>()).Count == 0, "a valid model has no errors");
        Check(model.Entries[0].Matches("ls -") && !model.Entries[0].Matches("ls") && model.Entries[1].Matches("git diff -") && !model.Entries[1].Matches("git log "), "Matches decides like the terminal");

        CompletionEditModel messy = new CompletionEditModel();
        messy.Entries.Add(new CommandEditItem());
        messy.Entries.Add(new CommandEditItem { Command = "  cd  ", Description = "   " });
        messy.Entries[1].Options.Add(new OptionEditItem { Value = " -P ", Aliases = "", Summary = "x" });
        messy.Entries[1].Options.Add(new OptionEditItem { Value = "-P" });
        messy.Entries.Add(new CommandEditItem { Command = "cd" });
        messy.Entries.Add(new CommandEditItem { Command = "grep", CommandLineRegex = "[" });
        messy.Entries[3].Options.Add(new OptionEditItem());
        System.Collections.Generic.List<string> warnings = new System.Collections.Generic.List<string>();
        System.Collections.Generic.List<string> errors = messy.Validate(warnings);
        Check(errors.Count == 3 && errors.Any(e => e.Contains("#1")) && errors.Any(e => e.Contains("invalid regular expression")) && errors.Any(e => e.Contains("no value")), "empty command, invalid regex and empty option are errors");
        Check(warnings.Count == 2 && warnings.Any(w => w.Contains("more than once") && w.Contains("'cd'")) && warnings.Any(w => w.Contains("option '-P'")), "duplicates are warnings");
        CommandCompletion cleaned = messy.ToDatabase().Completions[1];
        Check(cleaned.Command == "cd" && cleaned.Description == null && cleaned.Options[0].Value == "-P" && cleaned.Options[0].Aliases == null && cleaned.Options[0].LongDescription.Summary == "x", "values are trimmed and empty ones are left out");
        Check(CommandEditItem.GetRegexError("[") != null && CommandEditItem.GetRegexError("^a+") == null && CommandEditItem.GetRegexError("  ") == null, "regex errors are reported");

        CommandEditItem copy = model.Entries[0].Clone();
        copy.Options[0].Value = "-A";
        Check(model.Entries[0].Options[0].Value == "-a" && copy.Command == "ls", "a clone is independent");
        int notifications = 0;
        model.Entries[0].PropertyChanged += (s, e) => notifications++;
        model.Entries[0].Command = "ls2";
        model.Entries[0].Command = "ls2";
        Check(notifications == 2 && model.Entries[0].DisplayName == "ls2", "setting a property notifies once and only when it changes (Command + DisplayName)");
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
            Check(!File.Exists(path) && !File.Exists(path + ".gz"), "missing XML is not created: the built-in template is used");
            CommandCompletionDatabase template = CommandCompletionDatabase.LoadTemplate();
            Check(template != null && template.Completions != null && template.LoadWarning == null && template.Completions.Length > 0,
                "the built-in template is valid and not empty");
            Check(autoCreated != null && autoCreated.Completions != null && autoCreated.LoadWarning == null &&
                  autoCreated.Completions.Length == template.Completions.Length,
                "a missing XML loads the built-in template");

            // A file next to the exe wins over the template; the first save of the editor writes template + additions there.
            CommandCompletionDatabase external = new CommandCompletionDatabase { Completions = new[] { new CommandCompletion { Command = "mytool" } } };
            external.SaveTo(path, compressed: false);
            Check(CommandCompletionDatabase.Load(path).Completions.Length == 1, "the file next to the exe takes priority over the template");
            File.Delete(path);
            Check(CommandCompletionDatabase.Load(path).Completions.Length == template.Completions.Length && !File.Exists(path),
                "without the file the template is used again, nothing is written");

            // Places: the folder of the program first, then %APPDATA%\\SmarTTY, then the template; the first save goes next to the exe
            // and falls back to %APPDATA%\\SmarTTY when the folder of the program refuses.
            string programDir = Path.Combine(directory, "program");
            string userDir = Path.Combine(directory, "user");
            Directory.CreateDirectory(programDir);
            CommandCompletionDatabase.ProgramDirectory = programDir;
            CommandCompletionDatabase.UserDirectory = userDir;
            CommandCompletionDatabase one = new CommandCompletionDatabase { Completions = new[] { new CommandCompletion { Command = "one" } } };
            CommandCompletionDatabase two = new CommandCompletionDatabase { Completions = new[] { new CommandCompletion { Command = "two" }, new CommandCompletion { Command = "three" } } };
            Check(CommandCompletionDatabase.FindExternalPath() == null && CommandCompletionDatabase.Load().Completions.Length == template.Completions.Length,
                "no file anywhere: the template");
            Check(CommandCompletionDatabase.PlannedPath() == CommandCompletionDatabase.DefaultPath, "a writable program folder is where the first save goes");
            Check(one.SaveToPreferredPlace(false) == CommandCompletionDatabase.DefaultPath && File.Exists(CommandCompletionDatabase.DefaultPath) && !Directory.Exists(userDir), "the first save goes next to the exe");
            two.SaveTo(CommandCompletionDatabase.UserPath, false);
            Check(CommandCompletionDatabase.FindExternalPath() == CommandCompletionDatabase.DefaultPath && CommandCompletionDatabase.Load().Completions.Length == 1, "the file next to the exe has priority over %APPDATA%");
            File.Delete(CommandCompletionDatabase.DefaultPath);
            Check(CommandCompletionDatabase.FindExternalPath() == CommandCompletionDatabase.UserPath && CommandCompletionDatabase.Load().Completions.Length == 2, "without the file next to the exe the one in %APPDATA% is read");
            Check(one.SaveToPreferredPlace(false) == CommandCompletionDatabase.UserPath && !File.Exists(CommandCompletionDatabase.DefaultPath) && CommandCompletionDatabase.Load().Completions.Length == 1, "a file in %APPDATA% is saved in place");
            File.Delete(CommandCompletionDatabase.UserPath);
            Directory.Delete(userDir);
            string blockedProgram = Path.Combine(directory, "blocked");
            File.WriteAllText(blockedProgram, "not a folder");
            CommandCompletionDatabase.ProgramDirectory = blockedProgram;
            Check(CommandCompletionDatabase.PlannedPath() == CommandCompletionDatabase.UserPath, "a program folder that cannot be written plans the %APPDATA% place");
            Check(two.SaveToPreferredPlace(false) == CommandCompletionDatabase.UserPath && File.Exists(CommandCompletionDatabase.UserPath), "an unwritable program folder: the first save falls back to %APPDATA% (folder created)");
            Check(CommandCompletionDatabase.Load().Completions.Length == 2, "the fallback file is found on the next start");
            CommandCompletionDatabase.ProgramDirectory = null;
            CommandCompletionDatabase.UserDirectory = null;

            Check(new CommandCompletionDatabase().LookupCommand("ls -") == null, "default database is safe");
            Check(new CommandCompletionDatabase { Completions = null }.LookupCommand("ls -") == null, "explicit null array is safe");
            Check(new CommandCompletionDatabase { Completions = new CommandCompletion[] { null } }.LookupCommand("ls -") == null, "null command is safe");
            CommandCompletionDatabase nonexistent = CommandCompletionDatabase.Load(directory);
            Check(nonexistent.LoadWarning == null && nonexistent.Completions.Length == template.Completions.Length,
                "a path without a file falls back to the template");

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
            RunEditorChecks(directory);
            Console.WriteLine("All " + _passed + " regression checks passed.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
