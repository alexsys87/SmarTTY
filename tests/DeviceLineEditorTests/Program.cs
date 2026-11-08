using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
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
        SerialTerminalControl terminal = (SerialTerminalControl)typeof(DeviceTerminalControl).GetField("_serialTerminalControl", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);

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

    private static string Suggestion(SerialTerminalControl terminal)
    {
        return terminal == null ? null : (string)typeof(SerialTerminalControl).GetField("_historySuggestion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(terminal);
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
