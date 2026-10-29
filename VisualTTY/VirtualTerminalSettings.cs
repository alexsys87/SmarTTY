using SmarTTY.Utilities;
using System;
using System.Collections.Generic;

namespace VisualTTY;

public class VirtualTerminalSettings
{
    public VirtualTerminalMode DefaultMode { get; set; }

    public AutoCompletionTriggers CompletionTriggers { get; set; } = new AutoCompletionTriggers();

    /// <summary>How the shell of the interactive wrapper is chosen (see SmarTTY.Terminal.ShellSelection).</summary>
    public enum ShellSelectionMode
    {
        [EnumDisplayText("Detect (bash, or ash on OpenWrt)")]
        Auto,
        [EnumDisplayText("Manual")]
        Manual
    }

    private ShellSelectionMode? _shellMode;

    /// <summary>
    /// Automatic: per connection, the login shell if it is bash or ash, otherwise bash, ash or sh, whichever is installed.
    /// Manual: <see cref="DefaultShell"/>. Settings saved before this option existed have no value for it; a shell other
    /// than the old default "bash" was typed in by the user there, so it stays in use.
    /// </summary>
    public ShellSelectionMode ShellMode
    {
        get => _shellMode ?? ((string.IsNullOrWhiteSpace(DefaultShell) || DefaultShell.Trim() == "bash") ? ShellSelectionMode.Auto : ShellSelectionMode.Manual);
        set => _shellMode = value;
    }

    /// <summary>The shell of the manual mode, used as typed (e.g. "bash", "/bin/ash" or "bash --norc").</summary>
    public string DefaultShell { get; set; } = "bash";

    public bool InteractiveModeWrapper { get; set; } = true;

    /// <summary>Show "[exit code N]" after a command that fails (and a note when the connection was lost).</summary>
    public bool ShowExitCodes { get; set; } = true;

    public bool ShowFileExplorer { get; set; } = true;

    /// <summary>
    /// Byte mode for programs run in Smart Terminal tabs when the shell counts UTF-8 bytes (BusyBox without Unicode
    /// support, OpenWrt); the command line itself is always edited locally. See GlobalSettings.ByteOrientedLineEditing.
    /// </summary>
    public GlobalSettings.ByteOrientedLineEditingMode ByteOrientedLineEditing { get; set; }

    /// <summary>The list that older versions shipped as the default; settings that still hold it are upgraded once.</summary>
    public const string OldDefaultAutoLinkExtensions = ".c;.cpp;.h;.sh;.txt;.js";

    /// <summary>Text file extensions that are underlined in the output of commands such as "ls" and open in the editor on click.</summary>
    public const string DefaultAutoLinkExtensions = ".txt;.log;.json;.html;.htm;.xml;.css;.js;.py;.sh;.bash;.c;.cpp;.cc;.h;.hpp;.md;.csv;.tsv;.yml;.yaml;.toml;.ini;.cfg;.conf;.cnf;.env;.properties;.sql;.php;.pl;.rb;.lua;.go;.rs;.java;.kt;.mk;.cmake;.diff;.patch;.service;.rules;.list;.repo;.desktop;.rst;.tex;.svg;.jsx;.tsx;.vue;.scss";

    public string AutoLinkExtensions { get; set; } = DefaultAutoLinkExtensions;

    /// <summary>
    /// Splits the "AutoLinkExtensions" list (separated by semicolons, commas or spaces) into a case-insensitive set;
    /// entries are written with or without the leading dot.
    /// </summary>
    public static HashSet<string> ParseAutoLinkExtensions(string list)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in (list ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string extension = item.Trim();
            if (extension.Length == 0 || extension == ".")
            {
                continue;
            }
            result.Add(extension[0] == '.' ? extension : "." + extension);
        }
        return result;
    }
}
