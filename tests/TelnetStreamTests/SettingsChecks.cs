using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using VisualTTY.DeviceTerminal;

// Saved connections: a telnet connection must survive a save/load round trip as a telnet connection, files written
// before telnet existed must still load as raw TCP, and the two kinds must never share a file name.
internal static class SettingsChecks
{
    private const string OldRawTcpFile = "<?xml version=\"1.0\"?>\r\n" +
        "<RawConnectionSettings xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
        "<Settings><Connection xsi:type=\"com.sysprogs.terminal.connection.tcp\"><Host>192.168.0.5</Host><Port>5000</Port></Connection>" +
        "<LastConnectionTime>0</LastConnectionTime></Settings></RawConnectionSettings>";

    private static RawConnectionSettings RoundTrip(RawConnectionSettings source, out string xml)
    {
        XmlSerializer serializer = new XmlSerializer(typeof(RawConnectionSettings));
        using StringWriter writer = new StringWriter();
        serializer.Serialize(writer, source);
        xml = writer.ToString();
        using StringReader reader = new StringReader(xml);
        return (RawConnectionSettings)serializer.Deserialize(reader);
    }

    public static void Run(Action<bool, string> check)
    {
        DeviceTerminalSettings.ConnectionSettings.Telnet telnet = new DeviceTerminalSettings.ConnectionSettings.Telnet { Host = "10.0.0.7", Port = 2323 };
        RawConnectionSettings saved = new RawConnectionSettings
        {
            Settings = new DeviceTerminalSettings { Connection = telnet, LineEnding = DeviceTerminalSettings.LineEndingType.CRLF }
        };

        RawConnectionSettings loaded = RoundTrip(saved, out string xml);
        check(xml.Contains("com.sysprogs.terminal.connection.telnet"), "telnet connection is saved with its own type name");
        check(loaded.Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet, "telnet connection is loaded back as telnet");
        DeviceTerminalSettings.ConnectionSettings.Tcp tcp = (DeviceTerminalSettings.ConnectionSettings.Tcp)loaded.Settings.Connection;
        check(tcp.Host == "10.0.0.7" && tcp.Port == 2323, "host and port survive the round trip");
        check(loaded.Settings.LineEnding == DeviceTerminalSettings.LineEndingType.CRLF, "the line ending survives the round trip");
        check(loaded.Settings.Connection.IsValid(), "a loaded telnet connection is valid");

        RawConnectionSettings rawTcp = new RawConnectionSettings
        {
            Settings = new DeviceTerminalSettings { Connection = new DeviceTerminalSettings.ConnectionSettings.Tcp { Host = "10.0.0.7", Port = 2323 } }
        };
        RawConnectionSettings rawLoaded = RoundTrip(rawTcp, out string rawXml);
        check(!(rawLoaded.Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet), "a raw TCP connection is not turned into telnet");
        check(!rawXml.Contains("telnet"), "a raw TCP file does not mention telnet");

        XmlSerializer serializer = new XmlSerializer(typeof(RawConnectionSettings));
        using (StringReader reader = new StringReader(OldRawTcpFile))
        {
            RawConnectionSettings old = (RawConnectionSettings)serializer.Deserialize(reader);
            check(old.Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp && !(old.Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet),
                "a file written before telnet existed still loads as raw TCP");
            check(((DeviceTerminalSettings.ConnectionSettings.Tcp)old.Settings.Connection).Port == 5000, "...with its port");
            check(old.Settings.DisplayMode == DeviceTerminalSettings.CharacterDisplayMode.ASCII && old.Settings.HexSettings.ShowTextView,
                "display mode: a file written before it existed opens as text, with the text pane ready for HEX");
        }

        check(telnet.FileNameBase != rawTcp.Settings.Connection.FileNameBase, "telnet and raw TCP to the same host:port do not share a file");
        check(telnet.FileNameBase == "telnet_10.0.0.7_2323", "telnet file name base");

        // display mode: the connection dialog writes it and the toolbar reads it back from the profile
        check(new DeviceTerminalSettings().DisplayMode == DeviceTerminalSettings.CharacterDisplayMode.ASCII
            && new DeviceTerminalSettings().HexSettings != null
            && new DeviceTerminalSettings().HexSettings.ShowTextView, "display mode: a new profile starts as text with the text pane enabled");
        DeviceTerminalSettings bothSettings = new DeviceTerminalSettings
        {
            Connection = new DeviceTerminalSettings.ConnectionSettings.Tcp { Host = "10.0.0.8", Port = 5001 },
            DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.Both
        };
        bothSettings.HexSettings.ShowTextView = true;
        bothSettings.HexSettings.MaximumBytesPerLine = 8;
        RawConnectionSettings bothLoaded = RoundTrip(new RawConnectionSettings { Settings = bothSettings }, out string bothXml);
        check(bothXml.Contains("<DisplayMode>Both</DisplayMode>"), "display mode: saved by name, not by an ordinal");
        check(bothLoaded.Settings.DisplayMode == DeviceTerminalSettings.CharacterDisplayMode.Both, "display mode: the combined view survives the round trip");
        check(bothLoaded.Settings.HexSettings != null && bothLoaded.Settings.HexSettings.MaximumBytesPerLine == 8,
            "display mode: the HEX options survive the round trip");
        DeviceTerminalSettings bothCopy = bothSettings.Clone();
        check(bothCopy.DisplayMode == DeviceTerminalSettings.CharacterDisplayMode.Both && bothCopy.HexSettings.ShowTextView
            && bothCopy.HexSettings.MaximumBytesPerLine == 8, "display mode: Clone() keeps the mode and the HEX options");
        check(DeviceTerminalSettings.CharacterDisplayModeHelper.Resolve(DeviceTerminalSettings.CharacterDisplayMode.HEX, bothSettings.HexSettings)
                == DeviceTerminalSettings.CharacterDisplayMode.Both,
            "display mode: an old HEX profile with the text pane enabled is shown as the combined view");
        bothSettings.HexSettings.ShowTextView = false;
        check(DeviceTerminalSettings.CharacterDisplayModeHelper.Resolve(DeviceTerminalSettings.CharacterDisplayMode.HEX, bothSettings.HexSettings)
                == DeviceTerminalSettings.CharacterDisplayMode.HEX,
            "display mode: HEX without the text pane stays a HEX-only view");
        check(DeviceTerminalSettings.CharacterDisplayModeHelper.Resolve(DeviceTerminalSettings.CharacterDisplayMode.ASCII, bothSettings.HexSettings)
                == DeviceTerminalSettings.CharacterDisplayMode.ASCII,
            "display mode: the mode chosen in the profile wins over the HEX options");

        // telnet options: defaults, round trip, files written before they existed
        check(telnet.TerminalType == "xterm" && telnet.LocalEcho == DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Auto
            && !telnet.ActiveNegotiation && telnet.KeepAliveSeconds == 0, "telnet options: defaults");
        check(((DeviceTerminalSettings.ConnectionSettings.Telnet)loaded.Settings.Connection).LocalEcho == DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Auto,
            "telnet options: a file saved before they existed gets the defaults");
        DeviceTerminalSettings.ConnectionSettings.Telnet custom = new DeviceTerminalSettings.ConnectionSettings.Telnet
        {
            Host = "sw1",
            Port = 23,
            TerminalType = "vt100",
            LocalEcho = DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Never,
            ActiveNegotiation = true,
            KeepAliveSeconds = 45,
            LineMode = true
        };
        RawConnectionSettings customLoaded = RoundTrip(new RawConnectionSettings { Settings = new DeviceTerminalSettings { Connection = custom } }, out string customXml);
        DeviceTerminalSettings.ConnectionSettings.Telnet back = (DeviceTerminalSettings.ConnectionSettings.Telnet)customLoaded.Settings.Connection;
        check(back.TerminalType == "vt100" && back.LocalEcho == DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Never && back.ActiveNegotiation && back.KeepAliveSeconds == 45,
            "telnet options: survive the round trip");
        DeviceTerminalSettings.ConnectionSettings.Telnet customCopy = (DeviceTerminalSettings.ConnectionSettings.Telnet)custom.Clone();
        check(customCopy.TerminalType == "vt100" && customCopy.KeepAliveSeconds == 45 && customCopy.ActiveNegotiation && customCopy.LineMode, "telnet options: Clone() keeps them");
        check(back.LineMode && !telnet.LineMode && !((DeviceTerminalSettings.ConnectionSettings.Telnet)loaded.Settings.Connection).LineMode,
            "linemode setting: off by default, survives the round trip, a file saved before it existed has it off");

        // auto login fields
        check(!telnet.HasAutoLogin, "auto login: none by default");
        DeviceTerminalSettings.ConnectionSettings.Telnet withLogin = new DeviceTerminalSettings.ConnectionSettings.Telnet
        {
            Host = "sw2",
            Port = 23,
            AutoLoginUserName = "admin",
            EncryptedSavedPassword = new byte[] { 1, 2, 3, 250 },
            LoginPrompts = "id>",
            PasswordPrompts = "pin>"
        };
        check(withLogin.HasAutoLogin, "auto login: HasAutoLogin when a user name is set");
        RawConnectionSettings withLoginLoaded = RoundTrip(new RawConnectionSettings { Settings = new DeviceTerminalSettings { Connection = withLogin } }, out string withLoginXml);
        DeviceTerminalSettings.ConnectionSettings.Telnet loginBack = (DeviceTerminalSettings.ConnectionSettings.Telnet)withLoginLoaded.Settings.Connection;
        check(loginBack.AutoLoginUserName == "admin" && loginBack.LoginPrompts == "id>" && loginBack.PasswordPrompts == "pin>"
            && loginBack.EncryptedSavedPassword != null && loginBack.EncryptedSavedPassword.Length == 4 && loginBack.EncryptedSavedPassword[3] == 250,
            "auto login: fields survive the round trip");
        check(withLoginXml.Contains("<EncryptedSavedPassword>"), "auto login: the password is stored in an element named like the SSH one (the archive export strips it by that name)");
        check(new DeviceTerminalSettings.ConnectionSettings.Telnet { Host = "h", Port = 1, EncryptedSavedPassword = new byte[] { 1 } }.HasAutoLogin, "auto login: HasAutoLogin when only a password is saved");
        check(!((DeviceTerminalSettings.ConnectionSettings.Telnet)loaded.Settings.Connection).HasAutoLogin && !xml.Contains("EncryptedSavedPassword"), "auto login: a plain telnet profile has no login and no password element");

        DeviceTerminalSettings.ConnectionSettings.Tcp copy = telnet.Clone();
        check(copy is DeviceTerminalSettings.ConnectionSettings.Telnet && !ReferenceEquals(copy, telnet), "Clone() of a telnet endpoint is a separate telnet endpoint");
        copy.Port = 1;
        check(telnet.Port == 2323, "changing the copy leaves the original alone");
        DeviceTerminalSettings.ConnectionSettings.Tcp rawCopy = ((DeviceTerminalSettings.ConnectionSettings.Tcp)rawTcp.Settings.Connection).Clone();
        check(!(rawCopy is DeviceTerminalSettings.ConnectionSettings.Telnet), "Clone() of a raw TCP endpoint stays raw TCP");

        check(new DeviceTerminalSettings.ConnectionSettings.Telnet { Host = "h", Port = 0 }.IsValid() == false, "an endpoint without a port is not valid");
        check(telnet.ToString() == "10.0.0.7:2323", "telnet endpoint is shown as host:port");

        // --- the text typed for a quick telnet connection ---
        DeviceTerminalSettings.ConnectionSettings.Telnet Parse(string text) => DeviceTerminalSettings.ConnectionSettings.Telnet.Parse(text);
        bool Rejects(string text)
        {
            try
            {
                Parse(text);
                return false;
            }
            catch (FormatException)
            {
                return true;
            }
        }
        check(Parse("router").Host == "router" && Parse("router").Port == 23, "quick telnet: a host alone uses port 23");
        check(Parse("  10.1.2.3:2000 ").Host == "10.1.2.3" && Parse("10.1.2.3:2000").Port == 2000, "quick telnet: host:port, blanks around are ignored");
        check(Parse("telnet://switch.local:2323/").Host == "switch.local" && Parse("telnet://switch.local:2323/").Port == 2323, "quick telnet: telnet:// prefix and trailing slash");
        check(Parse("[::1]:2323").Host == "::1" && Parse("[::1]:2323").Port == 2323, "quick telnet: [ipv6]:port");
        check(Parse("[fe80::1]").Host == "fe80::1" && Parse("[fe80::1]").Port == 23, "quick telnet: [ipv6] alone uses port 23");
        check(Parse("fe80::1:2").Host == "fe80::1:2" && Parse("fe80::1:2").Port == 23, "quick telnet: a bare ipv6 address is not cut at a colon");
        check(Rejects("") && Rejects("   ") && Rejects(":23"), "quick telnet: an empty host is rejected");
        check(Rejects("host:") && Rejects("host:abc") && Rejects("host:0") && Rejects("host:70000") && Rejects("host:-1"), "quick telnet: a wrong port is rejected");
        check(Rejects("[::1") && Rejects("[::1]x"), "quick telnet: a broken [ipv6] is rejected");
        check(Parse("host:65535").Port == 65535 && Parse("host:1").Port == 1, "quick telnet: the port range limits are accepted");
        check(Parse("h").IsValid(), "quick telnet: a parsed endpoint is valid");
    }
}
