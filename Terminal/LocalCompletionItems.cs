using VisualTTY;
using VisualTTY.DeviceTerminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmarTTY.Terminal;

/// <summary>
/// What the completion of the input line of a COM/TCP terminal offers and how it reads the word being completed: the built-in commands of
/// the terminal with their signatures, the programs on this computer, the names of a directory of the device as completion items, the
/// directory part of a path word, and the rules that end a word. It was the static part of the completion session of
/// <c>SerialTerminalControl</c>, as it was.
/// </summary>
public static class LocalCompletionItems
{
        public static readonly char[] CompletionMetaChars = { '`', '$', '<', '>', '|', '&', ';' };

        // The directory part of a path word as the shell reads it ("/etc/", "my dir/", "~/"), up to and with its last
        // slash; empty when the word names something in the shell's own directory.
        public static string GetDeviceDirectoryPrefix(string word)
        {
            string path = ShellCommandLine.Unquote(word ?? "");
            int lastSlash = path.LastIndexOf('/');
            return lastSlash >= 0 ? path.Substring(0, lastSlash + 1) : "";
        }

        public static List<CompletionItem> BuildPathItems(string directory, IReadOnlyList<DeviceDirectoryEntry> entries, bool directoriesOnly)
        {
            List<CompletionItem> items = new List<CompletionItem>();
            if (entries == null)
            {
                return items;
            }
            // Directories first, then files, each sorted by name.
            foreach (DeviceDirectoryEntry entry in entries
                .Where(e => e.IsDirectory || !directoriesOnly)
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.Ordinal))
            {
                string fullPath = directory + entry.Name;
                items.Add(new CompletionItem
                {
                    Icon = entry.IsDirectory ? VirtualTerminalPopupItemIcon.Directory : VirtualTerminalPopupItemIcon.File,
                    Text = entry.Name,
                    InsertText = ShellCommandLine.EscapeWord(fullPath) + (entry.IsDirectory ? "/" : ""),
                    Summary = new VirtualTerminalPopupSummary.FileOrDirectoryInfo
                    {
                        FullPath = fullPath,
                        IsDirectory = entry.IsDirectory
                    }
                });
            }
            return items;
        }

        public static bool ContainsUnescapedSpace(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ' ') continue;
                int backslashes = 0;
                for (int j = i - 1; j >= 0 && text[j] == '\\'; j--)
                {
                    backslashes++;
                }
                if (backslashes % 2 == 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static IEnumerable<CompletionItem> GetBuiltInCommands()
        {
            // Built-in commands available in serial terminal
            string[] builtins = new[]
            {
                "help", "clear", "history", "exit", "quit",
                "cd", "pwd", "ls", "dir", "cat", "type",
                "echo", "set", "unset", "export", "env",
                "send", "sendhex", "sendfile", "recv", "recvfile",
                "break", "reset", "reconnect", "disconnect",
                "log", "logstart", "logstop", "baud", "settings"
            };

            foreach (string cmd in builtins)
            {
                bool hasDoc = BuiltInCommandDocs.TryGetValue(cmd, out (string Usage, string Description) doc);
                string usage = hasDoc ? doc.Usage : cmd;
                string description = hasDoc ? doc.Description : $"Built-in command: {cmd}";
                yield return new CompletionItem
                {
                    Icon = VirtualTerminalPopupItemIcon.Command,
                    Text = cmd,
                    InsertText = cmd,
                    Summary = new VirtualTerminalPopupSummary.OptionInfo
                    {
                        Option = cmd,
                        // Summary = argument signature, Info = short description; the completion
                        // popup tooltip shows both, giving the user argument hints.
                        Description = new CommandCompletion.OptionDescription { Summary = usage, Info = description }
                    }
                };
            }
        }

        /// <summary>Argument signatures and short descriptions shown as hints for the built-in commands.</summary>
        private static readonly Dictionary<string, (string Usage, string Description)> BuiltInCommandDocs =
            new Dictionary<string, (string Usage, string Description)>(StringComparer.Ordinal)
            {
                ["help"] = ("help", "Show available built-in commands"),
                ["clear"] = ("clear", "Clear the terminal screen"),
                ["history"] = ("history", "Show the command history"),
                ["exit"] = ("exit", "End the session"),
                ["quit"] = ("quit", "End the session"),
                ["cd"] = ("cd <directory>", "Change the current directory"),
                ["pwd"] = ("pwd", "Print the current directory"),
                ["ls"] = ("ls [directory]", "List directory contents"),
                ["dir"] = ("dir [directory]", "List directory contents"),
                ["cat"] = ("cat <file>", "Print the contents of a file"),
                ["type"] = ("type <file>", "Print the contents of a file"),
                ["echo"] = ("echo <text>", "Print text to the terminal"),
                ["set"] = ("set <name>=<value>", "Set a session variable"),
                ["unset"] = ("unset <name>", "Remove a session variable"),
                ["export"] = ("export <name>=<value>", "Export a session variable"),
                ["env"] = ("env", "Show all session variables"),
                ["send"] = ("send <text>", "Send text to the device"),
                ["sendhex"] = ("sendhex <hex>", "Send hexadecimal bytes to the device"),
                ["sendfile"] = ("sendfile <file>", "Send the contents of a file to the device"),
                ["recv"] = ("recv <length>", "Receive a number of bytes from the device"),
                ["recvfile"] = ("recvfile <file>", "Receive bytes from the device into a file"),
                ["break"] = ("break", "Send a serial break condition"),
                ["reset"] = ("reset", "Reset the device connection"),
                ["reconnect"] = ("reconnect", "Reconnect to the device"),
                ["disconnect"] = ("disconnect", "Disconnect from the device"),
                ["log"] = ("log <file>", "Log terminal output to a file"),
                ["logstart"] = ("logstart <file>", "Start logging terminal output to a file"),
                ["logstop"] = ("logstop", "Stop logging terminal output"),
                ["baud"] = ("baud <rate>", "Set the baud rate, e.g. 115200"),
                ["settings"] = ("settings", "Open the terminal settings")
            };

        public static IEnumerable<CompletionItem> GetLocalExecutables(string filter)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] pathDirs = pathEnv.Split(Path.PathSeparator);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in pathDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string file in EnumerateFilesSafe(dir))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (seen.Add(name) && (string.IsNullOrEmpty(filter) || name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return new CompletionItem
                        {
                            Icon = VirtualTerminalPopupItemIcon.Command,
                            Text = name,
                            InsertText = name,
                            Summary = new VirtualTerminalPopupSummary.PathInfo { FullPath = file }
                        };
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateFilesSafe(string directory)
        {
            try
            {
                return Directory.GetFiles(directory);
            }
            catch
            {
                return new string[0];
            }
        }

        public static string CommonPrefix(string first, string second)
        {
            int limit = Math.Min(first.Length, second.Length);
            for (int i = 0; i < limit; i++)
            {
                if (first[i] != second[i])
                {
                    return first.Substring(0, i);
                }
            }
            return first.Substring(0, limit);
        }
}
