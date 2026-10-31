using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

using Netconsole = VisualTTY.DeviceTerminal.DeviceTerminalSettings.ConnectionSettings.Netconsole;

// Runs the real COM/TCP terminal control as a netconsole receiver against a UDP socket on this machine that plays the
// device: its console output is shown (plain and extended messages), a line typed in the input box (or into the terminal
// in line mode) goes out as one datagram, and in character mode every key typed into the terminal goes out at once.
internal static class Program
{
    private sealed class Step
    {
        public string Name;

        public Action Act;

        public Func<bool> Done;

        public int TimeoutMs = 10000;
    }

    private static readonly List<string> Failures = new List<string>();

    private static int _passed;

    private static readonly MethodInfo OnKeyDownMethod = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo OnKeyPressMethod = typeof(Control).GetMethod("OnKeyPress", BindingFlags.Instance | BindingFlags.NonPublic);

    // Datagrams the device got, in order.
    private static readonly List<string> DeviceReceived = new List<string>();

    [STAThread]
    private static int Main()
    {
        // A hang must fail the job instead of blocking it.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(3));
            Console.WriteLine("FAIL: the netconsole terminal checks did not finish within 3 minutes");
            Environment.Exit(2);
        })
        {
            IsBackground = true
        }.Start();

        using UdpClient device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int devicePort = ((IPEndPoint)device.Client.LocalEndPoint).Port;
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = device.Receive(ref from);
                    lock (DeviceReceived)
                    {
                        DeviceReceived.Add(Encoding.UTF8.GetString(data));
                    }
                }
            }
            catch
            {
            }
        })
        {
            IsBackground = true
        }.Start();

        int port = FreeUdpPort();
        IPEndPoint receiver = new IPEndPoint(IPAddress.Loopback, port);
        Netconsole connection = new Netconsole
        {
            LocalAddress = "127.0.0.1",
            LocalPort = port,
            RemotePort = devicePort,
            ColorizeLevels = false,
            LocalEcho = true
        };
        DeviceTerminalSettings settings = new DeviceTerminalSettings
        {
            Connection = connection,
            LineEnding = DeviceTerminalSettings.LineEndingType.LF,
            TreatLFAsCRLF = true,
            AutoDetectPrompt = false
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

        void Send(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            device.Send(data, data.Length, receiver);
        }

        List<Step> steps = new List<Step>
        {
            new Step
            {
                Name = "the UDP port is opened and the tab says it listens",
                Act = () => control.ApplySettings(settings),
                Done = () => control.StatusText.Contains("listening")
            },
            new Step
            {
                Name = "line input shows the input box below the terminal",
                Done = () => control.NetconsoleLineInput && control.NetconsoleInputBox.Visible && !terminal.HasInputLine
            },
            new Step
            {
                Name = "an extended kernel message is shown like dmesg",
                Act = () => Send("6,1,1500000,-;hello from the kernel\n"),
                Done = () => DocumentText(terminal).Contains("[    1.500000] hello from the kernel")
            },
            new Step
            {
                Name = "plain console output split over datagrams is joined",
                Act = () =>
                {
                    Send("eth0: link ");
                    Send("up\n");
                },
                Done = () => DocumentText(terminal).Contains("eth0: link up")
            },
            new Step
            {
                Name = "a prompt-like message does not start a local input line",
                Act = () => Send("root@board:~# "),
                Done = () => DocumentText(terminal).Contains("root@board:~#") && !terminal.HasInputLine
            },
            new Step
            {
                Name = "the status names the sender",
                Done = () => control.StatusText.Contains("last message from 127.0.0.1")
            },
            new Step
            {
                Name = "a line from the input box goes out as one datagram",
                Act = () =>
                {
                    control.NetconsoleInputBox.Text = "printenv";
                    control.SendNetconsoleLine();
                },
                Done = () => Received().Contains("printenv\n") && control.NetconsoleInputBox.Text.Length == 0
            },
            new Step
            {
                Name = "the sent line is echoed in the terminal",
                Done = () => DocumentText(terminal).Contains("printenv")
            },
            new Step
            {
                Name = "keys typed into the terminal in line mode fill the input box and Enter sends the line",
                Act = () => Type(terminal, "ver\r"),
                Done = () => Received().Contains("ver\n") && control.NetconsoleInputBox.Text.Length == 0
            },
            new Step
            {
                Name = "Up in the input box brings the last line back",
                Act = () => PressInBox(control, Keys.Up),
                Done = () => control.NetconsoleInputBox.Text == "ver"
            },
            new Step
            {
                Name = "Up again goes further back in the history",
                Act = () => PressInBox(control, Keys.Up),
                Done = () => control.NetconsoleInputBox.Text == "printenv"
            },
            new Step
            {
                Name = "Escape clears the box",
                Act = () => PressInBox(control, Keys.Escape),
                Done = () => control.NetconsoleInputBox.Text.Length == 0
            },
            new Step
            {
                Name = "character input hides the box",
                Act = () => control.SetNetconsoleInputMode(Netconsole.InputMode.Character),
                Done = () => !control.NetconsoleInputBox.Visible && !control.NetconsoleLineInput
            },
            new Step
            {
                Name = "character input sends every key as a datagram of its own",
                Act = () =>
                {
                    lock (DeviceReceived)
                    {
                        DeviceReceived.Clear();
                    }
                    Type(terminal, "ls\r");
                },
                Done = () => Received() == "l|s|\n"
            },
            new Step
            {
                Name = "character input draws the typed keys (local echo)",
                Done = () => DocumentText(terminal).Contains("ls")
            },
            new Step
            {
                Name = "the device's output still arrives in character mode",
                Act = () => Send("4,2,2000000,-;w\\xc3\\xa4rning\n"),
                Done = () => DocumentText(terminal).Contains("[    2.000000] wärning")
            },
            new Step
            {
                Name = "back to line input",
                Act = () => control.SetNetconsoleInputMode(Netconsole.InputMode.Line),
                Done = () => control.NetconsoleInputBox.Visible && control.NetconsoleLineInput && !terminal.HasInputLine
            },
            new Step
            {
                Name = "a line typed in pieces still goes out as one datagram",
                Act = () =>
                {
                    lock (DeviceReceived)
                    {
                        DeviceReceived.Clear();
                    }
                    Type(terminal, "bo");
                    Type(terminal, "ot\r");
                },
                Done = () => Received() == "boot\n"
            },
            new Step
            {
                Name = "the tab is closed without hanging",
                Act = () => control.Disconnect(),
                Done = () => !control.StatusText.Contains("listening")
            }
        };
        ExecuteSteps(form, terminal, steps);
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("All " + _passed + " netconsole terminal checks passed.");
            return 0;
        }
        Console.WriteLine(Failures.Count + " netconsole terminal check(s) FAILED:");
        foreach (string failure in Failures)
        {
            Console.WriteLine("  " + failure);
        }
        return 1;
    }

    private static int FreeUdpPort()
    {
        using UdpClient probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
    }

    private static string Received()
    {
        lock (DeviceReceived)
        {
            return string.Join("|", DeviceReceived);
        }
    }

    // Runs the steps one after another on the UI thread of the form, failing a step that does not finish in time.
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

    private static DeviceTerminalControl _control;

    private static void Fail(string name, TerminalEmulatorControl terminal)
    {
        Failures.Add(name);
        Console.WriteLine("FAIL: " + name);
        Console.WriteLine("  status: " + _control?.StatusText);
        Console.WriteLine("  device got: " + Received().Replace("\n", "\\n"));
        Console.WriteLine("  screen:");
        foreach (string line in DocumentText(terminal).Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                Console.WriteLine("    " + line);
            }
        }
    }

    private static void Type(TerminalEmulatorControl terminal, string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\r')
            {
                // Enter comes as a key first (as in Windows); the terminal turns it into the line ending itself.
                KeyEventArgs down = new KeyEventArgs(Keys.Return);
                OnKeyDownMethod.Invoke(terminal, new object[] { down });
                if (down.SuppressKeyPress)
                {
                    continue;
                }
            }
            OnKeyPressMethod.Invoke(terminal, new object[] { new KeyPressEventArgs(ch) });
        }
    }

    private static void PressInBox(DeviceTerminalControl control, Keys key)
    {
        TextBox box = control.NetconsoleInputBox.TextBox;
        OnKeyDownMethod.Invoke(box, new object[] { new KeyEventArgs(key) });
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
