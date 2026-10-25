using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualTTY;

/// <summary>Base class of the editable copies of the completion database used by the command completions editor window.</summary>
public abstract class CompletionEditItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set(ref string field, string value, params string[] alsoChanged)
    {
        string newValue = value ?? "";
        if (field == newValue)
        {
            return false;
        }
        field = newValue;
        Notify(alsoChanged);
        return true;
    }

    protected void Notify(params string[] names)
    {
        PropertyChangedEventHandler handler = PropertyChanged;
        if (handler != null)
        {
            foreach (string name in names)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}

/// <summary>One option (flag or sub command) of a command. Edited as plain strings; empty strings are left out when saved.</summary>
public sealed class OptionEditItem : CompletionEditItem
{
    private string _value = "";
    private string _aliases = "";
    private string _shortDescription = "";
    private string _summary = "";
    private string _info = "";

    public string Value
    {
        get { return _value; }
        set { Set(ref _value, value, "Value", "DisplayName"); }
    }

    /// <summary>Other spellings of the option, separated by '|' (for example "--all").</summary>
    public string Aliases
    {
        get { return _aliases; }
        set { Set(ref _aliases, value, "Aliases"); }
    }

    public string ShortDescription
    {
        get { return _shortDescription; }
        set { Set(ref _shortDescription, value, "ShortDescription"); }
    }

    public string Summary
    {
        get { return _summary; }
        set { Set(ref _summary, value, "Summary"); }
    }

    public string Info
    {
        get { return _info; }
        set { Set(ref _info, value, "Info"); }
    }

    public string DisplayName
    {
        get { return string.IsNullOrWhiteSpace(_value) ? "(new option)" : _value; }
    }

    public OptionEditItem Clone()
    {
        return new OptionEditItem { Value = _value, Aliases = _aliases, ShortDescription = _shortDescription, Summary = _summary, Info = _info };
    }
}

/// <summary>One command of the database with its options.</summary>
public sealed class CommandEditItem : CompletionEditItem
{
    private string _command = "";
    private string _description = "";
    private string _commandLineRegex = "";
    private bool _suppressAutoTriggers;

    public ObservableCollection<OptionEditItem> Options { get; private set; }

    public CommandEditItem()
    {
        Options = new ObservableCollection<OptionEditItem>();
    }

    /// <summary>The command word: the entry applies to command lines that start with it followed by a space.</summary>
    public string Command
    {
        get { return _command; }
        set { Set(ref _command, value, "Command", "DisplayName"); }
    }

    public string Description
    {
        get { return _description; }
        set { Set(ref _description, value, "Description"); }
    }

    /// <summary>Optional: when set, the entry applies to the command lines this regular expression matches instead.</summary>
    public string CommandLineRegex
    {
        get { return _commandLineRegex; }
        set { Set(ref _commandLineRegex, value, "CommandLineRegex", "RegexError", "DisplayName"); }
    }

    public bool SuppressAutoTriggers
    {
        get { return _suppressAutoTriggers; }
        set
        {
            if (_suppressAutoTriggers != value)
            {
                _suppressAutoTriggers = value;
                Notify("SuppressAutoTriggers");
            }
        }
    }

    public string DisplayName
    {
        get
        {
            string command = _command.Trim();
            if (command.Length > 0)
            {
                return _commandLineRegex.Trim().Length > 0 ? command + "  (regex)" : command;
            }
            return _commandLineRegex.Trim().Length > 0 ? "regex: " + _commandLineRegex.Trim() : "(new command)";
        }
    }

    /// <summary>The reason the regular expression is not valid, or null.</summary>
    public string RegexError
    {
        get { return GetRegexError(_commandLineRegex); }
    }

    public static string GetRegexError(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }
        try
        {
            new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return null;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>Whether the entry would be used for the command line, exactly as the terminal decides it.</summary>
    public bool Matches(string commandLine)
    {
        return ToCompletion().IsMatch(commandLine);
    }

    public CommandEditItem Clone()
    {
        CommandEditItem copy = new CommandEditItem
        {
            Command = _command,
            Description = _description,
            CommandLineRegex = _commandLineRegex,
            SuppressAutoTriggers = _suppressAutoTriggers
        };
        foreach (OptionEditItem option in Options)
        {
            copy.Options.Add(option.Clone());
        }
        return copy;
    }

    internal CommandCompletion ToCompletion()
    {
        CommandCompletion completion = new CommandCompletion
        {
            Command = NullIfEmpty(_command),
            Description = NullIfEmpty(_description),
            SuppressAutoTriggers = _suppressAutoTriggers,
            CommandLineRegex = NullIfEmpty(_commandLineRegex),
            Options = Options.Select(option => new CommandCompletion.Option
            {
                Value = NullIfEmpty(option.Value),
                Aliases = NullIfEmpty(option.Aliases),
                ShortDescription = NullIfEmpty(option.ShortDescription),
                LongDescription = new CommandCompletion.OptionDescription { Summary = NullIfEmpty(option.Summary), Info = NullIfEmpty(option.Info) }
            }).ToArray()
        };
        return completion;
    }

    internal static string NullIfEmpty(string text)
    {
        string trimmed = (text ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

/// <summary>
/// The editable copy of a <see cref="CommandCompletionDatabase"/>: converts to and from the database, and checks it
/// before saving. Anything the loader would silently drop (a command without a name, an option without a value)
/// or disable (an invalid regular expression) is reported as an error here instead.
/// </summary>
public sealed class CompletionEditModel
{
    public ObservableCollection<CommandEditItem> Entries { get; private set; }

    public CompletionEditModel()
    {
        Entries = new ObservableCollection<CommandEditItem>();
    }

    public static CompletionEditModel FromDatabase(CommandCompletionDatabase database)
    {
        CompletionEditModel model = new CompletionEditModel();
        if (database == null || database.Completions == null)
        {
            return model;
        }
        foreach (CommandCompletion completion in database.Completions)
        {
            if (completion == null)
            {
                continue;
            }
            CommandEditItem entry = new CommandEditItem
            {
                Command = completion.Command,
                Description = completion.Description,
                CommandLineRegex = completion.CommandLineRegex,
                SuppressAutoTriggers = completion.SuppressAutoTriggers
            };
            foreach (CommandCompletion.Option option in completion.Options ?? new CommandCompletion.Option[0])
            {
                if (option == null)
                {
                    continue;
                }
                entry.Options.Add(new OptionEditItem
                {
                    Value = option.Value,
                    Aliases = option.Aliases,
                    ShortDescription = option.ShortDescription,
                    Summary = option.LongDescription.Summary,
                    Info = option.LongDescription.Info
                });
            }
            model.Entries.Add(entry);
        }
        return model;
    }

    public CommandCompletionDatabase ToDatabase()
    {
        return new CommandCompletionDatabase { Completions = Entries.Select(entry => entry.ToCompletion()).ToArray() };
    }

    /// <summary>Returns the problems that must be fixed before saving. Warnings are things that work but are probably not intended.</summary>
    public List<string> Validate(List<string> warnings)
    {
        List<string> errors = new List<string>();
        HashSet<string> commands = new HashSet<string>(StringComparer.Ordinal);
        int number = 0;
        foreach (CommandEditItem entry in Entries)
        {
            number++;
            string command = entry.Command.Trim();
            string regex = entry.CommandLineRegex.Trim();
            string name = command.Length > 0 ? "'" + command + "'" : "#" + number;
            if (command.Length == 0 && regex.Length == 0)
            {
                errors.Add("Command " + name + ": enter the command or a command line regex, otherwise the entry is ignored.");
            }
            if (command.IndexOfAny(new[] { ' ', '\t' }) >= 0 && regex.Length == 0)
            {
                warnings.Add("Command " + name + " contains a space: it only matches command lines that start with exactly this text.");
            }
            string regexError = CommandEditItem.GetRegexError(regex);
            if (regexError != null)
            {
                errors.Add("Command " + name + ": invalid regular expression: " + regexError);
            }
            if (command.Length > 0 && regex.Length == 0 && !commands.Add(command))
            {
                warnings.Add("Command '" + command + "' is listed more than once: only the first entry is used.");
            }
            int optionNumber = 0;
            HashSet<string> values = new HashSet<string>(StringComparer.Ordinal);
            foreach (OptionEditItem option in entry.Options)
            {
                optionNumber++;
                string value = option.Value.Trim();
                if (value.Length == 0)
                {
                    errors.Add("Command " + name + ", option #" + optionNumber + ": the option has no value, it would be dropped.");
                }
                else if (!values.Add(value))
                {
                    warnings.Add("Command " + name + ": option '" + value + "' is listed more than once.");
                }
            }
        }
        return errors;
    }
}
