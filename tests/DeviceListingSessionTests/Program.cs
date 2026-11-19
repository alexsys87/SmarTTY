using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualTTY.DeviceTerminal;

// Checks of the listing of a device directory for the path completion (VisualTTY/DeviceTerminal/DeviceListingSession.cs): the start, the answer taken out of the
// received text, the timeouts, the request that waits for the one that runs, and the input typed meanwhile. The panel is a fake with a clock of its own.
internal static class Program
{
    private const string Prompt = "root@board:~# ";

    private sealed class FakeHost : IDeviceListingHost
    {
        public bool CanList = true;
        public object Connection = new object();
        public int TickCount;
        public bool SendResult = true;
        public readonly List<string> Sent = new List<string>();
        public readonly List<string> Log = new List<string>();
        public int TimerStarts;
        public int TimerStops;
        public readonly List<string> Unclaimed = new List<string>();

        bool IDeviceListingHost.CanList => CanList;

        object IDeviceListingHost.Connection => Connection;

        int IDeviceListingHost.TickCount => TickCount;

        byte[] IDeviceListingHost.EncodeCommandLine(string command) => Encoding.UTF8.GetBytes(command + "\r");

        bool IDeviceListingHost.Send(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes);
            Sent.Add(text);
            Log.Add("send " + (text.Contains(DeviceDirectoryListing.MarkerHead) ? "listing" : text));
            return SendResult;
        }

        void IDeviceListingHost.StartTimer()
        {
            TimerStarts++;
        }

        void IDeviceListingHost.StopTimer()
        {
            TimerStops++;
            Log.Add("stop timer");
        }

        void IDeviceListingHost.ShowUnclaimedText(string text)
        {
            Unclaimed.Add(text);
            Log.Add("unclaimed");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static string Names(IReadOnlyList<DeviceDirectoryEntry> entries)
    {
        return entries == null ? "none" : string.Join(",", entries.Select((DeviceDirectoryEntry entry) => entry.Name + (entry.IsDirectory ? "/" : "")));
    }

    // What a shell prints for the listing with this token: the echo, the two marker lines around the names, the prompt.
    private static string Answer(string token, string names, bool withPrompt = true)
    {
        return " printf ...\r\n" + DeviceDirectoryListing.BeginMarker(token) + "\r\n" + names + DeviceDirectoryListing.EndMarker(token) + "\r\n" + (withPrompt ? Prompt : "");
    }

    private static DeviceListingSession Make(FakeHost host)
    {
        return new DeviceListingSession(host);
    }

    private static void StartChecks()
    {
        FakeHost host = new FakeHost { CanList = false };
        DeviceListingSession session = Make(host);
        Equal(session.Start("/etc", entries => throw new Exception("not called")), false, "no listing when the panel says it cannot list now");
        Check(host.Sent.Count == 0 && host.TimerStarts == 0 && !session.IsListing, "nothing is sent, no timer, no listing");

        host.CanList = true;
        host.SendResult = false;
        Equal(session.Start("/etc", entries => throw new Exception("not called")), false, "no listing when the command could not be sent");
        Check(!session.IsListing && host.TimerStarts == 0, "no listing is left and no timer runs");
        Check(host.Sent[0].Contains("'B1'") && host.Sent[0].EndsWith("\r", StringComparison.Ordinal), "the first command has the token 1 and ends the line");

        host.SendResult = true;
        Equal(session.Start("/etc", entries => { }), true, "a listing starts");
        Check(host.Sent[1].Contains("'B2'") && host.Sent[1].Contains("cd -- '/etc'"), "the token counts on, also after a command that was not sent");
        Check(session.IsListing && host.TimerStarts == 1, "it runs and the timer is on");
    }

    private static void AnswerChecks()
    {
        FakeHost host = new FakeHost();
        DeviceListingSession session = Make(host);
        Equal(session.Filter("plain text"), "plain text", "without a listing the text is shown as it is");
        string result = null;
        session.Start("/etc", entries => result = Names(entries));
        string answer = Answer("1", "init.d/\r\nhosts\r\n");
        string shown = session.Filter(answer.Substring(0, 30));
        Equal(shown, "", "the echo is not shown");
        Check(session.IsListing && result == null, "the listing goes on");
        Equal(session.Filter(answer.Substring(30)), "", "nor is the rest of the answer");
        Equal(result, "init.d/,hosts", "the names, in order, directories marked");
        Check(!session.IsListing, "the listing is over after the prompt");
        Equal(host.TimerStops, 1, "the timer is stopped");
        Equal(session.Filter("after"), "after", "text after it is shown again");

        result = null;
        session.Start("/etc", entries => result = Names(entries));
        Equal(session.Filter(Answer("2", "a\r\n") + "tail"), "tail", "what follows the prompt in the same chunk is shown");
        Equal(result, "a", "and the names are there");

        result = null;
        session.Start("/etc", entries => result = Names(entries));
        session.Filter(Answer("3", ""));
        Equal(result, "", "an empty directory gives an empty list, not none");
    }

    private static void TimeoutChecks()
    {
        FakeHost host = new FakeHost();
        DeviceListingSession session = Make(host);
        string result = "unset";
        session.Start("/silent", entries => result = Names(entries));
        session.Filter(" printf the echo only\r\n");
        host.TickCount = DeviceListingSession.TimeoutMs - 1;
        session.CheckTimeout();
        Check(session.IsListing && result == "unset", "a device is waited for until the timeout");
        host.TickCount = DeviceListingSession.TimeoutMs;
        session.CheckTimeout();
        Equal(result, "none", "the listing says that nothing was listed");
        Check(!session.IsListing && host.Unclaimed.Count == 1 && host.Unclaimed[0] == " printf the echo only\r\n", "what the device printed is shown as it came");

        // The system clock wraps around: the time is the difference.
        host = new FakeHost { TickCount = int.MaxValue - 100 };
        session = Make(host);
        result = "unset";
        session.Start("/silent", entries => result = Names(entries));
        host.TickCount = unchecked(int.MaxValue - 100 + DeviceListingSession.TimeoutMs);
        session.CheckTimeout();
        Equal(result, "none", "the timeout is found across the wrap of the clock");

        // The complete answer is there but the prompt is not.
        host = new FakeHost();
        session = Make(host);
        result = "unset";
        session.Start("/noprompt", entries => result = Names(entries));
        // The moment of the end marker is kept with its lowest bit set (zero means "not yet"), so an odd tick is used here.
        host.TickCount = 1001;
        session.Filter(Answer("1", "a.txt\r\n", withPrompt: false));
        host.TickCount = 1001 + DeviceListingSession.PromptWaitMs - 1;
        session.CheckTimeout();
        Check(session.IsListing && result == "unset", "a late prompt is waited for");
        host.TickCount = 1001 + DeviceListingSession.PromptWaitMs;
        session.CheckTimeout();
        Equal(result, "a.txt", "then the names are given");
        Check(host.Unclaimed.Count == 0, "and no text is shown again, the answer was complete");

        // The clock may show 0 when the end marker arrives: that must not look like "not yet", or the wait would start again with the next text.
        host = new FakeHost();
        session = Make(host);
        result = "unset";
        session.Start("/noprompt", entries => result = Names(entries));
        host.TickCount = 0;
        session.Filter(Answer("1", "a.txt\r\n", withPrompt: false));
        host.TickCount = 300;
        session.Filter("some more text");
        host.TickCount = 500;
        session.CheckTimeout();
        Check(session.IsListing && result == "unset", "the wait counts from the end marker, also when the clock showed 0 then");
        host.TickCount = 501;
        session.CheckTimeout();
        Equal(result, "a.txt", "and ends there, not later because of text that came in between");

        // The connection is gone.
        host = new FakeHost();
        session = Make(host);
        result = "unset";
        session.Start("/x", entries => result = Names(entries));
        host.Connection = new object();
        session.CheckTimeout();
        Equal(result, "none", "a new connection ends a listing that has not answered");
        host = new FakeHost();
        session = Make(host);
        result = "unset";
        session.Start("/x", entries => result = Names(entries));
        session.Filter(Answer("1", "a\r\n", withPrompt: false));
        host.Connection = null;
        session.CheckTimeout();
        Equal(result, "a", "and one that has answered gives its names at once");

        // No listing: the timer stops itself.
        host = new FakeHost();
        session = Make(host);
        session.CheckTimeout();
        Equal(host.TimerStops, 1, "the timer is stopped when there is no listing");
    }

    private static void QueueChecks()
    {
        FakeHost host = new FakeHost();
        DeviceListingSession session = Make(host);
        List<string> results = new List<string>();
        session.Start("/a", entries => results.Add("a=" + Names(entries)));
        Equal(session.Start("/b", entries => results.Add("b=" + Names(entries))), true, "a request made meanwhile is accepted");
        Equal(host.Sent.Count, 1, "but nothing is sent for it yet");
        Equal(session.Start("/c", entries => results.Add("c=" + Names(entries))), true, "so is a newer one");
        Equal(string.Join("|", results), "b=none", "which takes the place of the older one, told that nothing was listed");
        session.Filter(Answer("1", "x\r\n"));
        Equal(string.Join("|", results), "b=none|a=x", "the first listing is done first");
        Check(host.Sent.Count == 2 && host.Sent[1].Contains("cd -- '/c'") && host.Sent[1].Contains("'B2'"), "then the newest request is sent");
        session.Filter(Answer("2", "y/\r\n"));
        Equal(string.Join("|", results), "b=none|a=x|c=y/", "and answered");

        // The waiting request cannot be started when its turn comes.
        host = new FakeHost();
        session = Make(host);
        results.Clear();
        session.Start("/a", entries => results.Add("a=" + Names(entries)));
        session.Start("/b", entries => results.Add("b=" + Names(entries)));
        host.CanList = false;
        session.Filter(Answer("1", "x\r\n"));
        Equal(string.Join("|", results), "a=x|b=none", "it is told that nothing was listed");
        Check(!session.IsListing && host.Sent.Count == 1, "no listing runs and nothing more was sent");
    }

    private static void HeldInputChecks()
    {
        FakeHost host = new FakeHost();
        DeviceListingSession session = Make(host);
        Equal(session.HoldInput(new byte[] { 1 }), false, "input is not held without a listing");
        session.Start("/a", entries => host.Log.Add("done"));
        byte[] typed = Encoding.UTF8.GetBytes("ls\r");
        Equal(session.HoldInput(typed), true, "input is held while a listing runs");
        typed[0] = (byte)'X';
        session.HoldInput(Encoding.UTF8.GetBytes("pwd\r"));
        host.Log.Clear();
        session.Filter(Answer("1", "x\r\n"));
        Equal(string.Join("|", host.Log), "stop timer|send ls\r|send pwd\r|done", "the timer stops, what was typed goes out in order, then the listing is told");
        Equal(host.Sent[1], "ls\r", "the held bytes are a copy");
        session.Filter("");
        Equal(host.Sent.Count, 3, "they go out once");

        // Without a connection they are dropped.
        host = new FakeHost();
        session = Make(host);
        session.Start("/a", entries => { });
        session.HoldInput(Encoding.UTF8.GetBytes("ls\r"));
        host.Connection = null;
        session.CheckTimeout();
        Equal(host.Sent.Count, 1, "nothing is sent when the connection is gone");
        session.Start("/a", entries => { });
        Check(host.Sent.Count == 2, "and what was held is not sent later");

        // The order when the device did not answer: its text first, then the input, then the listing is told.
        host = new FakeHost();
        session = Make(host);
        session.Start("/a", entries => host.Log.Add("done"));
        session.Filter("echo\r\n");
        session.HoldInput(Encoding.UTF8.GetBytes("ls\r"));
        host.Log.Clear();
        host.TickCount = DeviceListingSession.TimeoutMs;
        session.CheckTimeout();
        Equal(string.Join("|", host.Log), "stop timer|unclaimed|send ls\r|done", "the text of the device is shown before the input goes out");
    }

    private static int Main()
    {
        try
        {
            StartChecks();
            AnswerChecks();
            TimeoutChecks();
            QueueChecks();
            HeldInputChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}
