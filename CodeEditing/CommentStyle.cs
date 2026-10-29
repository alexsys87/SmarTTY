using System;

namespace SmarTTY.CodeEditing
{
    /// <summary>How a language marks a line as a comment, and the line-by-line toggling of that mark.</summary>
    public sealed class CommentStyle
    {
        /// <summary>Text put in front of the line content, e.g. "# ".</summary>
        public readonly string Prefix;

        /// <summary>Text put after the line content for languages with block comments only, e.g. " -->"; empty otherwise.</summary>
        public readonly string Suffix;

        public CommentStyle(string prefix, string suffix = "")
        {
            Prefix = prefix;
            Suffix = suffix;
        }

        /// <summary>The comment style of a language; null when the language has no comments (plain text, diffs).</summary>
        public static CommentStyle ForLanguage(SyntaxHighlighting.Language language)
        {
            switch (language)
            {
                case SyntaxHighlighting.Language.Python:
                case SyntaxHighlighting.Language.Bash:
                case SyntaxHighlighting.Language.Config:
                case SyntaxHighlighting.Language.Script:
                    return new CommentStyle("# ");
                case SyntaxHighlighting.Language.CLike:
                    return new CommentStyle("// ");
                case SyntaxHighlighting.Language.Sql:
                    return new CommentStyle("-- ");
                case SyntaxHighlighting.Language.Markup:
                    return new CommentStyle("<!-- ", " -->");
                default:
                    return null;
            }
        }

        /// <summary>
        /// Comments out the lines, or uncomments them when every non-blank line is already commented.
        /// Blank lines are left alone unless all the lines are blank. The mark goes to the smallest indentation
        /// of the lines, so the commented block keeps its shape. Returns a new array.
        /// </summary>
        public string[] Toggle(string[] lines)
        {
            string[] result = (string[])lines.Clone();
            bool anyContent = false;
            bool allCommented = true;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0)
                {
                    continue;
                }
                anyContent = true;
                if (!IsCommented(lines[i]))
                {
                    allCommented = false;
                }
            }
            if (anyContent && allCommented)
            {
                for (int i = 0; i < result.Length; i++)
                {
                    if (lines[i].Trim().Length != 0)
                    {
                        result[i] = Uncomment(lines[i]);
                    }
                }
                return result;
            }
            int indent = int.MaxValue;
            for (int i = 0; i < lines.Length; i++)
            {
                if (anyContent && lines[i].Trim().Length == 0)
                {
                    continue;
                }
                indent = Math.Min(indent, IndentLength(lines[i]));
            }
            for (int i = 0; i < result.Length; i++)
            {
                if (anyContent && lines[i].Trim().Length == 0)
                {
                    continue;
                }
                string line = lines[i];
                int at = Math.Min(indent, line.Length);
                result[i] = line.Substring(0, at) + Prefix + line.Substring(at) + Suffix;
            }
            return result;
        }

        private bool IsCommented(string line)
        {
            string trimmed = line.TrimStart(' ', '\t');
            if (!trimmed.StartsWith(Prefix.TrimEnd(), StringComparison.Ordinal))
            {
                return false;
            }
            return Suffix.Length == 0 || line.TrimEnd(' ', '\t').EndsWith(Suffix.Trim(), StringComparison.Ordinal);
        }

        private string Uncomment(string line)
        {
            int start = IndentLength(line);
            string mark = Prefix.TrimEnd();
            int afterMark = start + mark.Length;
            if (afterMark < line.Length && line[afterMark] == ' ')
            {
                afterMark++;
            }
            string rest = line.Substring(afterMark);
            if (Suffix.Length != 0)
            {
                string tail = Suffix.Trim();
                string trimmedRest = rest.TrimEnd(' ', '\t');
                if (trimmedRest.EndsWith(tail, StringComparison.Ordinal))
                {
                    rest = trimmedRest.Substring(0, trimmedRest.Length - tail.Length);
                    if (rest.EndsWith(" ", StringComparison.Ordinal))
                    {
                        rest = rest.Substring(0, rest.Length - 1);
                    }
                }
            }
            return line.Substring(0, start) + rest;
        }

        private static int IndentLength(string line)
        {
            int length = 0;
            while (length < line.Length && (line[length] == ' ' || line[length] == '\t'))
            {
                length++;
            }
            return length;
        }
    }
}
