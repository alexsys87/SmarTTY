using SmarTTY.Controls;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using VisualTTY;
using VisualTTY.DeviceTerminal;

using Tcp = VisualTTY.DeviceTerminal.DeviceTerminalSettings.ConnectionSettings.Tcp;

// Runs the real COM/TCP terminal control connected over TCP to a "device" on this machine: a shell that prints
// "root@board:~# ", echoes every command it gets and answers "passwd" with a password prompt. The checks type into the
// local input line that continues the device's prompt:
//  * the grey suggestion from the history (Right, End and Ctrl+Right take it, Esc hides it, nothing at a password prompt),
//  * Up/Down by the beginning of the line, and both options of Settings -> Command Completion turned off.
// Run with:
//   dotnet build tests/DeviceLineEditorTests -c Release
//   xvfb-run -a mono tests/DeviceLineEditorTests/bin/Release/net48/DeviceLineEditorTests.exe
internal static class Program
{
    private sealed class Step
    {
        public string Name;

        public Action Act;

        public Func<bool> Done;

        public int TimeoutMs = 10000;
    }

    // Stands in for the suggestion popup of the application: it keeps what the terminal gave it.
    private sealed class TestPopup : ICompletionPopup
    {
        private CompletionItem[] _items = new CompletionItem[0];

        private string _filter = "";

        public bool IsOpen { get; private set; } = true;

        public CompletionItem[] Items => _items;

        public string Filter => _filter;

        public event Action<CompletionItem, VirtualKey> ItemSelected;

        public void Close()
        {
            IsOpen = false;
        }

        public void SetFilter(string text)
        {
            _filter = text ?? "";
        }

        public bool TryHandleKeyEvent(KeyStroke e)
        {
            return false;
        }

        public void DisplayException(Exception exception)
        {
            Console.WriteLine("  (popup) " + exception.Message);
        }

        public void SetItemList(CompletionItem[] items)
        {
            _items = items ?? new CompletionItem[0];
        }

        public CompletionItem GetSelectedItem(bool onlyIfPrefixMatch)
        {
            return _items.FirstOrDefault((CompletionItem item) => item.Text != null && item.Text.StartsWith(_filter, StringComparison.Ordinal));
        }
    }

    private static TestPopup _popup;

    private static CompletionItem PopupItem(string text)
    {
        return _popup?.Items.FirstOrDefault((CompletionItem item) => item.Text == text) ?? default;
    }

    // The texts of the items of the popup that the terminal asked for last, in order.
    private static string PopupTexts()
    {
        return _popup == null ? null : string.Join("|", _popup.Items.Select((CompletionItem item) => item.Text));
    }

    private static string PopupSummary(string text)
    {
        CompletionItem item = PopupItem(text);
        VirtualTerminalPopupSummary.OptionInfo info = item.Summary as VirtualTerminalPopupSummary.OptionInfo;
        return info == null ? null : info.Description.Summary + " / " + info.Description.Info;
    }

    // What the link scanner of the device terminal makes of the text the device printed: the text each link covers (the cells that have the
    // same target), whether the link is live ("+") or not yet ("-") when the text has been scanned, and what a click on it opens. A
    // piece is the text, or "column|text" for a scan that is told where on the screen the text goes (0: a new line).
    private static string ScanLinks(SerialTerminalControl terminal, bool finish, params string[] pieces)
    {
        List<string> opened = new List<string>();
        EventHandler<OpenFileEventArgs> handler = (object sender, OpenFileEventArgs e) => opened.Add(e.Path);
        terminal.OpenFileRequested += handler;
        try
        {
            IColumnAwareLinkScanner scanner = (IColumnAwareLinkScanner)terminal.CreateLinkScanner();
            StringBuilder text = new StringBuilder();
            List<LinkCell> cells = new List<LinkCell>();
            foreach (string piece in pieces)
            {
                int bar = piece.IndexOf('|');
                if (bar > 0 && int.TryParse(piece.Substring(0, bar), out int column))
                {
                    string chunk = piece.Substring(bar + 1);
                    text.Append(chunk);
                    cells.AddRange(scanner.Scan(chunk, column));
                }
                else
                {
                    text.Append(piece);
                    cells.AddRange(scanner.Scan(piece));
                }
            }
            if (finish)
            {
                scanner.Finish();
            }
            List<ILinkTarget> targets = new List<ILinkTarget>();
            foreach (LinkCell cell in cells)
            {
                if (cell.Target != null && !targets.Contains(cell.Target))
                {
                    targets.Add(cell.Target);
                }
            }
            List<string> result = new List<string>();
            foreach (ILinkTarget target in targets)
            {
                StringBuilder covered = new StringBuilder();
                for (int i = 0; i < cells.Count; i++)
                {
                    if (ReferenceEquals(cells[i].Target, target))
                    {
                        covered.Append(text[i]);
                    }
                }
                result.Add((target.IsActive ? "+" : "-") + covered);
                target.Activate();
            }
            return string.Join(" ", result) + " => " + string.Join(" ", opened);
        }
        finally
        {
            terminal.OpenFileRequested -= handler;
        }
    }

    private static Step LinkStep(SerialTerminalControl terminal, string name, bool finish, string[] pieces, string expected)
    {
        bool reported = false;
        return new Step
        {
            Name = "link scanner: " + name,
            // The scan does not depend on the time: one try is enough, and a difference is printed once.
            TimeoutMs = 300,
            Done = () =>
            {
                string actual = ScanLinks(terminal, finish, pieces);
                if (actual != expected && !reported)
                {
                    reported = true;
                    Console.WriteLine("  expected: " + expected + "\n  actual:   " + actual);
                }
                return actual == expected;
            }
        };
    }

    private const string ShellPrompt = "root@board:~# ";

    private const string PasswordPrompt = "Enter new password> ";

    private static readonly List<string> Failures = new List<string>();

    private static int _passed;

    private static readonly MethodInfo OnKeyDownMethod = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo OnKeyPressMethod = typeof(Control).GetMethod("OnKeyPress", BindingFlags.Instance | BindingFlags.NonPublic);

    // Command lines the device got, in order.
    private static readonly List<string> DeviceReceived = new List<string>();

    private static DeviceTerminalControl _control;

    [STAThread]
    private static int Main()
    {
        // A hang must fail the job instead of blocking it.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(3));
            Console.WriteLine("FAIL: the device line editor checks did not finish within 3 minutes");
            Environment.Exit(2);
        })
        {
            IsBackground = true
        }.Start();

        // The saved history of the device goes next to the program (portable mode), not into the profile of the user.
        string programDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        File.WriteAllText(Path.Combine(programDirectory, "portable.txt"), "");
        string dataDirectory = Path.Combine(programDirectory, "PortableSettings");
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(() => RunDevice(listener))
        {
            IsBackground = true
        }.Start();

        GlobalSettings globalSettings = new GlobalSettings();
        AutoCompletionTriggers triggers = globalSettings.VirtualTerminal.CompletionTriggers;
        DeviceTerminalSettings settings = new DeviceTerminalSettings
        {
            Connection = new Tcp
            {
                Host = "127.0.0.1",
                Port = port
            },
            LineEnding = DeviceTerminalSettings.LineEndingType.LF,
            TreatLFAsCRLF = true,
            AutoDetectPrompt = true
        };

        using Form form = new Form
        {
            Width = 900,
            Height = 500
        };
        DeviceTerminalControl control = new DeviceTerminalControl
        {
            Dock = DockStyle.Fill
        };
        form.Controls.Add(control);
        _control = control;
        InstallEditorStandIns();
        SerialTerminalControl terminal = (SerialTerminalControl)typeof(DeviceTerminalControl).GetField("_serialTerminalControl", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);

        terminal.CompletionPopupRequested += (object sender, CompletionPopupRequest request) =>
        {
            _popup = new TestPopup();
            request.Popup = _popup;
        };

        List<Step> steps = new List<Step>
        {
            new Step
            {
                Name = "the device's prompt is continued by the input line",
                Act = () =>
                {
                    control.ApplyCommonSettings(globalSettings);
                    control.ApplySettings(settings);
                },
                Done = () => terminal.HasInputLine && InputText(terminal) == ""
            },
            RunCommand(terminal, "uname -a"),
            RunCommand(terminal, "uptime"),
            RunCommand(terminal, "ls -la /tmp"),
            new Step
            {
                Name = "the rest of a typed command is suggested",
                Act = () => Type(terminal, "un"),
                Done = () => Suggestion(terminal) == "ame -a"
            },
            new Step
            {
                Name = "Right at the end of the line takes the suggestion",
                Act = () => Key(terminal, Keys.Right),
                Done = () => InputText(terminal) == "uname -a" && Suggestion(terminal) == null
            },
            new Step
            {
                Name = "the newest matching command is suggested",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "ls");
                },
                Done = () => Suggestion(terminal) == " -la /tmp"
            },
            new Step
            {
                Name = "Ctrl+Right takes the next word",
                Act = () => Key(terminal, Keys.Control | Keys.Right),
                Done = () => InputText(terminal) == "ls -la" && Suggestion(terminal) == " /tmp"
            },
            new Step
            {
                Name = "End takes the rest",
                Act = () => Key(terminal, Keys.End),
                Done = () => InputText(terminal) == "ls -la /tmp"
            },
            new Step
            {
                Name = "a suggestion for one letter",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "u");
                },
                Done = () => Suggestion(terminal) == "ptime"
            },
            new Step
            {
                Name = "no suggestion while the caret is not at the end of the line",
                Act = () => Key(terminal, Keys.Left),
                Done = () => Suggestion(terminal) == null
            },
            new Step
            {
                Name = "Right then only moves the caret back to the end",
                Act = () => Key(terminal, Keys.Right),
                Done = () => InputText(terminal) == "u" && Suggestion(terminal) == "ptime"
            },
            new Step
            {
                Name = "Up shows the newest command that starts with the typed text",
                Act = () => Key(terminal, Keys.Up),
                Done = () => InputText(terminal) == "uptime"
            },
            new Step
            {
                Name = "Up again skips the commands that start otherwise",
                Act = () => Key(terminal, Keys.Up),
                Done = () => InputText(terminal) == "uname -a"
            },
            new Step
            {
                Name = "Up stops at the oldest match",
                Act = () => Key(terminal, Keys.Up),
                Done = () => InputText(terminal) == "uname -a"
            },
            new Step
            {
                Name = "Down goes back to the newer match",
                Act = () => Key(terminal, Keys.Down),
                Done = () => InputText(terminal) == "uptime"
            },
            new Step
            {
                Name = "Down past the newest match brings the typed text back",
                Act = () => Key(terminal, Keys.Down),
                Done = () => InputText(terminal) == "u" && Suggestion(terminal) == "ptime"
            },
            new Step
            {
                Name = "Esc hides the suggestion",
                Act = () => Key(terminal, Keys.Escape),
                Done = () => Suggestion(terminal) == null
            },
            new Step
            {
                Name = "Right does not take a hidden suggestion",
                Act = () => Key(terminal, Keys.Right),
                Done = () => InputText(terminal) == "u"
            },
            new Step
            {
                Name = "typing brings the suggestion back",
                Act = () => Type(terminal, "p"),
                Done = () => Suggestion(terminal) == "time"
            },
            new Step
            {
                Name = "the suggestions can be turned off",
                Act = () =>
                {
                    triggers.HistorySuggestions = false;
                    ClearLine(terminal);
                    Type(terminal, "u");
                },
                Done = () => Suggestion(terminal) == null && InputText(terminal) == "u"
            },
            new Step
            {
                Name = "with the prefix search off Up shows the newest command whatever was typed",
                Act = () =>
                {
                    triggers.HistorySuggestions = true;
                    triggers.HistoryPrefixSearch = false;
                    Key(terminal, Keys.Up);
                },
                Done = () => InputText(terminal) == "ls -la /tmp"
            },
            new Step
            {
                Name = "Down brings the typed text back",
                Act = () =>
                {
                    triggers.HistoryPrefixSearch = true;
                    Key(terminal, Keys.Down);
                },
                Done = () => InputText(terminal) == "u"
            },
            new Step
            {
                // The history is "uname -a", "uptime", "ls -la /tmp" (oldest first); "t" is in the last two.
                Name = "Ctrl+R shows the newest command that contains the typed text",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "t");
                    Key(terminal, Keys.Control | Keys.R);
                },
                Done = () => InputText(terminal) == "ls -la /tmp"
            },
            new Step
            {
                Name = "Ctrl+R again shows the command before it",
                Act = () => Key(terminal, Keys.Control | Keys.R),
                Done = () => InputText(terminal) == "uptime"
            },
            new Step
            {
                Name = "Ctrl+R after the oldest match goes round to the newest",
                Act = () => Key(terminal, Keys.Control | Keys.R),
                Done = () => InputText(terminal) == "ls -la /tmp"
            },
            new Step
            {
                Name = "Esc ends the search and brings the typed text back",
                Act = () => Key(terminal, Keys.Escape),
                Done = () => InputText(terminal) == "t"
            },
            new Step
            {
                Name = "Ctrl+R finds nothing for a text that no command contains: the line stays",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "zzz");
                    Key(terminal, Keys.Control | Keys.R);
                },
                Done = () => InputText(terminal) == "zzz"
            },
            new Step
            {
                Name = "Esc ends that search too",
                Act = () => Key(terminal, Keys.Escape),
                Done = () => InputText(terminal) == "zzz"
            },
            new Step
            {
                Name = "Ctrl+R ignores the case of the typed text",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "UNAME");
                    Key(terminal, Keys.Control | Keys.R);
                },
                Done = () => InputText(terminal) == "uname -a"
            },
            new Step
            {
                Name = "Up after a search starts from the typed text, not from the match",
                Act = () =>
                {
                    Key(terminal, Keys.Escape);
                    ClearLine(terminal);
                    Type(terminal, "up");
                    Key(terminal, Keys.Up);
                },
                Done = () => InputText(terminal) == "uptime"
            },
            new Step
            {
                Name = "the device asks for a password",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "passwd\r");
                },
                Done = () => terminal.HasInputLine && InputPrompt(terminal).EndsWith(PasswordPrompt, StringComparison.Ordinal)
            },
            new Step
            {
                Name = "nothing is suggested at a password prompt",
                Act = () => Type(terminal, "u"),
                Done = () => InputText(terminal) == "u" && Suggestion(terminal) == null
            },
            new Step
            {
                Name = "the password is sent",
                Act = () => Type(terminal, "ptime-secret\r"),
                Done = () => Received().Contains("uptime-secret") && terminal.HasInputLine && InputPrompt(terminal).EndsWith(ShellPrompt, StringComparison.Ordinal)
            },
            new Step
            {
                Name = "the password is not suggested later",
                Act = () => Type(terminal, "u"),
                Done = () => Suggestion(terminal) == "ptime"
            },
            new Step
            {
                // "Command history..." of the terminal menu: the window lists the commands, the newest first.
                Name = "the history window lists the commands sent to the device, the newest first",
                Act = () => ClearLine(terminal),
                Done = () => string.Join("|", control.CreateCommandHistorySource().Load()) == "passwd|ls -la /tmp|uptime|uname -a"
            },
            new Step
            {
                Name = "a command deleted in the history window is no longer suggested",
                Act = () =>
                {
                    control.DeleteFromCommandHistory(new[] { "uptime" });
                    Type(terminal, "u");
                },
                Done = () => Suggestion(terminal) == "name -a" && !control.CreateCommandHistorySource().Load().Contains("uptime")
            },
            new Step
            {
                Name = "it is gone from the saved history of the device as well",
                Done = () => !SavedHistory(control).Contains("uptime") && SavedHistory(control).Contains("uname -a")
            },
            new Step
            {
                Name = "a command chosen in the history window replaces the typed line",
                Act = () => control.CreateCommandHistorySource().Insert("ls -la /tmp"),
                Done = () => InputText(terminal) == "ls -la /tmp"
            },
            new Step
            {
                Name = "the line is cleared",
                Act = () => ClearLine(terminal),
                Done = () => InputText(terminal) == ""
            },
            new Step
            {
                // The saved commands of the device, the built-in commands of the terminal and the programs on this computer.
                Name = "Tab on an empty line lists the saved commands, the built-in commands and the programs of this computer",
                Act = () => Key(terminal, Keys.Tab),
                Done = () => _popup != null && PopupItem("help").Text != null && PopupItem("settings").Text != null && PopupItem("ls -la /tmp").Text != null
            },
            new Step
            {
                Name = "the list holds a saved command once, as a saved command",
                Done = () => _popup.Items.Count((CompletionItem item) => item.Text == "ls -la /tmp") == 1 && PopupSummary("ls -la /tmp") == "Saved command / "
            },
            new Step
            {
                Name = "a built-in command comes with its signature and its description",
                Done = () => PopupSummary("cd") == "cd <directory> / Change the current directory" && PopupSummary("sendhex") == "sendhex <hex> / Send hexadecimal bytes to the device"
            },
            new Step
            {
                Name = "a built-in command has the icon of a command",
                Done = () => PopupItem("baud").Icon == VirtualTerminalPopupItemIcon.Command && PopupItem("baud").InsertText == "baud"
            },
            new Step
            {
                Name = "the list holds all 30 built-in commands",
                Done = () => new[] { "help", "clear", "history", "exit", "quit", "cd", "pwd", "ls", "dir", "cat", "type", "echo", "set", "unset", "export", "env", "send", "sendhex", "sendfile", "recv", "recvfile", "break", "reset", "reconnect", "disconnect", "log", "logstart", "logstop", "baud", "settings" }.All((string name) => PopupItem(name).Text != null)
            },
            new Step
            {
                Name = "Esc closes the list and leaves the line",
                Act = () => Key(terminal, Keys.Escape),
                Done = () => !_popup.IsOpen && InputText(terminal) == ""
            },
            new Step
            {
                Name = "Tab after the start of a saved command completes it",
                Act = () =>
                {
                    Type(terminal, "unam");
                    Key(terminal, Keys.Tab);
                },
                Done = () => InputText(terminal) == "uname -a"
            },
            new Step
            {
                Name = "that list is closed after the command is completed",
                Done = () => !_popup.IsOpen
            },
            new Step
            {
                // Only the saved commands match a word that has letters in it: the built-in ones are offered for an empty word.
                Name = "Tab after the start of a built-in command offers nothing and leaves the line",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "hel");
                    Key(terminal, Keys.Tab);
                },
                Done = () => _popup != null && _popup.Filter == "hel" && _popup.Items.Length == 0 && InputText(terminal) == "hel"
            },
            new Step
            {
                Name = "the line is cleared after the completion checks",
                Act = () =>
                {
                    Key(terminal, Keys.Escape);
                    ClearLine(terminal);
                },
                Done = () => InputText(terminal) == ""
            },
            // A word at the end of the text is still undecided (it may go on in the next piece): it has a target that is not live ("-").
            LinkStep(terminal, "an address in a sentence covers its full stop but opens without it; the word after it is not live", true, new[] { "see https://example.com/a. now" }, "+https://example.com/a. -now => https://example.com/a"),
            LinkStep(terminal, "an address that comes in two pieces is one link, with its query", true, new[] { "go https://exa", "mple.com/x?y=1&z=2 ok" }, "+https://example.com/x?y=1&z=2 -ok => https://example.com/x?y=1&z=2"),
            LinkStep(terminal, "an address at the end of the text is not live until the text is finished", false, new[] { "at https://example.com/z" }, "-https://example.com/z => https://example.com/z"),
            LinkStep(terminal, "and it is live when the text is finished", true, new[] { "at https://example.com/z" }, "+https://example.com/z => https://example.com/z"),
            LinkStep(terminal, "a path of the device and a bare name are no links (only the last word is left undecided)", true, new[] { "/tmp/x build.log ./run.sh" }, "-./run.sh => "),
            LinkStep(terminal, "a word after a backslash and a blank is a word of its own", true, new[] { "a\\ b.txt" }, "-b.txt => "),
            LinkStep(terminal, "an address does not go on in the next line", true, new[] { "0|https://example.com/long", "0|tail" }, "+https://example.com/long -tail => https://example.com/long"),
            LinkStep(terminal, "but it goes on in the row of a wrapped line", true, new[] { "0|https://example.com/long", "-1|tail" }, "+https://example.com/longtail => https://example.com/longtail"),
            LinkStep(terminal, "two addresses are two links", true, new[] { "https://a.org/1 and ftp://b.org/2" }, "+https://a.org/1 +ftp://b.org/2 => https://a.org/1 ftp://b.org/2"),
            LinkStep(terminal, "an address ends at a quote", true, new[] { "<a href=\"https://a.org/p\">x" }, "+https://a.org/p -x => https://a.org/p"),
            LinkStep(terminal, "a path with a drive letter is a link candidate; on this system its full path is not one of this computer", true, new[] { "see C:\\dir\\a.txt ok" }, "-C:\\dir\\a.txt -ok => "),
            new Step
            {
                Name = "a listing of a directory of the device gives its names, as the shell printed them",
                Act = () => StartListing(control, "/etc", "a"),
                Done = () => ListedText() == "a=init.d/,hosts,passwd" && terminal.HasInputLine && InputText(terminal) == ""
            },
            new Step
            {
                Name = "the command, its output and the markers are kept off the screen",
                Done = () => !DocumentText(terminal).Contains("__SMARTTY") && !DocumentText(terminal).Contains("command ls") && !DocumentText(terminal).Contains("init.d")
            },
            new Step
            {
                Name = "the device got one command for it, which ends the line the way the connection does",
                Done = () => ReceivedShort().EndsWith("|ls /etc", StringComparison.Ordinal)
            },
            new Step
            {
                Name = "a device that does not answer is given up after four seconds, and the listing says so",
                Act = () => StartListing(control, "/silent", "s"),
                Done = () => ListedText() == "a=init.d/,hosts,passwd|s=none",
                TimeoutMs = 9000
            },
            new Step
            {
                Name = "what such a device printed is shown as it came, and its prompt is the input line again",
                Done = () => DocumentText(terminal).Contains("printf") && terminal.HasInputLine && InputText(terminal) == "",
                TimeoutMs = 9000
            },
            new Step
            {
                Name = "a listing whose prompt is late is given its names after half a second",
                Act = () => StartListing(control, "/noprompt", "n"),
                Done = () => ListedText() == "a=init.d/,hosts,passwd|s=none|n=a.txt,sub/"
            },
            new Step
            {
                Name = "and the prompt that comes later is the input line",
                Done = () => terminal.HasInputLine && InputText(terminal) == "",
                TimeoutMs = 9000
            },
            new Step
            {
                // The second request waits for the first; the third takes the place of the second (which is told that nothing was listed at once).
                // The blank typed after "echo" starts the completion of a word, which asks for a listing of its own: it takes the place of the third
                // request at once (told "none"). When the first listing has finished, what was typed goes out and the waiting request (the one of the
                // completion) is asked again: the shell has not printed its prompt yet, so it cannot be listed for (CanListDeviceDirectory) and its
                // callback gets nothing. Recorded as it is.
                Name = "a request made while a listing runs waits, a newer one replaces it, and what is typed meanwhile is held",
                Act = () =>
                {
                    StartListing(control, "/slow", "1");
                    StartListing(control, "/etc", "2");
                    StartListing(control, "/other", "3");
                    Type(terminal, "echo held\r");
                },
                Done = () => ListedText().EndsWith("|2=none|3=none|1=a.txt,sub/", StringComparison.Ordinal) && terminal.HasInputLine && InputText(terminal) == "",
                TimeoutMs = 9000
            },
            new Step
            {
                Name = "the device got the first listing, then what was typed, and no listing for the waiting request",
                Done = () => ReceivedShort().EndsWith("|ls /slow|echo held", StringComparison.Ordinal)
            },
            new Step
            {
                Name = "all six requests were accepted (a waiting one is accepted too)",
                Done = () => string.Join(",", ListingStarted) == "True,True,True,True,True,True"
            },
            new Step
            {
                // The editor of a command list is the themed window of the application; here it stands in for it and says what it was asked.
                Name = "the command list of this device is saved and Tab offers it after the recent commands",
                Act = () =>
                {
                    _editorAnswers.Enqueue(new List<string> { "reboot", "show version" });
                    InvokeEditCommandList(control, forThisDevice: true);
                },
                Done = () => Suggestions(terminal).EndsWith("|reboot|show version", StringComparison.Ordinal) && string.Join("|", Storage().LoadCommands(DeviceKey(port))) == "reboot|show version"
            },
            new Step
            {
                Name = "the editor was told the title and the description of the list of this device",
                Done = () => _editorTitles.Count == 1 && _editorTitles[0].StartsWith("Commands - ", StringComparison.Ordinal) && _editorDescriptions[0].Contains("Commands offered by Tab for ")
            },
            new Step
            {
                Name = "the list for all devices is saved too and comes after the one of this device (a repeated command keeps the first place)",
                Act = () =>
                {
                    _editorAnswers.Enqueue(new List<string> { "ls /", "reboot" });
                    InvokeEditCommandList(control, forThisDevice: false);
                },
                Done = () => Suggestions(terminal).EndsWith("|reboot|show version|ls /", StringComparison.Ordinal) && string.Join("|", Storage().LoadCommands(null)) == "ls /|reboot"
            },
            new Step
            {
                Name = "the editor was told the title of the list for all devices",
                Done = () => _editorTitles.Count == 2 && _editorTitles[1] == "Commands for all devices"
            },
            new Step
            {
                Name = "an editor that gives nothing (cancelled) leaves the lists as they are",
                Act = () =>
                {
                    _suggestionsBefore = Suggestions(terminal);
                    _editorAnswers.Enqueue(null);
                    InvokeEditCommandList(control, forThisDevice: true);
                },
                Done = () => _editorTitles.Count == 3 && Suggestions(terminal) == _suggestionsBefore && string.Join("|", Storage().LoadCommands(DeviceKey(port))) == "reboot|show version"
            },
            new Step
            {
                // The blank is not sent (the command goes to the device trimmed), but the saved history looks at the raw text.
                Name = "a command typed with a leading blank is sent but not saved",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, " blank-cmd\r");
                },
                Done = () => Received().EndsWith("|blank-cmd", StringComparison.Ordinal) && terminal.HasInputLine && InputText(terminal) == ""
            },
            new Step
            {
                Name = "what was typed at the password prompt is in neither the saved history nor the file of the device",
                Done = () => !SavedHistory(control).Any((string command) => command.Contains("secret")) && !Storage().LoadHistory(DeviceKey(port)).Items.Any((string command) => command.Contains("secret"))
            },
            new Step
            {
                Name = "it is in the history of the terminal and not in the saved history",
                Done = () => terminal.CommandHistory.Contains("blank-cmd") && !SavedHistory(control).Contains(" blank-cmd") && !SavedHistory(control).Contains("blank-cmd")
            },
            new Step
            {
                Name = "a command typed normally is saved, to the file of the device as well, and Tab offers it first",
                Act = () => Type(terminal, "saved-cmd\r"),
                Done = () => SavedHistory(control).Contains("saved-cmd") && Storage().LoadHistory(DeviceKey(port)).Items.Contains("saved-cmd") && Suggestions(terminal).StartsWith("saved-cmd|", StringComparison.Ordinal) && terminal.HasInputLine && InputText(terminal) == ""
            },
            new Step
            {
                Name = "clearing the saved history asks first, and No keeps everything",
                Act = () =>
                {
                    _messageBoxAnswer = DialogResult.No;
                    InvokePrivate(control, "ClearSavedHistory");
                },
                Done = () => _messageBoxTexts.Count == 1 && _messageBoxTexts[0].StartsWith("Delete the saved command history of ", StringComparison.Ordinal) && SavedHistory(control).Contains("saved-cmd") && terminal.CommandHistory.Contains("saved-cmd")
            },
            new Step
            {
                Name = "Yes clears the saved history, the history of the terminal, the file of the device and what Tab offered from them; the lists stay",
                Act = () =>
                {
                    _messageBoxAnswer = DialogResult.Yes;
                    InvokePrivate(control, "ClearSavedHistory");
                },
                Done = () => SavedHistory(control).Count == 0 && terminal.CommandHistory.Count == 0 && Storage().LoadHistory(DeviceKey(port)).Count == 0 && Suggestions(terminal) == "reboot|show version|ls /"
            },
            new Step
            {
                Name = "with nothing to clear the question is not asked",
                Act = () => InvokePrivate(control, "ClearSavedHistory"),
                Done = () => _messageBoxTexts.Count == 2
            },
            new Step
            {
                // The history of the terminal has a command that the saved history does not (a leading blank): that is something to clear.
                Name = "with only the history of the terminal holding a command the question is asked",
                Act = () =>
                {
                    Type(terminal, " lead-only\r");
                    _messageBoxAnswer = DialogResult.No;
                },
                Done = () => terminal.HasInputLine && InputText(terminal) == "" && terminal.CommandHistory.Contains("lead-only") && SavedHistory(control).Count == 0
            },
            new Step
            {
                Name = "and it is asked when the clearing is asked for",
                Act = () => InvokePrivate(control, "ClearSavedHistory"),
                Done = () => _messageBoxTexts.Count == 3 && terminal.CommandHistory.Contains("lead-only")
            },
            new Step
            {
                // The only directory of /etc is applied at once (the best match is taken when the list is there), and the list is gone after it.
                Name = "Tab after \"cd /etc/\" asks the device for the directory, offers its directories only and applies the only one",
                Act = () =>
                {
                    ClearLine(terminal);
                    Type(terminal, "cd /etc/");
                    Key(terminal, Keys.Tab);
                },
                Done = () => PopupTexts() == "" && InputText(terminal) == "cd /etc/init.d/" && terminal.HasInputLine
            },
            new Step
            {
                // The blank typed after "cd" started a completion of its own (a listing of the shell's directory), as in the steps of the listing above.
                // (A listing of the shell's own directory is "cd -- ." without quotes: the helper of the stand shows it as "ls " with no directory.)
                Name = "the device got a listing command for the shell's directory and one for the directory as it was typed",
                Done = () => ReceivedShort().EndsWith("|ls |ls /etc/", StringComparison.Ordinal)
            },
            new Step
            {
                Name = "Tab after \"cat /etc/h\" lists the files and completes the only name that matches",
                Act = () =>
                {
                    Key(terminal, Keys.Escape);
                    ClearLine(terminal);
                    Type(terminal, "cat /etc/h");
                    Key(terminal, Keys.Tab);
                },
                Done = () => InputText(terminal) == "cat /etc/hosts"
            },
            new Step
            {
                Name = "Tab after \"cat /cfg/c\" completes the common start of the names that match and offers them",
                Act = () =>
                {
                    Key(terminal, Keys.Escape);
                    ClearLine(terminal);
                    Type(terminal, "cat /cfg/c");
                    Key(terminal, Keys.Tab);
                },
                Done = () => InputText(terminal) == "cat /cfg/config." && PopupTexts() == "config.a|config.b"
            },
            new Step
            {
                // The answer for /delay/ comes after 600 ms; by then the word has been cut back to "/", so the answer is dropped.
                Name = "the answer of a listing that the word has moved away from is dropped",
                Act = () =>
                {
                    Key(terminal, Keys.Escape);
                    ClearLine(terminal);
                    Type(terminal, "cat /delay/");
                    Key(terminal, Keys.Tab);
                    for (int i = 0; i < 6; i++)
                    {
                        Key(terminal, Keys.Back);
                    }
                },
                Done = () => InputText(terminal) == "cat /" && PopupTexts() == "sub|a.txt" && terminal.HasInputLine,
                TimeoutMs = 9000
            },
            new Step
            {
                Name = "the list is closed and the line cleared after the path completion checks",
                Act = () =>
                {
                    Key(terminal, Keys.Escape);
                    ClearLine(terminal);
                },
                Done = () => InputText(terminal) == ""
            },
            new Step
            {
                // Disconnect returns: closing the connection did not hang.
                Name = "the connection is closed without hanging",
                Act = () => control.Disconnect()
            }
        };
        ExecuteSteps(form, terminal, steps);
        listener.Stop();
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("All " + _passed + " device line editor checks passed.");
            return 0;
        }
        Console.WriteLine(Failures.Count + " device line editor check(s) FAILED:");
        foreach (string failure in Failures)
        {
            Console.WriteLine("  " + failure);
        }
        return 1;
    }

    // The shell of the device: a prompt, the echo of every line it gets and a password prompt after "passwd".
    private static void RunDevice(TcpListener listener)
    {
        try
        {
            using TcpClient client = listener.AcceptTcpClient();
            NetworkStream stream = client.GetStream();
            void Write(string text)
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                stream.Write(data, 0, data.Length);
            }
            Write("BusyBox v1.36.1 built-in shell (ash)\r\n" + ShellPrompt);
            StringBuilder line = new StringBuilder();
            bool askingPassword = false;
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                {
                    return;
                }
                foreach (char ch in Encoding.UTF8.GetString(buffer, 0, count))
                {
                    if (ch != '\r' && ch != '\n')
                    {
                        line.Append(ch);
                        continue;
                    }
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    string command = line.ToString();
                    line.Clear();
                    lock (DeviceReceived)
                    {
                        DeviceReceived.Add(command);
                    }
                    if (askingPassword)
                    {
                        // A password is not echoed.
                        askingPassword = false;
                        Write("\r\n" + ShellPrompt);
                    }
                    else if (command == "passwd")
                    {
                        askingPassword = true;
                        Write(command + "\r\n" + PasswordPrompt);
                    }
                    else if (command.Contains(DeviceDirectoryListing.MarkerHead))
                    {
                        AnswerListing(command, Write);
                    }
                    else
                    {
                        Write(command + "\r\n" + ShellPrompt);
                    }
                }
            }
        }
        catch
        {
        }
    }

    // What the shell of the device prints for the command that lists a directory (DeviceDirectoryListing.BuildCommand): its echo, the two marker lines
    // around "ls -1Ap" and the prompt. Some directories make it behave the way a busy or a different shell does.
    private static void AnswerListing(string command, Action<string> write)
    {
        string token = Regex.Match(command, "'B(\\d+)'").Groups[1].Value;
        string directory = Regex.Match(command, "cd -- '([^']*)'").Groups[1].Value;
        string begin = DeviceDirectoryListing.BeginMarker(token) + "\r\n";
        string end = DeviceDirectoryListing.EndMarker(token) + "\r\n";
        string names = directory == "/etc" || directory == "/etc/" ? "init.d/\r\nhosts\r\npasswd\r\n"
            : directory == "/cfg/" ? "config.a\r\nconfig.b\r\nother\r\n"
            : directory == "/delay/" ? "delayed.txt\r\n"
            : "a.txt\r\nsub/\r\n";
        switch (directory)
        {
            case "/silent":
                // Not a shell that answers: only the echo comes, and the prompt a while after the terminal gave up.
                write(command + "\r\n");
                Thread.Sleep(4600);
                write(ShellPrompt);
                break;
            case "/noprompt":
                // The listing is complete but the prompt after it is late.
                write(command + "\r\n" + begin + names + end);
                Thread.Sleep(1000);
                write(ShellPrompt);
                break;
            case "/delay/":
                // A slow answer of the path completion: the word may have moved to another directory by then.
                Thread.Sleep(600);
                write(command + "\r\n" + begin + names + end + ShellPrompt);
                break;
            case "/slow":
                Thread.Sleep(600);
                write(command + "\r\n" + begin + names + end + ShellPrompt);
                break;
            default:
                write(command + "\r\n" + begin + names + end + ShellPrompt);
                break;
        }
    }

    // The answers of the listings that were asked for, in the order their callbacks ran ("label=name,name/" or "label=none").
    private static readonly List<string> Listed = new List<string>();

    private static readonly List<bool> ListingStarted = new List<bool>();

    private static bool StartListing(DeviceTerminalControl control, string directory, string label)
    {
        MethodInfo method = typeof(DeviceTerminalControl).GetMethod("ListDeviceDirectory", BindingFlags.Instance | BindingFlags.NonPublic);
        Action<IReadOnlyList<DeviceDirectoryEntry>> done = entries =>
        {
            lock (Listed)
            {
                Listed.Add(label + "=" + (entries == null ? "none" : string.Join(",", entries.Select((DeviceDirectoryEntry entry) => entry.Name + (entry.IsDirectory ? "/" : "")))));
            }
        };
        bool started = (bool)method.Invoke(control, new object[] { directory, done });
        lock (Listed)
        {
            ListingStarted.Add(started);
        }
        return started;
    }

    private static string ListedText()
    {
        lock (Listed)
        {
            return string.Join("|", Listed);
        }
    }

    // What the device got, a listing command as "ls <directory>".
    private static string ReceivedShort()
    {
        lock (DeviceReceived)
        {
            return string.Join("|", DeviceReceived.Select((string line) => line.Contains(DeviceDirectoryListing.MarkerHead) ? "ls " + Regex.Match(line, "cd -- '([^']*)'").Groups[1].Value : line));
        }
    }

    private static Step RunCommand(SerialTerminalControl terminal, string command)
    {
        return new Step
        {
            Name = "a command goes to the device and its prompt comes back: " + command,
            Act = () => Type(terminal, command + "\r"),
            // The first command after a shell prompt also carries the size of the terminal ("stty cols 87 rows 23; ...").
            Done = () => Received().Split('|').Any((string line) => line == command || line.EndsWith("; " + command, StringComparison.Ordinal)) && terminal.HasInputLine && InputText(terminal) == ""
        };
    }

    private static string Received()
    {
        lock (DeviceReceived)
        {
            return string.Join("|", DeviceReceived);
        }
    }

    private static void ExecuteSteps(Form form, TerminalEmulatorControl terminal, List<Step> steps)
    {
        int index = 0;
        bool started = false;
        DateTime startedAt = DateTime.UtcNow;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer
        {
            Interval = 50
        };
        timer.Tick += (sender, e) =>
        {
            if (index >= steps.Count)
            {
                timer.Stop();
                form.Close();
                return;
            }
            Step step = steps[index];
            try
            {
                PumpDispatcher();
                if (!started)
                {
                    started = true;
                    startedAt = DateTime.UtcNow;
                    step.Act?.Invoke();
                }
                if (step.Done == null || step.Done())
                {
                    _passed++;
                    Console.WriteLine("PASS: " + step.Name);
                    index++;
                    started = false;
                }
                else if ((DateTime.UtcNow - startedAt).TotalMilliseconds > step.TimeoutMs)
                {
                    Fail(step.Name, terminal);
                    // The following steps build on this one.
                    index = steps.Count;
                }
            }
            catch (Exception exception)
            {
                Fail(step.Name + " (" + exception.GetType().Name + ": " + exception.Message + ")", terminal);
                index = steps.Count;
            }
        };
        form.Shown += (sender, e) => timer.Start();
        Application.Run(form);
        timer.Dispose();
    }

    /// <summary>
    /// Runs what the connection posted to the dispatcher of this thread. In the application the WinForms message loop
    /// does that (the WPF dispatcher uses window messages); under Mono the dispatcher has a queue of its own.
    /// </summary>
    private static void PumpDispatcher()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Fail(string name, TerminalEmulatorControl terminal)
    {
        Failures.Add(name);
        Console.WriteLine("FAIL: " + name);
        Console.WriteLine("  status: " + _control?.StatusText);
        Console.WriteLine("  device got: " + Received());
        Console.WriteLine("  popup items: " + PopupTexts());
        Console.WriteLine("  listings: " + ListedText() + " (accepted: " + string.Join(",", ListingStarted) + ")");
        Console.WriteLine("  input line: '" + InputText(terminal as SerialTerminalControl) + "', suggestion: '" + Suggestion(terminal as SerialTerminalControl) + "'");
        Console.WriteLine("  screen:");
        foreach (string line in DocumentText(terminal).Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                Console.WriteLine("    " + line);
            }
        }
    }

    private static void Key(TerminalEmulatorControl terminal, Keys keyData)
    {
        KeyEventArgs e = new KeyEventArgs(keyData);
        OnKeyDownMethod.Invoke(terminal, new object[] { e });
        char? ch = null;
        switch (keyData)
        {
            case Keys.Return:
                ch = '\r';
                break;
            case Keys.Back:
                ch = '\b';
                break;
            case Keys.Escape:
                ch = '\u001b';
                break;
        }
        // As in Windows: a handled key with SuppressKeyPress produces no character.
        if (ch.HasValue && !e.SuppressKeyPress)
        {
            OnKeyPressMethod.Invoke(terminal, new object[] { new KeyPressEventArgs(ch.Value) });
        }
    }

    private static void Type(TerminalEmulatorControl terminal, string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\r')
            {
                Key(terminal, Keys.Return);
                continue;
            }
            OnKeyPressMethod.Invoke(terminal, new object[] { new KeyPressEventArgs(ch) });
        }
    }

    // Backspace over the whole line (Ctrl+C would go to the device, which prints a new prompt).
    private static void ClearLine(SerialTerminalControl terminal)
    {
        Key(terminal, Keys.End);
        for (int i = (InputText(terminal) ?? "").Length; i > 0; i--)
        {
            Key(terminal, Keys.Back);
        }
    }

    private static object InputLine(SerialTerminalControl terminal)
    {
        return typeof(SerialTerminalControl).GetField("_inputLine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(terminal);
    }

    private static string InputText(SerialTerminalControl terminal)
    {
        object line = terminal == null ? null : InputLine(terminal);
        return line == null ? null : (string)line.GetType().GetProperty("Text").GetValue(line);
    }

    private static string InputPrompt(SerialTerminalControl terminal)
    {
        object line = InputLine(terminal);
        return line == null ? "" : (string)line.GetType().GetField("Prompt").GetValue(line);
    }

    // The editor of a command list stands in for the themed window of the application: it gives the lists that the steps queue (null: cancelled).
    private static readonly Queue<List<string>> _editorAnswers = new Queue<List<string>>();

    private static readonly List<string> _editorTitles = new List<string>();

    private static readonly List<string> _editorDescriptions = new List<string>();

    private static string _suggestionsBefore;

    private static DialogResult _messageBoxAnswer = DialogResult.No;

    private static readonly List<string> _messageBoxTexts = new List<string>();

    private static void InstallEditorStandIns()
    {
        DeviceTerminalControl.CommandListEditor = (title, description, commands) =>
        {
            _editorTitles.Add(title);
            _editorDescriptions.Add(description);
            return _editorAnswers.Dequeue();
        };
        ThemedMessageBox.ShowImpl = (text, caption, buttons, icon, owner) =>
        {
            _messageBoxTexts.Add(text);
            return _messageBoxAnswer;
        };
    }

    private static void InvokeEditCommandList(DeviceTerminalControl control, bool forThisDevice)
    {
        typeof(DeviceTerminalControl).GetMethod("EditCommandList", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null).Invoke(control, new object[] { forThisDevice });
    }

    private static void InvokePrivate(DeviceTerminalControl control, string name)
    {
        typeof(DeviceTerminalControl).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null).Invoke(control, null);
    }

    private static string Suggestions(SerialTerminalControl terminal)
    {
        return string.Join("|", terminal.CommandSuggestions);
    }

    private static DeviceLineInputStorage Storage()
    {
        return new DeviceLineInputStorage(GlobalSettings.DataDirectory);
    }

    private static string DeviceKey(int port)
    {
        return DeviceLineInputStorage.MakeTcpKey("127.0.0.1", port);
    }

    // What the device terminal keeps on disk for the next session.
    private static IReadOnlyList<string> SavedHistory(DeviceTerminalControl control)
    {
        // The accessor follows the move of the state into DeviceCommandLists.
        DeviceCommandLists lists = (DeviceCommandLists)typeof(DeviceTerminalControl).GetField("_commandLists", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);
        CommandHistory history = lists.SavedHistory;
        return history.Items;
    }

    private static string Suggestion(SerialTerminalControl terminal)
    {
        if (terminal == null)
        {
            return null;
        }
        // The accessor follows the move of the field into HistorySuggestionState (its Current is what the field held).
        object state = typeof(SerialTerminalControl).GetField("_suggestion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(terminal);
        return (string)state.GetType().GetProperty("Current", BindingFlags.Instance | BindingFlags.Public).GetValue(state, null);
    }

    private static string DocumentText(TerminalEmulatorControl terminal)
    {
        EditableTextDocument document = (EditableTextDocument)typeof(EditableTextBrowser).GetProperty("EditableDocument", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(terminal);
        StringBuilder text = new StringBuilder();
        for (int line = 0; line < document.LineCount; line++)
        {
            text.Append(document.GetLineText(line)).Append('\n');
        }
        return text.ToString();
    }
}
