using SmarTTY.Controls;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Tests;

/// <summary>
/// End-to-end checks of the Smart Terminal (Controls/SshTerminalControl.cs) on the test servers that
/// tests/SshNetIntegrationTests provisions: OpenSSH (setup-sshd.sh, SMARTTY_SSH_TEST_*) and Dropbear with BusyBox ash,
/// as on OpenWRT (setup-dropbear.sh, SMARTTY_DROPBEAR_TEST_*). Keys are fed to the control's own key handlers, the
/// results are read from its text. Run under Mono with a virtual display, see the project file.
/// </summary>
internal static class Program
{
    private sealed class SessionContext : ITerminalSessionContext
    {
        private ShellCompletionAnalyzer _analyzer;

        public SshSession Session { get; set; }

        public string TitleWithUserName { get; set; }

        public VirtualTerminalSettings VTSettings { get; } = new VirtualTerminalSettings();

        public bool IsRoot => false;

        public ShellCompletionAnalyzer ProvideAnalyzer()
        {
            lock (this)
            {
                return _analyzer ??= new ShellCompletionAnalyzer(this, Session);
            }
        }
    }

    // Stands in for the WPF suggestion popup of the application.
    private sealed class TestPopup : ICompletionPopup
    {
        private CompletionItem[] _items = new CompletionItem[0];

        private string _filter = "";

        public bool IsOpen { get; private set; } = true;

        public event Action<CompletionItem, Keys> ItemSelected;

        public void Close()
        {
            IsOpen = false;
        }

        public void SetFilter(string text)
        {
            _filter = text ?? "";
        }

        public bool TryHandleKeyEvent(KeyEventArgs e)
        {
            if (!IsOpen || (e.KeyCode != Keys.Return && e.KeyCode != Keys.Tab))
            {
                return false;
            }
            CompletionItem item = GetSelectedItem(onlyIfPrefixMatch: false);
            if (item.Text == null)
            {
                return false;
            }
            IsOpen = false;
            ItemSelected?.Invoke(item, e.KeyCode);
            return true;
        }

        public void DisplayException(Exception exception)
        {
            Console.WriteLine("  (popup) " + exception.Message);
        }

        public void SetItemList(CompletionItem[] items)
        {
            _items = items ?? new CompletionItem[0];
            if (Environment.GetEnvironmentVariable("SMARTTY_TEST_TRACE") == "1")
            {
                Console.WriteLine("  (popup) " + _items.Length + " items, filter '" + _filter + "': " + string.Join(" ", _items.Take(8).Select((CompletionItem item) => item.Text)));
            }
        }

        public CompletionItem GetSelectedItem(bool onlyIfPrefixMatch)
        {
            return _items.FirstOrDefault((CompletionItem item) => item.Text != null && item.Text.StartsWith(_filter, StringComparison.Ordinal));
        }
    }

    private sealed class Step
    {
        public string Name;

        public Action Act;

        public Func<bool> Done;

        public int TimeoutMs = 20000;
    }

    private static readonly List<string> Failures = new List<string>();

    private static int _passed;

    private static readonly MethodInfo OnKeyDownMethod = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo OnKeyPressMethod = typeof(Control).GetMethod("OnKeyPress", BindingFlags.Instance | BindingFlags.NonPublic);

    [STAThread]
    private static int Main()
    {
        // A hang must fail the job instead of blocking it.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(8));
            Console.WriteLine("FAIL: the Smart Terminal checks did not finish within 8 minutes");
            Environment.Exit(2);
        })
        {
            IsBackground = true
        }.Start();

        bool any = false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_SSH_TEST_PORT")))
        {
            any = true;
            RunServer("OpenSSH", Value("SMARTTY_SSH_TEST_HOST", "127.0.0.1"), int.Parse(Value("SMARTTY_SSH_TEST_PORT", "2222")), Value("SMARTTY_SSH_TEST_USER", "smartty-test"), Value("SMARTTY_SSH_TEST_PASSWORD", "smartty-test-password"), bash: true);
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_DROPBEAR_TEST_PORT")))
        {
            any = true;
            RunServer("Dropbear + BusyBox ash", Value("SMARTTY_DROPBEAR_TEST_HOST", "127.0.0.1"), int.Parse(Value("SMARTTY_DROPBEAR_TEST_PORT", "2223")), Value("SMARTTY_DROPBEAR_TEST_USER", "smartty-ash"), Value("SMARTTY_DROPBEAR_TEST_PASSWORD", "smartty-ash-password"), bash: false);
        }
        if (!any)
        {
            Console.WriteLine("FAIL: no test server is configured; run tests/SshNetIntegrationTests/setup-sshd.sh and setup-dropbear.sh first.");
            return 1;
        }
        Console.WriteLine();
        if (Failures.Count != 0)
        {
            Console.WriteLine($"SmartTerminalIntegrationTests: {Failures.Count} of {_passed + Failures.Count} checks failed:");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
            return 1;
        }
        Console.WriteLine($"SmartTerminalIntegrationTests: all {_passed} checks passed.");
        return 0;
    }

    private static string Value(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static SshSession Connect(string host, int port, string user, string password)
    {
        SshSession session = new SshSession(new SshConnectionParameters
        {
            Host = host,
            DisplayHostName = host,
            Port = port,
            UserName = user,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
        if (!session.Authenticate(user, password))
        {
            session.Dispose();
            throw new InvalidOperationException("Could not authenticate as " + user);
        }
        return session;
    }

    private static void RunServer(string serverName, string host, int port, string user, string password, bool bash)
    {
        Console.WriteLine();
        Console.WriteLine("== Smart Terminal on " + serverName + " (" + host + ":" + port + " as " + user + ")");
        // Aliases, functions and hosts for completion. BusyBox ash has no .bashrc; its commands run without the
        // interactive bash wrapper, as they have to on OpenWRT where bash is usually missing.
        using (SshSession setup = Connect(host, port, user, password))
        {
            setup.RunCommand("mkdir -p ~/.ssh && printf 'Host smartty-test-host other-host\\n  HostName 127.0.0.1\\n' > ~/.ssh/config", out _);
            if (bash)
            {
                setup.RunCommand("printf \"alias smarttyalias='echo from-alias'\\nsmarttyfunction() { echo from-function; }\\n\" > ~/.bashrc", out _);
            }
        }

        // The first command of a fresh tab used to hang on Dropbear (a PTY channel set up while a helper command
        // ended, see Ssh/PtySetupGate.cs); a few fresh tabs check that.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using SshSession session = Connect(host, port, user, password);
            RunSteps(session, user, bash, new Func<SshTerminalControl, SessionContext, IEnumerable<Step>>[] { FirstCommandSteps }, serverName + ", fresh tab " + attempt);
        }

        using (SshSession session = Connect(host, port, user, password))
        {
            RunSteps(session, user, bash, new Func<SshTerminalControl, SessionContext, IEnumerable<Step>>[] { CommandSteps, HistorySearchSteps, CompletionSteps }, serverName);
        }

        // BusyBox ash without Unicode support (OpenWrt) edits in UTF-8 bytes: byte mode. bash: the normal mode.
        bool byteMode = !bash;
        if (byteMode)
        {
            using SshSession session = Connect(host, port, user, password);
            RunSteps(session, user, bash, new Func<SshTerminalControl, SessionContext, IEnumerable<Step>>[] { SmartByteModeSteps }, serverName + ", byte mode");
        }
        using (SshSession session = Connect(host, port, user, password))
        {
            RunClassicTerminal(session, byteMode, serverName + ", classic terminal" + (byteMode ? " in byte mode" : ""));
        }
    }

    private static void RunSteps(SshSession session, string user, bool bash, Func<SshTerminalControl, SessionContext, IEnumerable<Step>>[] scenarios, string label)
    {
        SessionContext context = new SessionContext
        {
            Session = session,
            TitleWithUserName = user + "@test"
        };
        context.VTSettings.InteractiveModeWrapper = bash;
        context.VTSettings.DefaultShell = bash ? "bash" : "sh";
        using Form form = new Form
        {
            Width = 1000,
            Height = 600
        };
        SshTerminalControl terminal = new SshTerminalControl(context)
        {
            Dock = DockStyle.Fill
        };
        terminal.CompletionPopupRequested += (sender, e) =>
        {
            e.Popup = new TestPopup();
        };
        form.Controls.Add(terminal);
        List<Step> steps = new List<Step>
        {
            new Step
            {
                Name = "the prompt is shown",
                Act = terminal.Start,
                Done = () => terminal.IsAtPrompt && DocumentText(terminal).Contains("Welcome to Smart Terminal.")
            }
        };
        foreach (Func<SshTerminalControl, SessionContext, IEnumerable<Step>> scenario in scenarios)
        {
            steps.AddRange(scenario(terminal, context));
        }
        ExecuteSteps(form, terminal, steps, label);
    }

    // Runs the steps one after another on the UI thread of the form (keys and output are handled there, as in the
    // application), failing a step that does not finish within its timeout.
    private static void ExecuteSteps(Form form, TerminalEmulatorControl terminal, List<Step> steps, string label)
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
                if (!started)
                {
                    started = true;
                    startedAt = DateTime.UtcNow;
                    step.Act?.Invoke();
                }
                if (step.Done == null || step.Done())
                {
                    Pass(label + ": " + step.Name);
                    index++;
                    started = false;
                }
                else if ((DateTime.UtcNow - startedAt).TotalMilliseconds > step.TimeoutMs)
                {
                    Fail(label + ": " + step.Name, terminal);
                    // The following steps build on this one.
                    index = steps.Count;
                }
            }
            catch (Exception exception)
            {
                Fail(label + ": " + step.Name + " (" + exception.GetType().Name + ": " + exception.Message + ")", terminal);
                index = steps.Count;
            }
        };
        form.Shown += (sender, e) => timer.Start();
        Application.Run(form);
        timer.Dispose();
    }

    // A program run from Smart Terminal reads a line through the kernel line editing (read): in byte mode the PTY
    // counts bytes (no IUTF8) and Backspace is sent once per byte, so it removes exactly one Cyrillic letter.
    private static IEnumerable<Step> SmartByteModeSteps(SshTerminalControl terminal, SessionContext context)
    {
        yield return new Step
        {
            Name = "a command started in byte mode waits for input",
            Act = () =>
            {
                terminal.ByteOrientedLineEditing = true;
                if (!terminal.Submit("read x; echo \"got:$x:\""))
                {
                    throw new InvalidOperationException("the terminal was not at the prompt");
                }
            },
            Done = () => !terminal.IsAtPrompt
        };
        yield return new Step
        {
            Name = "Backspace after 'при' removes one letter from what read receives",
            Act = () =>
            {
                Type(terminal, "AAпри");
                Key(terminal, Keys.Back);
                Type(terminal, "BB");
                Key(terminal, Keys.Return);
            },
            Done = () => terminal.IsAtPrompt && DocumentText(terminal).Contains("got:AAпрBB:")
        };
        yield return Check("the screen shows the edited line", () => CountLines(terminal, "AAпрBB") >= 1);
    }

    /// <summary>
    /// A classic terminal tab: the real TerminalEmulatorControl on an interactive login shell, wired as
    /// MainWindowController.CreateNewTerminal and SessionWrapper.AttachChannelToControl do it.
    /// </summary>
    private static void RunClassicTerminal(SshSession session, bool byteMode, string label)
    {
        using Form form = new Form
        {
            Width = 1000,
            Height = 600
        };
        TerminalEmulatorControl terminal = new TerminalEmulatorControl
        {
            Dock = DockStyle.Fill,
            ByteOrientedLineEditing = byteMode
        };
        form.Controls.Add(terminal);
        SshShellChannel channel = null;
        Utf8StreamDecoder decoder = new Utf8StreamDecoder();
        terminal.DataEntered += (object sender, byte[] input, TerminalEmulatorControl.InputKind kind) => channel?.Input.Write(input, 0, input.Length);
        string screenLine = null;
        DateTime catStarted = DateTime.MaxValue;
        List<Step> steps = new List<Step>
        {
            new Step
            {
                Name = "the shell prompt is shown",
                Act = () =>
                {
                    channel = session.OpenShellChannel(null, "xterm", ExtendedDataMode.Merge, utf8Tty: !byteMode);
                    channel.DataReceived += (object sender, byte[] data, int offset, int length, int value) =>
                    {
                        decoder.EscapeInvalidBytes = terminal.ByteOrientedLineEditing;
                        terminal.EnqueueOutput(decoder.Decode(data, offset, length));
                    };
                    channel.PollingEnabled = true;
                    channel.ResizePty(terminal.TerminalSize.Width, terminal.TerminalSize.Height);
                },
                Done = () => DocumentText(terminal).TrimEnd().EndsWith("$")
            },
            new Step
            {
                // bash edits in characters only with a UTF-8 locale; the runner's default must not decide the check.
                Name = "the locale is UTF-8 (normal mode) or none (byte mode, as on OpenWrt)",
                Act = () => Type(terminal, byteMode ? "unset LANG LC_ALL LC_CTYPE\r" : "export LANG=C.UTF-8 LC_ALL=C.UTF-8\r"),
                Done = () => DocumentText(terminal).TrimEnd().EndsWith("$") && DocumentText(terminal).Contains(byteMode ? "LC_CTYPE\n" : "C.UTF-8\n")
            },
            new Step
            {
                Name = "Backspace after 'при' at the shell prompt removes one letter on the screen",
                Act = () =>
                {
                    Type(terminal, "echo AAпри");
                    Key(terminal, Keys.Back);
                },
                Done = () => (screenLine = LastLine(terminal)).EndsWith("echo AAпр")
            },
            new Step
            {
                Name = "and one letter from the command line",
                Act = () =>
                {
                    Type(terminal, "BB");
                    Key(terminal, Keys.Return);
                },
                Done = () => DocumentText(terminal).Contains("\nAAпрBB\n")
            },
            new Step
            {
                Name = "Left, Left, Backspace and a letter typed in the middle of a word",
                Act = () =>
                {
                    Type(terminal, "echo CCпривет");
                    Key(terminal, Keys.Left);
                    Key(terminal, Keys.Left);
                    Key(terminal, Keys.Back);
                    Type(terminal, "Ж");
                },
                Done = () => (screenLine = LastLine(terminal)).EndsWith("echo CCприЖет")
            },
            new Step
            {
                Name = "the edited word reaches the shell",
                Act = () => Key(terminal, Keys.Return),
                Done = () => DocumentText(terminal).Contains("\nCCприЖет\n")
            },
            new Step
            {
                Name = "Backspace held over mixed text faster than the echo comes back",
                Act = () =>
                {
                    Type(terminal, "echo xyz приве");
                    for (int press = 0; press < 6; press++)
                    {
                        Key(terminal, Keys.Back);
                    }
                    Type(terminal, "Q");
                    Key(terminal, Keys.Return);
                },
                Done = () => CountLines(terminal, "xyzQ") >= 1
            },
            new Step
            {
                // Keys typed before cat has put the tty back into line mode would reach it as raw bytes.
                Name = "cat starts",
                Act = () =>
                {
                    Type(terminal, "cat\r");
                    catStarted = DateTime.UtcNow;
                },
                Done = () => DocumentText(terminal).Contains("cat\n") && (DateTime.UtcNow - catStarted).TotalMilliseconds > 500
            },
            new Step
            {
                Name = "Backspace in cat (the kernel line editing) removes one letter",
                Act = () =>
                {
                    Type(terminal, "DDпри");
                    Key(terminal, Keys.Back);
                    Type(terminal, "\r\u0004");
                },
                // The typed line and what cat prints back ("\b \b" may leave a blank at the end of the first).
                Done = () => CountLines(terminal, "DDпр") >= 2
            },
            new Step
            {
                Name = "exit",
                Act = () => Type(terminal, "exit\r"),
                Done = () => channel.TryWaitClosed()
            }
        };
        ExecuteSteps(form, terminal, steps, label);
        if (screenLine != null && Environment.GetEnvironmentVariable("SMARTTY_TEST_TRACE") == "1")
        {
            Console.WriteLine("  last screen line: " + screenLine);
        }
        channel?.Dispose();
    }

    private static int CountLines(TerminalEmulatorControl terminal, string text)
    {
        return DocumentText(terminal).Split('\n').Count((string line) => line.TrimEnd() == text);
    }

    private static string LastLine(TerminalEmulatorControl terminal)
    {
        string[] lines = DocumentText(terminal).TrimEnd('\n', ' ').Split('\n');
        return lines[lines.Length - 1].TrimEnd();
    }

    private static IEnumerable<Step> FirstCommandSteps(SshTerminalControl terminal, SessionContext context)
    {
        yield return Run(terminal, "echo first-command", "first-command", "the first command of a tab runs");
    }

    private static IEnumerable<Step> CommandSteps(SshTerminalControl terminal, SessionContext context)
    {
        yield return Run(terminal, "echo hello-smartty", "hello-smartty", "a command runs and the prompt comes back");
        yield return Run(terminal, "cd /tmp", null, "cd changes the directory");
        yield return Check("the prompt and the title show the new directory", () => terminal.Title.EndsWith(":/tmp") && InputPrompt(terminal).Contains(":/tmp"));
        yield return Run(terminal, "pwd", "/tmp", "the next command runs in the new directory");
        yield return Run(terminal, "cd -", null, "cd - goes back");
        yield return Check("cd - returned to the previous directory", () => !terminal.CurrentDirectory.EndsWith("/tmp") && terminal.CurrentDirectory.StartsWith("/"));
        yield return Run(terminal, "export SMARTTY_UNIQUE_VAR=value-42", null, "export is kept");
        yield return Run(terminal, "echo \"[$SMARTTY_UNIQUE_VAR]\"", "[value-42]", "an exported variable reaches the next command");
        yield return Run(terminal, "sh -c 'exit 3'", "[exit code 3]", "a failing command shows its exit code");
        yield return new Step
        {
            Name = "the exit code can be turned off",
            Act = () =>
            {
                context.VTSettings.ShowExitCodes = false;
                terminal.Submit("sh -c 'exit 4'; echo exit-code-off");
            },
            Done = () => terminal.IsAtPrompt && DocumentText(terminal).Contains("exit-code-off")
        };
        yield return Check("no exit code is shown when it is turned off", () => !DocumentText(terminal).Contains("[exit code 4]") && !DocumentText(terminal).Contains("[exit code 0]"));
        yield return new Step
        {
            Name = "exit codes are shown again",
            Act = () => context.VTSettings.ShowExitCodes = true
        };
    }

    private static IEnumerable<Step> HistorySearchSteps(SshTerminalControl terminal, SessionContext context)
    {
        yield return new Step
        {
            Name = "Ctrl+R starts the history search",
            Act = () =>
            {
                Key(terminal, Keys.Control | Keys.R);
                Type(terminal, "hello-sm");
            },
            Done = () => InputPrompt(terminal) == "(reverse-i-search)`hello-sm': " && InputText(terminal) == "echo hello-smartty"
        };
        yield return new Step
        {
            Name = "a query without a match marks the search as failed",
            Act = () => Type(terminal, "zzz"),
            Done = () => InputPrompt(terminal).StartsWith("(failed reverse-i-search)") && InputText(terminal) == "echo hello-smartty"
        };
        yield return new Step
        {
            Name = "Backspace shortens the query",
            Act = () =>
            {
                Key(terminal, Keys.Back);
                Key(terminal, Keys.Back);
                Key(terminal, Keys.Back);
            },
            Done = () => InputPrompt(terminal) == "(reverse-i-search)`hello-sm': "
        };
        yield return new Step
        {
            Name = "Escape cancels the search and restores the line",
            Act = () => Key(terminal, Keys.Escape),
            Done = () => !InputPrompt(terminal).Contains("reverse-i-search") && InputText(terminal) == ""
        };
        yield return new Step
        {
            Name = "Enter runs the found command",
            Act = () =>
            {
                Key(terminal, Keys.Control | Keys.R);
                Type(terminal, "UNIQUE_VAR]");
                Key(terminal, Keys.Return);
            },
            Done = () => terminal.IsAtPrompt && CountOccurrences(DocumentText(terminal), "[value-42]") >= 2
        };
        yield return new Step
        {
            Name = "Right arrow keeps the found command for editing",
            Act = () =>
            {
                Key(terminal, Keys.Control | Keys.R);
                Type(terminal, "hello-smartty");
                Key(terminal, Keys.Right);
            },
            Done = () => !InputPrompt(terminal).Contains("reverse-i-search") && InputText(terminal) == "echo hello-smartty"
        };
        yield return new Step
        {
            Name = "the line is cleared",
            Act = () => Key(terminal, Keys.Control | Keys.C),
            Done = () => InputText(terminal) == ""
        };
    }

    private static IEnumerable<Step> CompletionSteps(SshTerminalControl terminal, SessionContext context)
    {
        yield return Complete(terminal, "echo $SMARTTY_UNIQ", "echo $SMARTTY_UNIQUE_VAR", "Tab completes a variable");
        yield return Complete(terminal, "echo ${SMARTTY_UNIQ", "echo ${SMARTTY_UNIQUE_VAR}", "Tab completes a braced variable");
        yield return Complete(terminal, "ssh smartty-test-h", "ssh smartty-test-host", "Tab completes a host from ~/.ssh/config");
        yield return Complete(terminal, "ssh me@smartty-test-h", "ssh me@smartty-test-host", "Tab completes a host after user@");
        if (context.VTSettings.InteractiveModeWrapper)
        {
            yield return Complete(terminal, "smarttyal", "smarttyalias", "Tab completes an alias of .bashrc");
            yield return Complete(terminal, "smarttyfunc", "smarttyfunction", "Tab completes a function of .bashrc");
            yield return Run(terminal, "smarttyalias", "from-alias", "the alias runs in the interactive wrapper");
        }
    }

    private static Step Complete(SshTerminalControl terminal, string typed, string expected, string name)
    {
        return new Step
        {
            Name = name,
            Act = () =>
            {
                Key(terminal, Keys.Control | Keys.C);
                Type(terminal, typed);
                // As a key: an open suggestion list (opened by the space after "ssh") takes Tab in OnKeyDown.
                Key(terminal, Keys.Tab);
            },
            Done = () => InputText(terminal) == expected
        };
    }

    private static Step Run(SshTerminalControl terminal, string command, string expectedOutput, string name)
    {
        int before = 0;
        return new Step
        {
            Name = name,
            Act = () =>
            {
                before = expectedOutput == null ? 0 : CountOccurrences(DocumentText(terminal), expectedOutput);
                if (!terminal.Submit(command))
                {
                    throw new InvalidOperationException("the terminal was not at the prompt");
                }
            },
            Done = () => terminal.IsAtPrompt && (expectedOutput == null || CountOccurrences(DocumentText(terminal), expectedOutput) > before)
        };
    }

    private static Step Check(string name, Func<bool> condition)
    {
        return new Step
        {
            Name = name,
            Done = condition,
            TimeoutMs = 3000
        };
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
            case Keys.Tab:
                ch = '\t';
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
            OnKeyPressMethod.Invoke(terminal, new object[] { new KeyPressEventArgs(ch) });
        }
    }

    private static object InputLine(SshTerminalControl terminal)
    {
        return typeof(SshTerminalControl).GetField("_inputLine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(terminal);
    }

    private static string InputText(SshTerminalControl terminal)
    {
        object line = InputLine(terminal);
        return line == null ? null : (string)line.GetType().GetProperty("Text").GetValue(line);
    }

    private static string InputPrompt(SshTerminalControl terminal)
    {
        object line = InputLine(terminal);
        return line == null ? "" : (string)line.GetType().GetField("Prompt").GetValue(line);
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

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index != -1; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static void Pass(string name)
    {
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Fail(string name, TerminalEmulatorControl terminal)
    {
        Failures.Add(name);
        Console.WriteLine("FAIL: " + name);
        if (terminal is SshTerminalControl smartTerminal)
        {
            Console.WriteLine("  input line: '" + InputPrompt(smartTerminal) + "' + '" + InputText(smartTerminal) + "'");
        }
        string[] lines = DocumentText(terminal).Split('\n');
        Console.WriteLine("  last lines of the terminal:");
        foreach (string line in lines.Skip(Math.Max(0, lines.Length - 15)))
        {
            Console.WriteLine("  | " + line);
        }
    }
}
