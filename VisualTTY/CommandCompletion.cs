using System;
using System.Diagnostics;
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
    private bool _invalidRegex;

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
            _Regex = null;
            _invalidRegex = false;
            if (value == null)
            {
                return;
            }
            try
            {
                _Regex = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                // Do not turn a broken restricted rule into an unrestricted command rule.
                _invalidRegex = true;
                Trace.TraceWarning("Ignoring invalid command completion regex: " + exception.Message);
            }
        }
    }

    public bool IsMatch(string cmdLine)
    {
        if (cmdLine == null || _invalidRegex)
        {
            return false;
        }
        if (_Regex != null)
        {
            try
            {
                return _Regex.IsMatch(cmdLine);
            }
            catch (RegexMatchTimeoutException)
            {
                _invalidRegex = true;
                Trace.TraceWarning("Disabled a command completion regex after a match timeout.");
                return false;
            }
        }
        return !string.IsNullOrWhiteSpace(Command) && cmdLine.StartsWith(Command + " ");
    }
}
