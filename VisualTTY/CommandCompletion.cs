using System.Text.RegularExpressions;

namespace VisualTTY;

public class CommandCompletion
{
    public struct OptionDescription
    {
        public string Summary { get; set; }

        public string Info { get; set; }
    }

    public class Option
    {
        public string Aliases;

        public string Value { get; set; }

        public string ShortDescription { get; set; }

        public OptionDescription LongDescription { get; set; }

        public override string ToString()
        {
            return Value;
        }
    }

    private Regex _Regex;

    public string Command;

    public string Description;

    public bool SuppressAutoTriggers;

    public Option[] Options;

    public string CommandLineRegex
    {
        get
        {
            return _Regex?.ToString();
        }
        set
        {
            try
            {
                if (value == null)
                {
                    _Regex = null;
                }
                else
                {
                    _Regex = new Regex(value);
                }
            }
            catch
            {
                _Regex = null;
            }
        }
    }

    public bool IsMatch(string cmdLine)
    {
        if (cmdLine == null)
        {
            return false;
        }
        if (_Regex != null)
        {
            return _Regex.IsMatch(cmdLine);
        }
        return cmdLine.StartsWith(Command + " ");
    }
}
