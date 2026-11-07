using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.CodeEditing
{
    /// <summary>
    /// Syntax highlighting for the built-in SmarTTY editor.
    ///
    /// The class provides:
    ///   * token type constants (indices into CodeEditorPP.TokenBrushes);
    ///   * language detection from the file name (and the "#!" line of files without an extension);
    ///   * line tokenizers (TokenCache.LineTokenizer delegates).
    ///
    /// Tokenizers emit color tokens (ITokenSink.AddToken) and, for languages with balanced brackets,
    /// paired bracket tokens without a fold style, so the editor can highlight the matching bracket.
    /// State that spans lines (Python triple quotes, block comments, ...) is kept in the integer
    /// startState/endState that TokenCache stores for every line.
    /// </summary>
    public static partial class SyntaxHighlighting
    {
        // ------------------------------------------------------------------
        // Token types: indices into TokenBrushes. Index 0 must be the plain text
        // color (used as a fallback when TokenBrushes is empty).
        // ------------------------------------------------------------------
        public const int TypeNormal = 0;        // plain text
        public const int TypeKeyword = 1;       // keywords, tag names
        public const int TypeString = 2;        // strings, removed diff lines
        public const int TypeComment = 3;       // comments, added diff lines
        public const int TypeNumber = 4;        // numbers
        public const int TypeVariable = 5;      // shell variables, configuration keys
        public const int TypeSection = 6;       // [section] headers, YAML document markers, diff hunks
        public const int TypePreprocessor = 7;  // shebang, #include, Python decorators
        public const int TypeCount = 8;         // palette size

        // Pair ids of bracket tokens: TokenCache matches an opening token with a closing token of the same id.
        private const int PairParenthesis = 1;
        private const int PairSquareBracket = 2;
        private const int PairCurlyBrace = 3;

        /// <summary>Supported languages.</summary>
        public enum Language
        {
            Plain,   // no highlighting
            Python,
            Bash,
            Config,  // INI / .conf / .properties / TOML / systemd units
            CLike,   // C: // and /* */ comments, # preprocessor directives
            Script,  // R, Nix: the generic # comment profile
            Sql,     // SQL: -- comments
            Markup,  // HTML, XML: <!-- --> comments and tag names
            Diff,    // unified diffs
            Lua,     // Lua: -- comments, [[long strings]], foldable function/if/for/while/do/repeat blocks
            Json,    // JSON: keys, strings, numbers, foldable { } and [ ] blocks
            Makefile,// Makefile: variables, targets, foldable ifeq/define blocks
            Uci,     // OpenWrt UCI (/etc/config/*): config/option/list, foldable config blocks
            Yaml,    // YAML: the colors of Config, blocks that fold by indentation
            Cpp,     // C++: the C library plus the STL; separate so C files never see C++ symbols
            Systemd, // systemd units: the colors of Config, the directives of systemd are keywords (SyntaxHighlighting.AdminFormats.cs)
            Nginx,   // nginx configuration: the colors of Config, the directives of nginx are keywords
            SshConfig, // ssh_config, sshd_config: the colors of Config, the options of OpenSSH are keywords
            Log,     // logs: the timestamp and the level (ERROR, WARN, ...) of each line
            Ruby,    // Ruby: # comments, def/end blocks
            Perl,    // Perl: # comments, sub blocks
            CMake,   // CMake: commands, variables and # comments
            Dockerfile, // Dockerfile: instructions and # comments
            CSharp,  // C#: // and /* */ comments, # preprocessor directives
            Java,    // Java: // and /* */ comments, text blocks
            Kotlin,  // Kotlin: // and /* */ comments, raw strings
            Scala,   // Scala: // and /* */ comments, raw strings
            Groovy,  // Groovy: // and /* */ comments, raw strings
            Go,      // Go: // and /* */ comments, raw strings
            Rust,    // Rust: // and /* */ comments, raw strings and lifetimes
            Swift,   // Swift: // and /* */ comments, multi-line strings
            Dart,    // Dart: // and /* */ comments, multi-line strings
            JavaScript, // JavaScript: // and /* */ comments, template strings
            TypeScript, // TypeScript: the JavaScript profile plus type keywords
            Php,     // PHP: //, # and /* */ comments
            Css,     // CSS, SCSS and Less: /* */ comments
            Protobuf, // Protocol Buffers: // and /* */ comments
            DeviceTree, // Device Tree source: C-like comments, no API set
            Tcl,     // Tcl: # comments, command words, foldable { } bodies, no API set
            Awk,     // AWK: # comments, BEGIN/END blocks, foldable { } bodies, no API set
            Assembly, // Assembly: ;, # and @ comments, labels, directives, instructions and registers, no API set
            PowerShell, // PowerShell: # and <# #> comments, $variables, Verb-Noun commands, here-strings, no API set
            Verilog, // Verilog/SystemVerilog: // and /* */ comments, `directives, $system tasks
            Vhdl,    // VHDL: -- comments, case-insensitive keywords
            Kconfig,  // Kconfig and .config: the colors of Config, the Kconfig directives are keywords
            UBoot,   // U-Boot env files and scripts: shell-like words and $variables
            Yocto,   // Yocto/BitBake recipes: the colors of Config, the BitBake variables and tasks are keywords
            LinkerScript, // GNU ld scripts: C-like comments, the commands of the linker are keywords
            Crontab, // cron/crontab: schedule fields, @reboot, environment assignments and comments
            Fstab,   // /etc/fstab: device, mount point, filesystem, options, dump and pass
            Sudoers, // sudoers: aliases, Defaults, tags (NOPASSWD), users, hosts and commands
            MySql,   // my.cnf, my.ini: the colors of Config with the MySQL/MariaDB options known
            Redis,   // redis.conf, sentinel.conf: the colors of Config with the Redis directives known
            HAProxy, // haproxy.cfg: the colors of Config with the sections, directives and values of HAProxy known
            ApacheHttpd // httpd.conf, apache2.conf, .htaccess: the colors of Config with the Apache directives known
        }

        /// <summary>The languages offered in the syntax menu, in the order of the menu.</summary>
        public static readonly Language[] SelectableLanguages =
        {
            Language.Plain, Language.Bash, Language.Uci, Language.Kconfig, Language.UBoot, Language.Yocto, Language.LinkerScript, Language.Lua, Language.Makefile, Language.Json, Language.Config,
            Language.Systemd, Language.Nginx, Language.SshConfig, Language.Crontab, Language.Fstab, Language.Sudoers, Language.MySql, Language.Redis, Language.HAProxy, Language.ApacheHttpd, Language.Log,
            Language.Python, Language.Yaml, Language.CLike, Language.Cpp, Language.CSharp, Language.Java,
            Language.Kotlin, Language.Scala, Language.Groovy, Language.Go, Language.Rust,
            Language.Verilog, Language.Vhdl, Language.Swift,
            Language.Dart, Language.JavaScript, Language.TypeScript, Language.Php, Language.Css, Language.Protobuf,
            Language.DeviceTree, Language.Script, Language.Awk, Language.Tcl, Language.Assembly,
            Language.PowerShell, Language.Ruby, Language.Perl, Language.CMake, Language.Dockerfile,
            Language.Sql, Language.Markup, Language.Diff
        };

        /// <summary>The name of a language in the syntax menu and the status bar.</summary>
        public static string GetDisplayName(Language language)
        {
            switch (language)
            {
                case Language.Plain: return "Plain text";
                case Language.Bash: return "Shell (sh, bash)";
                case Language.Uci: return "UCI (OpenWrt config)";
                case Language.Kconfig: return "Kconfig (kernel, OpenWrt)";
                case Language.UBoot: return "U-Boot (env, scripts)";
                case Language.Yocto: return "Yocto (BitBake recipe)";
                case Language.LinkerScript: return "Linker script (ld)";
                case Language.Lua: return "Lua";
                case Language.Makefile: return "Makefile";
                case Language.Json: return "JSON";
                case Language.Config: return "Config (INI, TOML, .env, properties)";
                case Language.Systemd: return "systemd unit";
                case Language.Nginx: return "nginx config";
                case Language.SshConfig: return "SSH config (ssh_config, sshd_config)";
                case Language.Crontab: return "crontab";
                case Language.Fstab: return "fstab (/etc/fstab)";
                case Language.Sudoers: return "sudoers";
                case Language.MySql: return "MySQL/MariaDB config";
                case Language.Redis: return "Redis config";
                case Language.HAProxy: return "HAProxy config";
                case Language.ApacheHttpd: return "Apache httpd config";
                case Language.Log: return "Log (syslog, application logs)";
                case Language.Yaml: return "YAML";
                case Language.Python: return "Python";
                case Language.CLike: return "C";
                case Language.Cpp: return "C++";
                case Language.CSharp: return "C#";
                case Language.Java: return "Java";
                case Language.Kotlin: return "Kotlin";
                case Language.Scala: return "Scala";
                case Language.Groovy: return "Groovy";
                case Language.Go: return "Go";
                case Language.Rust: return "Rust";
                case Language.Verilog: return "Verilog";
                case Language.Vhdl: return "VHDL";
                case Language.Swift: return "Swift";
                case Language.Dart: return "Dart";
                case Language.JavaScript: return "JavaScript";
                case Language.TypeScript: return "TypeScript";
                case Language.Php: return "PHP";
                case Language.Css: return "CSS, SCSS, Less";
                case Language.Protobuf: return "Protocol Buffers";
                case Language.DeviceTree: return "Device Tree";
                case Language.Script: return "Script (R, Nix)";
                case Language.Awk: return "AWK";
                case Language.Tcl: return "Tcl";
                case Language.Assembly: return "Assembly";
                case Language.PowerShell: return "PowerShell";
                case Language.Ruby: return "Ruby";
                case Language.Perl: return "Perl";
                case Language.CMake: return "CMake";
                case Language.Dockerfile: return "Dockerfile";
                case Language.Sql: return "SQL";
                case Language.Markup: return "HTML, XML";
                case Language.Diff: return "Diff, patch";
                default: return language.ToString();
            }
        }

        /// <summary>Detects the language from the file name (path). Extensions are case insensitive.</summary>
        public static Language DetectLanguage(string remotePath)
        {
            return DetectLanguage(remotePath, null);
        }

        /// <summary>
        /// Detects the language from the file name, falling back to the interpreter named by the "#!"
        /// line for files whose name says nothing (e.g. /usr/local/bin/backup).
        /// </summary>
        public static Language DetectLanguage(string remotePath, string firstLine)
        {
            Language language = DetectLanguageFromName(remotePath);
            if (language == Language.Plain && firstLine != null)
            {
                language = DetectLanguageFromShebang(firstLine);
            }
            return language;
        }

        private static Language DetectLanguageFromName(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                return Language.Plain;
            }

            string name = remotePath.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }
            if (string.IsNullOrEmpty(name))
            {
                return Language.Plain;
            }
            name = name.ToLowerInvariant();

            // OpenWrt keeps UCI configuration files without an extension in /etc/config.
            string directory = remotePath.Replace('\\', '/').ToLowerInvariant();
            if (directory.Contains("/etc/config/") && !name.Contains(".") )
            {
                return Language.Uci;
            }

            Language byPath = DetectLanguageFromPath(directory, name);
            if (byPath != Language.Plain)
            {
                return byPath;
            }

            Language byName = DetectLanguageFromNameOnly(name);
            if (byName == Language.Plain && IsOpenWrtShellDirectory(directory))
            {
                return Language.Bash;
            }
            return byName;
        }

        /// <summary>
        /// The formats an administrator edits that are told by where the file is: nginx configuration, the files of OpenSSH, the
        /// drop-ins of systemd, the logs of /var/log. Plain when the place says nothing.
        /// </summary>
        private static Language DetectLanguageFromPath(string lowerCasePath, string name)
        {
            // The kernel and OpenWrt keep the result of "make config" in /boot/config-<release>.
            if (lowerCasePath.Contains("/boot/") && (name == "config" || name.StartsWith("config-", StringComparison.Ordinal)))
            {
                return Language.Kconfig;
            }
            // A layer of Yocto/OpenEmbedded: .inc is a recipe fragment only there, elsewhere it is PHP or assembly.
            bool yoctoDirectory = lowerCasePath.Contains("/meta/") || lowerCasePath.Contains("/meta-") ||
                lowerCasePath.Contains("/recipes-") || lowerCasePath.Contains("/poky/") ||
                lowerCasePath.Contains("/openembedded/");
            if (yoctoDirectory && (name.EndsWith(".bb", StringComparison.Ordinal) ||
                name.EndsWith(".bbappend", StringComparison.Ordinal) ||
                name.EndsWith(".bbclass", StringComparison.Ordinal) ||
                name.EndsWith(".inc", StringComparison.Ordinal) ||
                name.EndsWith(".conf", StringComparison.Ordinal)))
            {
                return Language.Yocto;
            }
            bool nginxDirectory = lowerCasePath.Contains("/etc/nginx/") || lowerCasePath.Contains("/nginx/sites-") || lowerCasePath.Contains("/nginx/conf");
            if (nginxDirectory && (name.EndsWith(".conf", StringComparison.Ordinal) || name.IndexOf('.') < 0))
            {
                return Language.Nginx;
            }
            if (name == "config" && lowerCasePath.Contains("/.ssh/"))
            {
                return Language.SshConfig;
            }
            if (lowerCasePath.Contains("/etc/cron.d/") || lowerCasePath.Contains("/var/spool/cron/"))
            {
                return Language.Crontab;
            }
            if (lowerCasePath.Contains("/etc/fstab.d/"))
            {
                return Language.Fstab;
            }
            if (lowerCasePath.Contains("/sudoers.d/"))
            {
                return Language.Sudoers;
            }
            if (lowerCasePath.Contains("/etc/mysql/") || lowerCasePath.Contains("/my.cnf.d/") ||
                lowerCasePath.Contains("/mysql/conf.d/") || lowerCasePath.Contains("/mariadb.conf.d/"))
            {
                return Language.MySql;
            }
            if (lowerCasePath.Contains("/etc/redis/") || lowerCasePath.Contains("/redis/conf/"))
            {
                return Language.Redis;
            }
            if (lowerCasePath.Contains("/etc/haproxy/") &&
                (name.EndsWith(".cfg", StringComparison.Ordinal) || name.EndsWith(".conf", StringComparison.Ordinal) || name.IndexOf('.') < 0))
            {
                return Language.HAProxy;
            }
            if ((lowerCasePath.Contains("/etc/httpd/") || lowerCasePath.Contains("/etc/apache2/") ||
                 lowerCasePath.Contains("/etc/apache/")) &&
                (name.EndsWith(".conf", StringComparison.Ordinal) || name == ".htaccess" || name.IndexOf('.') < 0))
            {
                return Language.ApacheHttpd;
            }
            if ((lowerCasePath.Contains("/ssh_config.d/") || lowerCasePath.Contains("/sshd_config.d/")) && name.EndsWith(".conf", StringComparison.Ordinal))
            {
                return Language.SshConfig;
            }
            // /etc/systemd/system/x.service.d/override.conf, /etc/systemd/journald.conf, /usr/lib/systemd/...
            if (lowerCasePath.Contains("/systemd/") && name.EndsWith(".conf", StringComparison.Ordinal))
            {
                return Language.Systemd;
            }
            if (lowerCasePath.Contains("/var/log/") && IsSystemLogName(name))
            {
                return Language.Log;
            }
            return Language.Plain;
        }

        // Files of /var/log that are text: no extension (syslog, messages, auth), .log, and the rotated ones (syslog.1, x.log.2).
        // wtmp, btmp, lastlog and the like are binary and are not opened as text anyway.
        private static bool IsSystemLogName(string name)
        {
            if (name == "wtmp" || name == "btmp" || name == "lastlog" || name == "faillog" || name == "utmp")
            {
                return false;
            }
            int dot = name.LastIndexOf('.');
            if (dot < 0)
            {
                return true;
            }
            string extension = name.Substring(dot + 1);
            return extension == "log" || extension == "old" || (extension.Length > 0 && extension.All(char.IsDigit));
        }

        /// <summary>OpenWrt scripts that have no extension: init scripts, hotplug handlers, uci-defaults.</summary>
        private static bool IsOpenWrtShellDirectory(string lowerCasePath)
        {
            return lowerCasePath.Contains("/etc/init.d/") || lowerCasePath.Contains("/etc/hotplug.d/") ||
                lowerCasePath.Contains("/etc/uci-defaults/") || lowerCasePath.Contains("/lib/functions/") ||
                lowerCasePath.Contains("/lib/netifd/") || lowerCasePath.Contains("/lib/upgrade/");
        }

        private static Language DetectLanguageFromNameOnly(string name)
        {
            // Well-known file names without a meaningful extension.
            switch (name)
            {
                case ".bashrc":
                case "profile":
                case "rc.local":
                case "rc.common":
                case ".bash_profile":
                case ".bash_login":
                case ".bash_logout":
                case ".bash_aliases":
                case ".bash_functions":
                case ".profile":
                case ".zshrc":
                case ".zprofile":
                case ".zshenv":
                case ".kshrc":
                case "pkgbuild":
                case "apkbuild":
                    return Language.Bash;

                case "makefile":
                case "gnumakefile":
                case "bsdmakefile":
                    return Language.Makefile;

                case "dockerfile":
                case "containerfile":
                    return Language.Dockerfile;

                case "cmakelists.txt":
                    return Language.CMake;

                case "gemfile":
                case "rakefile":
                case "vagrantfile":
                case "guardfile":
                case "capfile":
                case "podfile":
                case "fastfile":
                case "appfile":
                case "berksfile":
                case "thorfile":
                case "brewfile":
                case "puppetfile":
                case "dangerfile":
                case "steepfile":
                    return Language.Ruby;

                case "makefile.pl":
                case "build.pl":
                case "cpanfile":
                    return Language.Perl;

                case "requirements.txt":
                case ".gitignore":
                case ".hgignore":
                case ".dockerignore":
                    return Language.Script;

                case ".gitconfig":
                case ".gitmodules":
                case ".editorconfig":
                case ".npmrc":
                case ".inputrc":
                case ".env":
                case "hosts":
                case "passwd":
                case "group":
                case "config":
                    return Language.Config;

                case "crontab":
                    return Language.Crontab;

                case "fstab":
                    return Language.Fstab;

                case "sudoers":
                    return Language.Sudoers;

                case "my.cnf":
                case ".my.cnf":
                case "my.ini":
                case "mysql.cnf":
                case "mariadb.cnf":
                case "mysql.conf":
                case "mariadb.conf":
                    return Language.MySql;

                case "redis.conf":
                case "sentinel.conf":
                case "redis-sentinel.conf":
                    return Language.Redis;

                case "haproxy.cfg":
                case "haproxy.conf":
                    return Language.HAProxy;

                case "httpd.conf":
                case "apache2.conf":
                case "httpd-vhosts.conf":
                case "httpd-ssl.conf":
                case "ports.conf":
                case ".htaccess":
                    return Language.ApacheHttpd;

                case ".config":
                case "kconfig":
                case "config.in":
                case "defconfig":
                    return Language.Kconfig;

                case "uenv.txt":
                case "uboot.env":
                case "uboot-env.txt":
                case "u-boot.env":
                case "u-boot-env.txt":
                case "boot.cmd":
                case "boot.scr":
                case "u-boot.cmd":
                case "u-boot.scr":
                    return Language.UBoot;

                case "ssh_config":
                case "sshd_config":
                    return Language.SshConfig;

                case "nginx.conf":
                    return Language.Nginx;
            }

            // Rotated logs: app.log.1, app.log.2.
            if (name.Contains(".log.") && name.Substring(name.LastIndexOf('.') + 1).All(char.IsDigit))
            {
                return Language.Log;
            }

            // Variants of well-known names: Dockerfile.dev, Makefile.am.
            if (name.EndsWith("_defconfig", StringComparison.Ordinal) ||
                name.StartsWith("kconfig.", StringComparison.Ordinal))
            {
                return Language.Kconfig;
            }
            if ((name.StartsWith("uboot-env", StringComparison.Ordinal) ||
                 name.StartsWith("u-boot-env", StringComparison.Ordinal) ||
                 name.StartsWith("uboot.", StringComparison.Ordinal) ||
                 name.StartsWith("u-boot.", StringComparison.Ordinal)) &&
                (name.EndsWith(".env", StringComparison.Ordinal) || name.EndsWith(".txt", StringComparison.Ordinal) ||
                 name.EndsWith(".cmd", StringComparison.Ordinal) || name.EndsWith(".scr", StringComparison.Ordinal)))
            {
                return Language.UBoot;
            }
            if (name.StartsWith("dockerfile.", StringComparison.Ordinal) || name.StartsWith("containerfile.", StringComparison.Ordinal))
            {
                return Language.Dockerfile;
            }
            if (name.StartsWith("makefile.", StringComparison.Ordinal))
            {
                return Language.Makefile;
            }
            if (name.StartsWith(".bash", StringComparison.Ordinal) ||
                name.StartsWith(".zsh", StringComparison.Ordinal) ||
                name.StartsWith(".ksh", StringComparison.Ordinal))
            {
                return Language.Bash;
            }

            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                return Language.Plain;
            }

            // The linker scripts that the kernel and U-Boot preprocess: vmlinux.lds.S, u-boot.lds.S, foo.ld.in.
            if (name.EndsWith(".lds.s", StringComparison.Ordinal) || name.EndsWith(".lds.in", StringComparison.Ordinal) ||
                name.EndsWith(".ld.in", StringComparison.Ordinal))
            {
                return Language.LinkerScript;
            }

            switch (name.Substring(dot + 1))
            {
                case "py":
                case "pyw":
                case "pyi":
                case "pyx":
                    return Language.Python;

                case "ps1":
                case "psm1":
                case "psd1":
                case "ps1xml":
                case "pssc":
                case "psrc":
                    return Language.PowerShell;

                case "sh":
                case "bash":
                case "zsh":
                case "ksh":
                    return Language.Bash;

                case "ini":
                case "conf":
                case "cfg":
                case "cnf":
                case "config":
                case "properties":
                case "inf":
                case "editorconfig":
                case "toml":
                case "env":
                case "network":
                case "netdev":
                case "link":
                case "desktop":
                case "rules":
                    return Language.Config;

                case "bb":
                case "bbappend":
                case "bbclass":
                    return Language.Yocto;

                case "ld":
                case "lds":
                    return Language.LinkerScript;

                case "service":
                case "socket":
                case "timer":
                case "mount":
                case "automount":
                case "swap":
                case "target":
                case "path":
                case "slice":
                case "scope":
                    return Language.Systemd;

                case "log":
                    return Language.Log;

                case "c":
                case "h":
                    return Language.CLike;

                case "cs":
                    return Language.CSharp;

                case "java":
                    return Language.Java;

                case "kt":
                case "kts":
                    return Language.Kotlin;

                case "scala":
                case "sc":
                    return Language.Scala;

                case "groovy":
                case "gradle":
                    return Language.Groovy;

                case "go":
                    return Language.Go;

                case "rs":
                    return Language.Rust;

                case "v":
                case "vh":
                case "sv":
                case "svh":
                    return Language.Verilog;

                case "vhd":
                case "vhdl":
                    return Language.Vhdl;

                case "swift":
                    return Language.Swift;

                case "dart":
                    return Language.Dart;

                case "js":
                case "mjs":
                case "cjs":
                case "jsx":
                    return Language.JavaScript;

                case "ts":
                case "tsx":
                case "mts":
                case "cts":
                    return Language.TypeScript;

                case "php":
                case "phtml":
                    return Language.Php;

                case "css":
                case "scss":
                case "sass":
                case "less":
                    return Language.Css;

                case "proto":
                    return Language.Protobuf;

                case "dts":
                case "dtsi":
                    return Language.DeviceTree;

                case "cpp":
                case "cxx":
                case "cc":
                case "c++":
                case "hpp":
                case "hxx":
                case "hh":
                case "h++":
                case "ipp":
                case "tpp":
                case "inl":
                case "ixx":
                case "cppm":
                case "ino":
                    return Language.Cpp;

                case "rb":
                case "rake":
                case "gemspec":
                case "ru":
                    return Language.Ruby;

                case "pl":
                case "pm":
                case "pod":
                case "psgi":
                case "t":
                    return Language.Perl;

                case "cmake":
                    return Language.CMake;

                case "dockerfile":
                    return Language.Dockerfile;

                case "r":
                case "nix":
                    return Language.Script;

                case "uboot":
                    return Language.UBoot;

                case "awk":
                    return Language.Awk;

                case "tcl":
                case "tk":
                    return Language.Tcl;

                case "asm":
                case "s":
                case "nasm":
                    return Language.Assembly;

                case "sql":
                    return Language.Sql;

                case "lua":
                    return Language.Lua;

                case "json":
                case "jsonc":
                case "json5":
                case "geojson":
                case "webmanifest":
                    return Language.Json;

                case "mk":
                case "make":
                    return Language.Makefile;

                case "uci":
                    return Language.Uci;

                case "yml":
                case "yaml":
                    return Language.Yaml;

                case "html":
                case "htm":
                case "xhtml":
                case "xml":
                case "xsd":
                case "xsl":
                case "svg":
                case "plist":
                    return Language.Markup;

                case "diff":
                case "patch":
                    return Language.Diff;

                // Prose (txt, md, csv, keys and certificates) stays plain: apostrophes and
                // "//" in URLs would otherwise be colored as strings and comments. A log is coloured by
                // its timestamps and levels only, which prose cannot be mistaken for.
                default:
                    return Language.Plain;
            }
        }

        private static Language DetectLanguageFromShebang(string firstLine)
        {
            if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
            {
                return Language.Plain;
            }
            string[] words = firstLine.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return Language.Plain;
            }
            string program = ProgramName(words[0]);
            if (program == "env")
            {
                // "#!/usr/bin/env [-S] python3": the interpreter is the first word that is not an option.
                program = "";
                for (int i = 1; i < words.Length; i++)
                {
                    if (!words[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        program = ProgramName(words[i]);
                        break;
                    }
                }
            }
            if (program == "busybox" && words.Length > 1)
            {
                // "#!/bin/busybox sh": the applet is the next word.
                program = ProgramName(words[1]);
            }
            if (program.StartsWith("python", StringComparison.Ordinal))
            {
                return Language.Python;
            }
            if (program.StartsWith("pwsh", StringComparison.Ordinal) ||
                program.StartsWith("powershell", StringComparison.Ordinal))
            {
                return Language.PowerShell;
            }
            if (program == "node" || program == "nodejs" || program == "deno" || program == "bun")
            {
                return Language.JavaScript;
            }
            if (program == "ts-node" || program == "ts-node-esm" || program == "tsx")
            {
                return Language.TypeScript;
            }
            if (program.StartsWith("php", StringComparison.Ordinal))
            {
                return Language.Php;
            }
            if (program.StartsWith("kotlin", StringComparison.Ordinal))
            {
                return Language.Kotlin;
            }
            if (program.StartsWith("scala", StringComparison.Ordinal) || program == "amm")
            {
                return Language.Scala;
            }
            if (program.StartsWith("groovy", StringComparison.Ordinal))
            {
                return Language.Groovy;
            }
            if (program == "swift")
            {
                return Language.Swift;
            }
            if (program == "dart")
            {
                return Language.Dart;
            }
            if (program == "dotnet-script" || program == "csi" || program == "scriptcs")
            {
                return Language.CSharp;
            }
            if (program == "rust-script" || program == "cargo-script")
            {
                return Language.Rust;
            }
            switch (program)
            {
                case "sh":
                case "bash":
                case "zsh":
                case "ksh":
                case "dash":
                case "ash":
                    return Language.Bash;
            }
            if (program == "make" || program == "gmake")
            {
                return Language.Makefile;
            }
            if (program.StartsWith("lua", StringComparison.Ordinal) || program == "luajit")
            {
                return Language.Lua;
            }
            if (program.StartsWith("ruby", StringComparison.Ordinal))
            {
                return Language.Ruby;
            }
            if (program.StartsWith("perl", StringComparison.Ordinal))
            {
                return Language.Perl;
            }
            if (program.StartsWith("cmake", StringComparison.Ordinal))
            {
                return Language.CMake;
            }
            if (program.StartsWith("tclsh", StringComparison.Ordinal) ||
                program.StartsWith("wish", StringComparison.Ordinal) || program == "expect")
            {
                return Language.Tcl;
            }
            if (program.EndsWith("awk", StringComparison.Ordinal))
            {
                return Language.Awk;
            }
            if (program == "rscript" || program.StartsWith("nix", StringComparison.Ordinal))
            {
                return Language.Script;
            }
            return Language.Plain;
        }

        private static string ProgramName(string path)
        {
            int slash = path.LastIndexOf('/');
            return path.Substring(slash + 1).ToLowerInvariant();
        }

        /// <summary>Returns the tokenizer for a language; null for Plain (no highlighting).</summary>
        public static TokenCache.LineTokenizer GetTokenizer(Language language)
        {
            switch (language)
            {
                case Language.Python: return TokenizePython;
                case Language.Bash: return TokenizeBash;
                case Language.Config: return TokenizeConfig;
                case Language.CLike: return CLikeTokenizer;
                case Language.Cpp: return CppTokenizer;
                case Language.CSharp: return CSharpTokenizer;
                case Language.Java: return JavaTokenizer;
                case Language.Kotlin: return KotlinTokenizer;
                case Language.Scala: return ScalaTokenizer;
                case Language.Groovy: return GroovyTokenizer;
                case Language.Go: return GoTokenizer;
                case Language.Rust: return RustTokenizer;
                case Language.Verilog: return VerilogTokenizer;
                case Language.Vhdl: return VhdlTokenizer;
                case Language.Swift: return SwiftTokenizer;
                case Language.Dart: return DartTokenizer;
                case Language.JavaScript: return JavaScriptTokenizer;
                case Language.TypeScript: return TypeScriptTokenizer;
                case Language.Php: return PhpTokenizer;
                case Language.Css: return CssTokenizer;
                case Language.Protobuf: return ProtobufTokenizer;
                case Language.DeviceTree: return DeviceTreeTokenizer;
                case Language.Script: return ScriptTokenizer;
                case Language.Awk: return AwkTokenizer;
                case Language.Tcl: return TclTokenizer;
                case Language.Assembly: return AssemblyTokenizer;
                case Language.PowerShell: return PowerShellTokenizer;
                case Language.Ruby: return RubyTokenizer;
                case Language.Perl: return PerlTokenizer;
                case Language.CMake: return CMakeTokenizer;
                case Language.Dockerfile: return DockerfileTokenizer;
                case Language.Sql: return SqlTokenizer;
                case Language.Markup: return MarkupTokenizer;
                case Language.Diff: return TokenizeDiff;
                case Language.Lua: return TokenizeLua;
                case Language.Json: return TokenizeJson;
                case Language.Makefile: return TokenizeMakefile;
                case Language.Uci: return TokenizeUci;
                case Language.Kconfig: return KconfigTokenizer;
                case Language.UBoot: return UBootTokenizer;
                case Language.Yocto: return YoctoTokenizer;
                case Language.LinkerScript: return LinkerScriptTokenizer;
                case Language.Yaml: return TokenizeYaml;
                case Language.Systemd: return SystemdTokenizer;
                case Language.Nginx: return NginxTokenizer;
                case Language.SshConfig: return SshConfigTokenizer;
                case Language.Crontab: return TokenizeCrontab;
                case Language.Fstab: return TokenizeFstab;
                case Language.Sudoers: return TokenizeSudoers;
                case Language.MySql: return TokenizeMySql;
                case Language.Redis: return TokenizeRedis;
                case Language.HAProxy: return TokenizeHAProxy;
                case Language.ApacheHttpd: return TokenizeApache;
                case Language.Log: return TokenizeLog;
                default: return null;
            }
        }

        // ---- Word lists for the editor's autocompletion (one list per language) ----

        private static readonly HashSet<string> JsonLiterals = new HashSet<string>(StringComparer.Ordinal)
        {
            "true", "false", "null"
        };

        private static readonly HashSet<string> UciKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "config", "option", "list", "package"
        };

        /// <summary>
        /// The words the editor offers as completions for a language: the language keywords (the same sets the
        /// tokenizers highlight with) plus the common literals and directives. Plain text and Diff have none.
        /// </summary>
        public static IReadOnlyCollection<string> GetKeywords(Language language)
        {
            switch (language)
            {
                case Language.Python:
                    return Combine(PyKeywords, PyBlockKeywords);
                case Language.Bash:
                    return Combine(BashKeywords);
                case Language.Lua:
                    return Combine(LuaKeywords);
                case Language.CLike:
                    return Combine(CKeywords, PreprocessorDirectives);
                case Language.Cpp:
                    return Combine(CppKeywords, PreprocessorDirectives);
                case Language.CSharp:
                    return Combine(CSharpKeywords, CSharpPreprocessorDirectives);
                case Language.Java:
                    return Combine(JavaKeywords);
                case Language.Kotlin:
                    return Combine(KotlinKeywords);
                case Language.Scala:
                    return Combine(ScalaKeywords);
                case Language.Groovy:
                    return Combine(GroovyKeywords);
                case Language.Go:
                    return Combine(GoKeywords);
                case Language.Rust:
                    return Combine(RustKeywords);
                case Language.Verilog:
                    return Combine(VerilogKeywords, VerilogDirectives);
                case Language.Vhdl:
                    return VhdlKeywords;
                case Language.Swift:
                    return Combine(SwiftKeywords);
                case Language.Dart:
                    return Combine(DartKeywords);
                case Language.JavaScript:
                    return Combine(JavaScriptKeywords);
                case Language.TypeScript:
                    return Combine(TypeScriptKeywords);
                case Language.Php:
                    return Combine(PhpKeywords);
                case Language.Css:
                    return Combine(CssKeywords);
                case Language.Protobuf:
                    return Combine(ProtobufKeywords);
                case Language.DeviceTree:
                    return Combine(DeviceTreeKeywords);
                case Language.Script:
                    return Combine(ScriptKeywords);
                case Language.Awk:
                    return Combine(AwkKeywords);
                case Language.Tcl:
                    return Combine(TclKeywords);
                case Language.Assembly:
                    return Combine(AssemblyKeywords, AssemblyRegisters);
                case Language.PowerShell:
                    return Combine(PowerShellKeywords);
                case Language.Ruby:
                    return Combine(RubyKeywords);
                case Language.Perl:
                    return Combine(PerlKeywords);
                case Language.CMake:
                    return Combine(CMakeKeywords);
                case Language.Dockerfile:
                    return Combine(DockerfileKeywords);
                case Language.Sql:
                    return Combine(SqlKeywords);
                case Language.Makefile:
                    return Combine(MakeDirectives);
                case Language.Json:
                    return Combine(JsonLiterals);
                case Language.Config:
                    return Combine(ConfigConstants);
                case Language.Yaml:
                    return Combine(ConfigConstants);
                case Language.Uci:
                    return Combine(UciKeywords);
                case Language.Kconfig:
                    return Combine(KconfigKeywords, KconfigValues);
                case Language.UBoot:
                    return Combine(UBootKeywords);
                case Language.Yocto:
                    return Combine(YoctoDirectives);
                case Language.LinkerScript:
                    return Combine(LinkerKeywords);
                case Language.Systemd:
                    return Combine(SystemdDirectives, SystemdSectionNames, SystemdValues, ConfigConstants);
                case Language.Nginx:
                    return Combine(NginxDirectives, NginxValues, ConfigConstants);
                case Language.SshConfig:
                    return Combine(SshDirectives, SshValues);
                case Language.Crontab:
                    return Combine(CrontabKeywords);
                case Language.Fstab:
                    return Combine(FstabTypes, FstabOptions);
                case Language.Sudoers:
                    return Combine(SudoersDirectives, SudoersValues);
                case Language.MySql:
                    return Combine(MySqlDirectives, MySqlValues);
                case Language.Redis:
                    return Combine(RedisDirectives, RedisValues);
                case Language.HAProxy:
                    return Combine(HAProxyDirectives, HAProxyValues);
                case Language.ApacheHttpd:
                    return Combine(ApacheDirectives, ApacheValues);
                case Language.Log:
                    return Combine(LogLevelWords);
                default:
                    return EmptyKeywords;
            }
        }

        private static readonly string[] EmptyKeywords = new string[0];

        private static IReadOnlyCollection<string> Combine(params IEnumerable<string>[] sets)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            foreach (IEnumerable<string> set in sets)
            {
                foreach (string word in set)
                {
                    result.Add(word);
                }
            }
            return result;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsIdStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool IsBlank(char c)
        {
            return c == ' ' || c == '\t';
        }

        private static int SkipBlanks(string text, int index)
        {
            while (index < text.Length && IsBlank(text[index]))
            {
                index++;
            }
            return index;
        }

        private static bool StartsWithAt(string text, int index, string value)
        {
            return index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
        }

        /// <summary>
        /// Leaves [start, end) in the plain text color and moves <paramref name="last"/> to end.
        /// No token is emitted: the editor paints text between tokens with the default color, and
        /// skipping these tokens halves the number of objects TokenCache keeps for a large file.
        /// </summary>
        private static void Normal(ref int last, int start, int end, TokenCache.ITokenSink sink)
        {
            last = end;
        }

        /// <summary>Finds the end of a quoted string honoring backslash escapes.
        /// Returns the index AFTER the closing quote or -1 if the string is not closed on this line.</summary>
        private static int FindQuoteEnd(string text, int start, char quote)
        {
            int len = text.Length;
            int i = start + 1;
            while (i < len)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == quote)
                {
                    return i + 1;
                }
                i++;
            }
            return -1;
        }

        private static bool TryGetBracketPair(char c, out int pairId, out bool isOpening)
        {
            switch (c)
            {
                case '(': pairId = PairParenthesis; isOpening = true; return true;
                case ')': pairId = PairParenthesis; isOpening = false; return true;
                case '[': pairId = PairSquareBracket; isOpening = true; return true;
                case ']': pairId = PairSquareBracket; isOpening = false; return true;
                case '{': pairId = PairCurlyBrace; isOpening = true; return true;
                case '}': pairId = PairCurlyBrace; isOpening = false; return true;
                default: pairId = 0; isOpening = false; return false;
            }
        }

        /// <summary>
        /// The fold of a multi-line { } block: "if (x) {...}". A "{" alone on its line folds from the line above
        /// (Allman style), and the blank line after the block stays visible.
        /// </summary>
        private static readonly TokenCache.RegionStyle CurlyBraceRegion = new TokenCache.RegionStyle(
            TokenCache.RegionStyle.FoldBoundary.TokenEnd, TokenCache.RegionStyle.FoldBoundary.TokenEnd, unusedFlag: false,
            TokenCache.RegionStyle.LabelSource.FixedText, "...", openingOnNextLine: true, trailingBlankLines: 0);

        /// <summary>
        /// Emits text[index] as a paired bracket token when it is a bracket. With <paramref name="foldCurlyBraces"/>
        /// a { } pair (and a [ ] pair with <paramref name="foldSquareBrackets"/>) spanning lines can be folded; other pairs are only used for matching-bracket highlighting.
        /// </summary>
        private static bool TryAddBracket(string text, int index, ref int last, TokenCache.ITokenSink sink, bool foldCurlyBraces = false, bool foldSquareBrackets = false)
        {
            if (!TryGetBracketPair(text[index], out int pairId, out bool isOpening))
            {
                return false;
            }
            Normal(ref last, last, index, sink);
            TokenCache.RegionStyle regionStyle = ((foldCurlyBraces && pairId == PairCurlyBrace) || (foldSquareBrackets && pairId == PairSquareBracket)) ? CurlyBraceRegion : null;
            sink.AddPairedToken(TypeNormal, index, index + 1, pairId, isOpening, regionStyle);
            last = index + 1;
            return true;
        }

        private static bool TryAddShebang(string text, TokenCache.ITokenSink sink)
        {
            if (sink.CurrentLine == 0 && text.StartsWith("#!", StringComparison.Ordinal))
            {
                sink.AddToken(TypePreprocessor, 0, text.Length);
                return true;
            }
            return false;
        }

        // ==================================================================
        // Python
        // ==================================================================

        // Line states:
        //   0x01 - inside a """ string
        //   0x02 - inside a ''' string
        private const int StPyTripleDouble = 0x01;
        private const int StPyTripleSingle = 0x02;

        private static readonly HashSet<string> PyKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "as", "assert", "async", "await", "break", "class", "continue",
            "def", "del", "elif", "else", "except", "False", "finally", "for", "from",
            "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not",
            "or", "pass", "raise", "return", "True", "try", "while", "with", "yield"
        };

        /// <summary>Returns the index after the closing triple quote (honoring escapes) or -1.</summary>
        private static int FindTripleQuoteEnd(string text, int start, string quote)
        {
            int i = start;
            while (i < text.Length)
            {
                if (text[i] == '\\')
                {
                    i += 2;
                    continue;
                }
                if (StartsWithAt(text, i, quote))
                {
                    return i + quote.Length;
                }
                i++;
            }
            return -1;
        }

        private static bool TokenizePython(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;
            int stringState = startState & StPyStringMask;
            int depth = (startState >> PyDepthShift) & 7;          // open indented blocks
            int bracket = (startState >> PyBracketShift) & 3;      // unclosed ( [ {
            int header = (startState >> PyHeaderShift) & 31;       // indent + 1 of a block header whose statement goes on
            int levels = (startState >> PyLevelShift) & 0xFFFFF;   // indent increase of every open block

            int Pack(int stringBits)
            {
                return stringBits | (depth << PyDepthShift) | (bracket << PyBracketShift) | (header << PyHeaderShift) | (levels << PyLevelShift);
            }

            // At the end of the document every block that is still open ends.
            void CloseAtEnd()
            {
                if (!sink.IsLastLine)
                {
                    return;
                }
                for (; depth > 0; depth--)
                {
                    sink.AddPairedToken(TypeNormal, len, len, PairIndentBlock, false, null);
                    levels &= ~(0xF << (4 * (depth - 1)));
                }
            }

            // The statement ends here (before a comment or at the end of the line): "def f(x):" opens a block.
            void FinishStatement(int codeEnd)
            {
                if (bracket != 0 || header == 0)
                {
                    return;
                }
                int end = codeEnd;
                while (end > 0 && IsBlank(text[end - 1]))
                {
                    end--;
                }
                if (end > 0 && text[end - 1] == ':' && depth < PyMaxLevels)
                {
                    int cumulative = IndentLevelSum(levels, depth, 0xF);
                    int delta = Math.Max(0, Math.Min(15, (header - 1) - cumulative));
                    levels |= delta << (4 * depth);
                    depth++;
                    sink.AddPairedToken(TypeNormal, end, end, PairIndentBlock, true, ExclusiveBlockRegion);
                }
                header = 0;
            }

            if (stringState != 0)
            {
                // Continuation of a triple-quoted string from the previous line.
                string quote = (stringState == StPyTripleDouble) ? "\"\"\"" : "'''";
                int close = FindTripleQuoteEnd(text, 0, quote);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    CloseAtEnd();
                    endState = Pack(stringState);
                    return true;
                }
                sink.AddToken(TypeString, 0, close);
                i = close;
                last = i;
            }
            else if (TryAddShebang(text, sink))
            {
                endState = Pack(0);
                return true;
            }
            else
            {
                // A new statement: the blocks that are indented as deep or deeper than this line end here.
                int first = SkipBlanks(text, 0);
                if (bracket == 0 && first < len)
                {
                    int indent = IndentColumns(text, first);
                    while (depth > 0 && IndentLevelSum(levels, depth, 0xF) >= indent)
                    {
                        sink.AddPairedToken(TypeNormal, 0, 0, PairIndentBlock, false, null);
                        levels &= ~(0xF << (4 * (depth - 1)));
                        depth--;
                    }
                    if (text[first] != '#')
                    {
                        header = IsPythonBlockHeader(text, first) ? Math.Min(31, indent + 1) : 0;
                    }
                }
            }

            int firstNonBlank = SkipBlanks(text, 0);
            while (i < len)
            {
                char c = text[i];

                // Comment up to the end of the line.
                if (c == '#')
                {
                    FinishStatement(i);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    CloseAtEnd();
                    endState = Pack(0);
                    return true;
                }

                // Strings.
                if (c == '"' || c == '\'')
                {
                    bool triple = i + 2 < len && text[i + 1] == c && text[i + 2] == c;
                    Normal(ref last, last, i, sink);
                    if (triple)
                    {
                        string quote = (c == '"') ? "\"\"\"" : "'''";
                        int close = FindTripleQuoteEnd(text, i + 3, quote);
                        if (close < 0)
                        {
                            sink.AddToken(TypeString, i, len);
                            CloseAtEnd();
                            endState = Pack((c == '"') ? StPyTripleDouble : StPyTripleSingle);
                            return true;
                        }
                        sink.AddToken(TypeString, i, close);
                        i = close;
                        last = i;
                        continue;
                    }
                    int end = FindQuoteEnd(text, i, c);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Decorators: @name or @module.name at the start of a line.
                if (c == '@' && i == firstNonBlank && i + 1 < len && IsIdStart(text[i + 1]))
                {
                    int s = i;
                    i++;
                    while (i < len && (IsIdChar(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypePreprocessor, s, i);
                    last = i;
                    continue;
                }

                // Numbers (including 0x.., 0o.., 0b.., 1_000, 1e-5, 1.5j).
                if (IsDigit(c) || (c == '.' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    while (i < len)
                    {
                        char n = text[i];
                        if (IsDigit(n) || n == '_' || n == '.' || n == 'x' || n == 'X' ||
                            n == 'o' || n == 'O' || n == 'j' || n == 'J' ||
                            (n >= 'a' && n <= 'f') || (n >= 'A' && n <= 'F') ||
                            ((n == '+' || n == '-') && i > s && (text[i - 1] == 'e' || text[i - 1] == 'E')))
                        {
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Words: keywords; other identifiers and string prefixes (f, r, b, rf) stay plain.
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    if (PyKeywords.Contains(text.Substring(s, i - s)))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                if (TryAddBracket(text, i, ref last, sink))
                {
                    // Statements go on over lines while a bracket is open.
                    if (c == '(' || c == '[' || c == '{')
                    {
                        bracket = Math.Min(3, bracket + 1);
                    }
                    else
                    {
                        bracket = Math.Max(0, bracket - 1);
                    }
                    i++;
                    continue;
                }

                i++;
            }

            FinishStatement(len);
            Normal(ref last, last, len, sink);
            CloseAtEnd();
            endState = Pack(0);
            return true;
        }

        // ==================================================================
        // Bash
        // ==================================================================

        private const int StBashDouble = 0x01;
        private const int StBashSingle = 0x02;

        // Inside a here document: the lines up to the terminator are text, whatever quotes they hold. The state also
        // carries a hash of the terminator word (from bit 4) and whether "<<-" lets the terminator be indented with tabs.
        private const int StBashHeredoc = 0x04;
        private const int StBashHeredocStrip = 0x08;
        private const int BashHeredocHashShift = 4;

        private static int BashHeredocHash(string word)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in word)
                {
                    hash = hash * 31 + c;
                }
                return hash & 0x7FFFFFF;
            }
        }

        /// <summary>
        /// Reads a here document opener ("&lt;&lt;EOF", "&lt;&lt;-'EOF'") at <paramref name="start"/>. <paramref name="end"/> is
        /// the index after it. A shift in arithmetic ("$((1 &lt;&lt; n))") or a here string is not an opener.
        /// </summary>
        private static bool TryParseBashHeredoc(string text, int start, out int end, out int state)
        {
            end = start + 2;
            state = 0;
            int arithmetic = text.LastIndexOf("((", start, StringComparison.Ordinal);
            if (arithmetic >= 0 && text.IndexOf("))", arithmetic, start - arithmetic, StringComparison.Ordinal) < 0)
            {
                return false;
            }
            int j = start + 2;
            bool strip = false;
            if (j < text.Length && text[j] == '-')
            {
                strip = true;
                j++;
            }
            j = SkipBlanks(text, j);
            if (j < text.Length && text[j] == '\\')
            {
                j++;
            }
            string word;
            bool quoted = false;
            if (j < text.Length && (text[j] == '\'' || text[j] == '"'))
            {
                int close = text.IndexOf(text[j], j + 1);
                if (close < 0)
                {
                    return false;
                }
                quoted = true;
                word = text.Substring(j + 1, close - j - 1);
                j = close + 1;
            }
            else
            {
                int wordStart = j;
                while (j < text.Length && !IsBlank(text[j]) && ";|&<>()".IndexOf(text[j]) < 0)
                {
                    j++;
                }
                word = text.Substring(wordStart, j - wordStart);
            }
            // A word that is not quoted must look like a name: "<< 3" is a shift.
            if (word.Length == 0 || (!quoted && !IsIdStart(word[0])))
            {
                return false;
            }
            end = j;
            state = StBashHeredoc | (strip ? StBashHeredocStrip : 0) | (BashHeredocHash(word) << BashHeredocHashShift);
            return true;
        }

        private static readonly HashSet<string> BashKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done",
            "case", "esac", "in", "function", "select", "time", "coproc", "local", "export",
            "readonly", "declare", "typeset", "return", "break", "continue", "source", "shift",
            "unset", "alias", "echo", "printf", "read", "trap", "exec", "exit", "set",
            "let", "eval", "test"
        };

        /// <summary>Characters after which a word (a command, a keyword, a '#' comment) starts.</summary>
        private static bool IsBashWordBreak(char c)
        {
            return c == ' ' || c == '\t' || c == ';' || c == '&' || c == '|' || c == '(' || c == ')' || c == '`' || c == '{' || c == '}';
        }

        private static bool TokenizeBash(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            LineBuffer buffer = new LineBuffer(sink);
            bool result = TokenizeBashLine(startState, text, buffer, out endState);
            // "(" and "[" count only when closed on the same line: case patterns "a)" and globs "[a-z" are not brackets.
            buffer.Flush(PairParenthesis, PairSquareBracket);
            return result;
        }

        private static bool TokenizeBashLine(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            bool seenCase = false;
            bool seenElif = false;
            int i = 0;
            int last = 0;
            // A here document opened on this line starts with the next one.
            int pendingHeredoc = 0;

            if ((startState & StBashHeredoc) != 0)
            {
                string body = ((startState & StBashHeredocStrip) != 0) ? text.TrimStart('\t') : text;
                if (BashHeredocHash(body) == (startState >> BashHeredocHashShift))
                {
                    // The terminator.
                    sink.AddToken(TypeKeyword, 0, len);
                    endState = 0;
                    return true;
                }
                if (len > 0)
                {
                    sink.AddToken(TypeString, 0, len);
                }
                endState = startState;
                return true;
            }

            if (startState == StBashDouble || startState == StBashSingle)
            {
                // Continuation of a quoted string from the previous line.
                int end;
                if (startState == StBashDouble)
                {
                    end = FindQuoteEnd(text, -1, '"');
                }
                else
                {
                    int close = text.IndexOf('\'');
                    end = (close < 0) ? -1 : (close + 1);
                }
                if (end < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeString, 0, len);
                    }
                    endState = startState;
                    return true;
                }
                sink.AddToken(TypeString, 0, end);
                i = end;
                last = i;
            }
            else if (TryAddShebang(text, sink))
            {
                endState = 0;
                return true;
            }

            while (i < len)
            {
                char c = text[i];

                // A backslash escapes the next character (\", \#, \$ ...).
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                // Comment: '#' at the start of a word.
                if (c == '#' && (i == 0 || IsBashWordBreak(text[i - 1])))
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = pendingHeredoc;
                    return true;
                }

                // Here document: "<<EOF", "<<-EOF", "<<'EOF'". The lines that follow are text up to the terminator.
                if (c == '<' && i + 1 < len && text[i + 1] == '<')
                {
                    if (i + 2 < len && text[i + 2] == '<')
                    {
                        // A here string, "<<<".
                        i += 3;
                        continue;
                    }
                    if (pendingHeredoc == 0 && TryParseBashHeredoc(text, i, out int openerEnd, out int heredocState))
                    {
                        pendingHeredoc = heredocState;
                        i = openerEnd;
                        continue;
                    }
                    i += 2;
                    continue;
                }

                // Single quotes (no escapes inside, may span lines).
                if (c == '\'')
                {
                    int close = text.IndexOf('\'', i + 1);
                    Normal(ref last, last, i, sink);
                    if (close < 0)
                    {
                        sink.AddToken(TypeString, i, len);
                        endState = StBashSingle;
                        return true;
                    }
                    sink.AddToken(TypeString, i, close + 1);
                    i = close + 1;
                    last = i;
                    continue;
                }

                // Double quotes (with escapes, may span lines).
                if (c == '"')
                {
                    int end = FindQuoteEnd(text, i, '"');
                    Normal(ref last, last, i, sink);
                    if (end < 0)
                    {
                        sink.AddToken(TypeString, i, len);
                        endState = StBashDouble;
                        return true;
                    }
                    sink.AddToken(TypeString, i, end);
                    i = end;
                    last = i;
                    continue;
                }

                // Variables: $var, ${var}, $1, $#, $?, $$, $@, $*, $!, $-.
                if (c == '$' && i + 1 < len)
                {
                    int s = i + 1;
                    int e = -1;
                    if (text[s] == '{')
                    {
                        int close = text.IndexOf('}', s + 1);
                        e = (close < 0) ? len : (close + 1);
                    }
                    else if (IsIdStart(text[s]))
                    {
                        e = s;
                        while (e < len && IsIdChar(text[e]))
                        {
                            e++;
                        }
                    }
                    else if (IsDigit(text[s]) || "@*#?$!-".IndexOf(text[s]) >= 0)
                    {
                        e = s + 1;
                    }
                    if (e > 0)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeVariable, i, e);
                        i = e;
                        last = i;
                        continue;
                    }
                    i++;
                    continue;
                }

                // Numbers.
                if (IsDigit(c))
                {
                    int s = i;
                    while (i < len && IsDigit(text[i]))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Words: keywords/builtins and assignments (name=value, name+=value).
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    // "--set", "/usr/bin/test" or "file.done" are arguments, not keywords or assignments.
                    bool startsWord = s == 0 || IsBashWordBreak(text[s - 1]);
                    if (!startsWord)
                    {
                        continue;
                    }
                    if (i < len && (text[i] == '=' || (text[i] == '+' && i + 1 < len && text[i + 1] == '=')))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeVariable, s, i);
                        last = i;
                        continue;
                    }
                    if ((i == len || IsBashWordBreak(text[i])) && BashKeywords.Contains(text.Substring(s, i - s)))
                    {
                        string word = text.Substring(s, i - s);
                        switch (word)
                        {
                            case "elif":
                                seenElif = true;
                                break;
                            case "then":
                                // "elif ...; then" continues the block that "if" opened.
                                if (!seenElif)
                                {
                                    AddKeywordPair(text, s, i, PairThenBlock, true, ref last, sink);
                                    continue;
                                }
                                seenElif = false;
                                break;
                            case "fi":
                                AddKeywordPair(text, s, i, PairThenBlock, false, ref last, sink);
                                continue;
                            case "do":
                                AddKeywordPair(text, s, i, PairLoopBlock, true, ref last, sink);
                                continue;
                            case "done":
                                AddKeywordPair(text, s, i, PairLoopBlock, false, ref last, sink);
                                continue;
                            case "case":
                                seenCase = true;
                                break;
                            case "in":
                                if (seenCase)
                                {
                                    seenCase = false;
                                    AddKeywordPair(text, s, i, PairCaseBlock, true, ref last, sink);
                                    continue;
                                }
                                break;
                            case "esac":
                                AddKeywordPair(text, s, i, PairCaseBlock, false, ref last, sink);
                                continue;
                        }
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                // Brackets. A "{" / "}" that stands alone is a brace group or a function body; "{a,b}" and "{}" are not.
                if (c == '{' && (i + 1 == len || IsBlank(text[i + 1])) && (i == 0 || IsBashWordBreak(text[i - 1])))
                {
                    TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true);
                    i++;
                    continue;
                }
                if (c == '}' && (i == 0 || IsBlank(text[i - 1]) || text[i - 1] == ';') && (i + 1 == len || IsBashWordBreak(text[i + 1])))
                {
                    TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true);
                    i++;
                    continue;
                }
                if (c == '(' || c == ')' || c == '[' || c == ']')
                {
                    TryAddBracket(text, i, ref last, sink);
                    i++;
                    continue;
                }

                i++;
            }

            Normal(ref last, last, Math.Min(i, len), sink);
            endState = pendingHeredoc;
            return true;
        }

        // ==================================================================
        // Config (INI / .conf / .properties / YAML / TOML / systemd units)
        // ==================================================================

        private static readonly HashSet<string> ConfigConstants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "yes", "no", "on", "off", "null", "none"
        };

        private static bool IsConfigKeyChar(char c)
        {
            return IsIdChar(c) || c == '.' || c == '-';
        }

        private static bool TokenizeConfig(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            return TokenizeConfigCore(null, startState, text, sink, out endState);
        }

        /// <summary>
        /// The tokenizer of Config. With <paramref name="directives"/> (systemd, nginx, ssh) the key at the start of a line is a
        /// keyword when it is one of them, and stays a plain key otherwise. With <paramref name="foldBraces"/> (nginx) the { } of the
        /// blocks are paired brackets that fold: "server {...}". With <paramref name="dollarParentheses"/> (Kconfig) a reference
        /// written as "$(NAME)" is a variable too, and <paramref name="valueKeywords"/> (Kconfig's y, m, n) are the words that are
        /// coloured wherever they stand.
        /// </summary>
        private static bool TokenizeConfigCore(HashSet<string> directives, int startState, string text, TokenCache.ITokenSink sink, out int endState, bool foldBraces = false, bool dollarParentheses = false, HashSet<string> valueKeywords = null)
        {
            int len = text.Length;
            int last = 0;
            int i = SkipBlanks(text, 0);
            endState = 0;

            if (i < len)
            {
                char first = text[i];

                // Whole-line comments: '#' and ';' at the start of a line.
                if (first == '#' || first == ';')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    return true;
                }

                if (first == '[')
                {
                    // [section] / [[table]] headers.
                    int close = text.LastIndexOf(']');
                    if (close > i)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeSection, i, close + 1);
                        i = close + 1;
                        last = i;
                    }
                }
                else if ((StartsWithAt(text, i, "---") || StartsWithAt(text, i, "...")) && SkipBlanks(text, i + 3) == len)
                {
                    // YAML document markers.
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeSection, i, i + 3);
                    i += 3;
                    last = i;
                }
                else
                {
                    // The first word of a line is a key when a value follows: "key = value",
                    // "key: value", "- key: value" (YAML list item) or "Key value" (sshd_config, nginx).
                    int keyStart = i;
                    if (first == '-' && i + 1 < len && text[i + 1] == ' ')
                    {
                        keyStart = SkipBlanks(text, i + 1);
                    }
                    int keyEnd = keyStart;
                    if (keyStart < len && IsIdStart(text[keyStart]))
                    {
                        while (keyEnd < len && IsConfigKeyChar(text[keyEnd]))
                        {
                            keyEnd++;
                        }
                    }
                    if (keyEnd > keyStart)
                    {
                        int probe = SkipBlanks(text, keyEnd);
                        bool isKey = probe < len && (text[probe] == '=' || text[probe] == ':' || (probe > keyEnd && text[probe] != '#'));
                        if (isKey)
                        {
                            Normal(ref last, last, keyStart, sink);
                            bool known = directives != null && directives.Contains(text.Substring(keyStart, keyEnd - keyStart));
                            sink.AddToken(known ? TypeKeyword : TypeVariable, keyStart, keyEnd);
                            i = keyEnd;
                            last = i;
                        }
                    }
                }
            }

            while (i < len)
            {
                char c = text[i];

                // Trailing comment: '#' after whitespace ("key = value # note"); "#fff" stays a value.
                if (c == '#' && i > 0 && IsBlank(text[i - 1]))
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    return true;
                }

                // Quoted strings.
                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                // Variable references: ${NAME}, $NAME, and $(NAME) where the language writes them so (Kconfig).
                if (dollarParentheses && c == '$' && i + 1 < len && text[i + 1] == '(')
                {
                    int close = text.IndexOf(')', i + 2);
                    int e = (close < 0) ? len : (close + 1);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeVariable, i, e);
                    i = e;
                    last = i;
                    continue;
                }

                // Variable references: ${NAME}, $NAME.
                if (c == '$' && i + 1 < len && (text[i + 1] == '{' || IsIdStart(text[i + 1])))
                {
                    int e;
                    if (text[i + 1] == '{')
                    {
                        int close = text.IndexOf('}', i + 2);
                        e = (close < 0) ? len : (close + 1);
                    }
                    else
                    {
                        e = i + 1;
                        while (e < len && IsIdChar(text[e]))
                        {
                            e++;
                        }
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeVariable, i, e);
                    i = e;
                    last = i;
                    continue;
                }

                // Numbers, IP addresses and versions (22, 0.5, 192.168.0.1).
                if (IsDigit(c))
                {
                    int s = i;
                    while (i < len && (IsDigit(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }
                    if (i < len && IsIdChar(text[i]))
                    {
                        // "10M", "3des": part of a word, not a number.
                        while (i < len && IsConfigKeyChar(text[i]))
                        {
                            i++;
                        }
                        continue;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                // Words: yes/no/true/false/on/off are colored as constants.
                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsConfigKeyChar(text[i]))
                    {
                        i++;
                    }
                    string word = text.Substring(s, i - s);
                    if (ConfigConstants.Contains(word) || (valueKeywords != null && valueKeywords.Contains(word)))
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                // The braces of a block. Comments, quoted strings and ${name} were taken above, so what is left is a block
                // ("server {", "}") or a quantifier of a regular expression ("{2,3}"), which opens and closes in its own line.
                if (foldBraces && (c == '{' || c == '}'))
                {
                    TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true);
                    i++;
                    continue;
                }

                i++;
            }

            Normal(ref last, last, len, sink);
            return true;
        }

        // ==================================================================
        // Diff
        // ==================================================================

        private static bool TokenizeDiff(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            endState = 0;
            if (text.Length == 0)
            {
                return true;
            }
            int type;
            if (text.StartsWith("diff ", StringComparison.Ordinal) || text.StartsWith("index ", StringComparison.Ordinal) ||
                text.StartsWith("+++", StringComparison.Ordinal) || text.StartsWith("---", StringComparison.Ordinal))
            {
                type = TypeKeyword;
            }
            else if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                type = TypeSection;
            }
            else if (text[0] == '+')
            {
                type = TypeComment;  // added lines: the green comment color in both themes
            }
            else if (text[0] == '-')
            {
                type = TypeString;   // removed lines: the red string color
            }
            else
            {
                type = TypeNormal;
            }
            sink.AddToken(type, 0, text.Length);
            return true;
        }

        // ==================================================================
        // Generic code (C-like, script, SQL and markup languages)
        // ==================================================================

        // Line state: inside a block comment (/* ... */ or <!-- ... -->).
        private const int StBlockComment = 0x04;

        // Line states of a string that goes on over lines: `...` (JavaScript, Go), """...""" (Java, Kotlin, Swift,
        // C#) and C# @"..." (a doubled quote is the escape). StInTag: inside an HTML/XML tag, where quotes start values.
        private const int StBacktick = 0x08;
        private const int StTripleQuote = 0x10;
        private const int StVerbatim = 0x20;
        private const int StInTag = 0x40;
        // PowerShell here-strings, closed by "@ or '@ at the start of a line, and ordinary quoted strings,
        // which may also go on over lines.
        private const int StPowerShellHereDouble = 0x80;
        private const int StPowerShellHereSingle = 0x100;
        private const int StPowerShellDouble = 0x200;
        private const int StPowerShellSingle = 0x400;

        /// <summary>The C keywords; the C profile stays separate from the other C-like languages.</summary>
        private static readonly HashSet<string> CKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "auto", "bool", "break", "case", "char", "const", "constexpr", "continue",
            "default", "do", "double", "else", "enum", "extern", "false", "float", "for", "goto", "if",
            "inline", "int", "long", "nullptr", "register", "restrict", "return", "short", "signed", "sizeof",
            "static", "static_assert", "struct", "switch", "thread_local", "true", "typedef", "typeof",
            "typeof_unqual", "union", "unsigned", "void", "volatile", "while",
            "_Alignas", "_Alignof", "_Atomic", "_BitInt", "_Bool", "_Complex", "_Decimal128", "_Decimal32",
            "_Decimal64", "_Generic", "_Imaginary", "_Noreturn", "_Static_assert", "_Thread_local"
        };

        private static readonly HashSet<string> CSharpPreprocessorDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "elif", "endif", "define", "undef", "warning", "error", "line", "region", "endregion",
            "pragma", "nullable", "checksum"
        };

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "add", "alias", "and", "as", "ascending", "async", "await", "base", "bool", "break",
            "by", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
            "default", "delegate", "descending", "do", "double", "dynamic", "else", "enum", "equals", "event",
            "explicit", "extern", "false", "file", "finally", "fixed", "float", "for", "foreach", "from",
            "get", "global", "goto", "group", "if", "implicit", "in", "init", "int", "interface", "internal",
            "into", "is", "join", "let", "lock", "long", "managed", "nameof", "namespace", "new", "nint",
            "not", "notnull", "nuint", "null", "object", "on", "operator", "or", "orderby", "out", "override",
            "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "remove",
            "required", "return", "sbyte", "scoped", "sealed", "select", "set", "short", "sizeof", "stackalloc",
            "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unmanaged", "unsafe", "ushort", "using", "value", "var", "virtual", "void",
            "volatile", "when", "where", "while", "with", "yield"
        };

        private static readonly HashSet<string> JavaKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
            "continue", "default", "do", "double", "else", "enum", "exports", "extends", "false", "final",
            "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface",
            "long", "module", "native", "new", "null", "open", "opens", "package", "permits", "private",
            "protected", "provides", "public", "record", "requires", "return", "sealed", "short", "static",
            "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "to", "transient", "true",
            "transitive", "try", "uses", "var", "void", "volatile", "while", "with", "yield"
        };

        private static readonly HashSet<string> KotlinKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "actual", "annotation", "as", "break", "by", "catch", "class", "companion", "const",
            "constructor", "context", "continue", "crossinline", "data", "delegate", "do", "dynamic", "else",
            "enum", "expect", "external", "false", "field", "file", "final", "finally", "for", "fun", "get",
            "if", "import", "in", "infix", "init", "inline", "inner", "interface", "internal", "is", "lateinit",
            "noinline", "null", "object", "open", "operator", "out", "override", "package", "param", "private",
            "property", "protected", "public", "receiver", "reified", "return", "sealed", "set", "setparam",
            "super", "suspend", "tailrec", "this", "throw", "true", "try", "typealias", "typeof", "val",
            "value", "var", "vararg", "when", "where", "while"
        };

        private static readonly HashSet<string> ScalaKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "case", "catch", "class", "def", "derives", "do", "else", "end", "enum", "export",
            "extends", "extension", "false", "final", "finally", "for", "forSome", "given", "if", "implicit",
            "import", "infix", "inline", "lazy", "match", "new", "null", "object", "opaque", "open", "override",
            "package", "private", "protected", "return", "sealed", "super", "then", "this", "throw", "trait",
            "transparent", "true", "try", "type", "using", "val", "var", "while", "with", "yield"
        };

        private static readonly HashSet<string> GroovyKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
            "continue", "def", "default", "do", "double", "else", "enum", "extends", "false", "final",
            "finally", "float", "for", "goto", "if", "implements", "import", "in", "instanceof", "int",
            "interface", "long", "native", "new", "null", "package", "permits", "private", "protected",
            "public", "record", "return", "sealed", "short", "static", "strictfp", "super", "switch",
            "synchronized", "this", "throw", "throws", "trait", "transient", "true", "try", "var", "void",
            "volatile", "while", "yield"
        };

        private static readonly HashSet<string> GoKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for",
            "func", "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select",
            "struct", "switch", "type", "var", "false", "iota", "nil", "true"
        };

        private static readonly HashSet<string> RustKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "async", "await", "become", "box", "break", "const", "continue", "crate", "do",
            "dyn", "else", "enum", "extern", "false", "final", "fn", "for", "if", "impl", "in", "let", "loop",
            "macro", "macro_rules", "match", "mod", "move", "mut", "override", "priv", "pub", "ref", "return",
            "self", "Self", "static", "struct", "super", "trait", "true", "try", "type", "typeof", "union",
            "unsafe", "unsized", "use", "virtual", "where", "while", "yield"
        };

        /// <summary>The reserved words of Verilog-2005 and SystemVerilog (IEEE 1800-2017).</summary>
        private static readonly HashSet<string> VerilogKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "accept_on", "alias", "always", "always_comb", "always_ff", "always_latch", "and", "assert",
            "assign", "assume", "automatic", "before", "begin", "bind", "bins", "binsof", "bit", "break",
            "buf", "bufif0", "bufif1", "byte", "case", "casex", "casez", "cell", "chandle", "checker",
            "class", "clocking", "cmos", "config", "const", "constraint", "context", "continue", "cover",
            "covergroup", "coverpoint", "cross", "deassign", "default", "defparam", "design", "disable",
            "dist", "do", "edge", "else", "end", "endcase", "endchecker", "endclass", "endclocking",
            "endconfig", "endfunction", "endgenerate", "endgroup", "endinterface", "endmodule", "endpackage",
            "endprimitive", "endprogram", "endproperty", "endsequence", "endspecify", "endtable", "endtask",
            "enum", "event", "expect", "export", "extends", "extern", "final", "first_match", "for", "force",
            "foreach", "forever", "fork", "forkjoin", "function", "generate", "genvar", "global", "highz0",
            "highz1", "if", "iff", "ifnone", "ignore_bins", "illegal_bins", "implements", "implies", "import",
            "incdir", "include", "initial", "inout", "input", "inside", "instance", "int", "integer",
            "interconnect", "interface", "intersect", "join", "join_any", "join_none", "large", "let",
            "liblist", "library", "local", "localparam", "logic", "longint", "macromodule", "matches",
            "medium", "modport", "module", "nand", "negedge", "nettype", "new", "nexttime", "nmos", "nor",
            "noshowcancelled", "not", "notif0", "notif1", "null", "or", "output", "package", "packed",
            "parameter", "pmos", "posedge", "primitive", "priority", "program", "property", "protected",
            "pull0", "pull1", "pulldown", "pullup", "pulsestyle_ondetect", "pulsestyle_onevent", "pure",
            "rand", "randc", "randcase", "randsequence", "rcmos", "real", "realtime", "ref", "reg",
            "reject_on", "release", "repeat", "restrict", "return", "rnmos", "rpmos", "rtran", "rtranif0",
            "rtranif1", "s_always", "s_eventually", "s_nexttime", "s_until", "s_until_with", "scalared",
            "sequence", "shortint", "shortreal", "showcancelled", "signed", "small", "solve", "specify",
            "specparam", "static", "string", "strong", "strong0", "strong1", "struct", "super", "supply0",
            "supply1", "sync_accept_on", "sync_reject_on", "table", "tagged", "task", "this", "throughout",
            "time", "timeprecision", "timeunit", "tran", "tranif0", "tranif1", "tri", "tri0", "tri1",
            "triand", "trior", "trireg", "type", "typedef", "union", "unique", "unique0", "unsigned",
            "until", "until_with", "untyped", "use", "uwire", "var", "vectored", "virtual", "void", "wait",
            "wait_order", "wand", "weak", "weak0", "weak1", "while", "wildcard", "wire", "with", "within",
            "wor", "xnor", "xor"
        };

        /// <summary>The compiler directives of Verilog and SystemVerilog (they start with a backtick).</summary>
        private static readonly HashSet<string> VerilogDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "accelerate", "autoexpand_vectornets", "begin_keywords", "celldefine", "default_decay_time",
            "default_nettype", "default_trireg_strength", "define", "delay_mode_distributed", "delay_mode_path",
            "delay_mode_unit", "delay_mode_zero", "else", "elsif", "end_keywords", "endcelldefine",
            "endprotect", "endprotected", "endif", "expand_vectornets", "ifdef", "ifndef", "include", "line",
            "noaccelerate", "noexpand_vectornets", "noremove_gatenames", "noremove_netnames",
            "nosuppress_faults", "nounconnected_drive", "pragma", "protect", "protected", "remove_gatename",
            "remove_netname", "resetall", "suppress_faults", "timescale", "unconnected_drive", "undef", "uselib"
        };

        /// <summary>The reserved words of VHDL-2008 (the language is case-insensitive).</summary>
        private static readonly HashSet<string> VhdlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "abs", "access", "after", "alias", "all", "and", "architecture", "array", "assert", "assume",
            "attribute", "begin", "block", "body", "buffer", "bus", "case", "component", "configuration",
            "constant", "context", "cover", "default", "disconnect", "downto", "else", "elsif", "end",
            "entity", "exit", "fairness", "file", "for", "force", "function", "generate", "generic", "group",
            "guarded", "if", "impure", "in", "inertial", "inout", "is", "label", "library", "linkage",
            "literal", "loop", "map", "mod", "nand", "new", "next", "nor", "not", "null", "of", "on",
            "open", "or", "others", "out", "package", "parameter", "port", "postponed", "procedure",
            "process", "property", "protected", "pure", "range", "record", "register", "reject", "release",
            "rem", "report", "restrict", "return", "rol", "ror", "select", "sequence", "severity", "shared",
            "signal", "sla", "sll", "sra", "srl", "strong", "subtype", "then", "to", "transport", "type",
            "unaffected", "units", "until", "use", "variable", "view", "vmode", "vprop", "vunit", "wait",
            "when", "while", "with", "xnor", "xor"
        };

        private static readonly HashSet<string> SwiftKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "actor", "any", "Any", "as", "associatedtype", "async", "await", "borrowing", "break", "case",
            "catch", "class", "consuming", "continue", "default", "defer", "deinit", "didSet", "distributed",
            "do", "else", "enum", "extension", "fallthrough", "false", "fileprivate", "final", "for", "func",
            "get", "guard", "if", "import", "in", "indirect", "infix", "init", "inout", "internal", "is",
            "isolated", "let", "macro", "nil", "nonisolated", "open", "operator", "optional", "override",
            "package", "postfix", "precedencegroup", "prefix", "private", "protocol", "public", "repeat",
            "required", "rethrows", "return", "self", "Self", "sending", "set", "some", "static", "struct",
            "subscript", "super", "switch", "throw", "throws", "true", "try", "typealias", "var", "where",
            "while", "willSet"
        };

        private static readonly HashSet<string> DartKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "assert", "async", "await", "base", "break", "case", "catch", "class", "const",
            "continue", "covariant", "default", "deferred", "do", "dynamic", "else", "enum", "export",
            "extends", "extension", "external", "factory", "false", "final", "finally", "for", "Function",
            "get", "hide", "if", "implements", "import", "in", "interface", "is", "late", "library", "macro",
            "mixin", "new", "null", "on", "operator", "part", "required", "rethrow", "return", "sealed", "set",
            "show", "static", "super", "switch", "sync", "this", "throw", "true", "try", "typedef", "var",
            "void", "when", "while", "with", "yield"
        };

        private static readonly HashSet<string> JavaScriptKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger",
            "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "from",
            "function", "get", "if", "implements", "import", "in", "instanceof", "interface", "let", "new",
            "null", "of", "package", "private", "protected", "public", "return", "set", "static", "super",
            "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "with",
            "yield"
        };

        private static readonly HashSet<string> TypeScriptKeywords =
            new HashSet<string>(JavaScriptKeywords, StringComparer.Ordinal)
            {
                "abstract", "any", "asserts", "bigint", "boolean", "declare", "infer", "keyof", "module",
                "namespace", "never", "number", "object", "override", "readonly", "require", "satisfies",
                "string", "symbol", "type", "unique", "unknown", "using"
            };

        private static readonly HashSet<string> PhpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "and", "array", "as", "break", "callable", "case", "catch", "class", "clone", "const",
            "continue", "declare", "default", "do", "echo", "else", "elseif", "empty", "enddeclare", "endfor",
            "endforeach", "endif", "endswitch", "endwhile", "enum", "eval", "exit", "extends", "false", "final",
            "finally", "fn", "for", "foreach", "function", "global", "goto", "if", "implements", "include",
            "include_once", "instanceof", "insteadof", "interface", "isset", "list", "match", "namespace",
            "new", "null", "or", "parent", "print", "private", "protected", "public", "readonly", "require",
            "require_once", "return", "self", "static", "switch", "throw", "trait", "true", "try", "unset",
            "use", "var", "while", "xor", "yield"
        };

        /// <summary>The CSS properties, values and at-rule names offered by the CSS profile.</summary>
        private static readonly HashSet<string> CssKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "align-content", "align-items", "align-self", "animation", "background", "border", "bottom",
            "box-shadow", "box-sizing", "color", "content", "cursor", "display", "flex", "flex-direction",
            "flex-wrap", "font", "font-family", "font-size", "font-style", "font-weight", "gap", "grid",
            "grid-area", "grid-column", "grid-row", "grid-template", "height", "justify-content", "left",
            "line-height", "list-style", "margin", "max-height", "max-width", "min-height", "min-width",
            "opacity", "outline", "overflow", "padding", "pointer-events", "position", "right", "text-align",
            "text-decoration", "text-transform", "top", "transform", "transition", "visibility", "white-space",
            "width", "z-index", "absolute", "auto", "block", "bold", "border-box", "center", "column",
            "contain", "cover", "dashed", "dotted", "fixed", "hidden", "inherit", "initial", "inline",
            "inline-block", "italic", "none", "no-repeat", "normal", "relative", "repeat", "revert", "row",
            "scroll", "solid", "static", "sticky", "transparent", "unset", "visible", "wrap", "important",
            "media", "supports", "keyframes", "font-face", "charset", "import", "layer", "namespace", "page",
            "property", "scope", "container"
        };

        private static readonly HashSet<string> ProtobufKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "syntax", "edition", "package", "import", "option", "message", "enum", "service", "rpc", "returns",
            "stream", "repeated", "optional", "required", "oneof", "map", "reserved", "extensions", "extend",
            "group", "to", "max", "public", "weak", "deprecated", "true", "false", "json_name", "default"
        };

        /// <summary>The Device Tree directives, standard properties and common node names.</summary>
        private static readonly HashSet<string> DeviceTreeKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            // Directives.
            "dts-v1", "plugin", "memreserve", "include", "delete-node", "delete-property", "omit-if-no-ref",
            "bits",
            // Core properties and node names.
            "compatible", "model", "status", "reg", "reg-names", "ranges", "dma-ranges", "device_type",
            "phandle", "chosen", "memory", "cpus", "cpu", "soc", "aliases", "reserved-memory", "firmware",
            "symbols", "bootargs", "stdout-path", "simple-bus", "simple-mfd", "syscon", "syscon-reboot",
            "syscon-poweroff",
            // Cell counts.
            "address-cells", "size-cells", "interrupt-cells", "clock-cells", "gpio-cells", "dma-cells",
            "pwm-cells", "reset-cells", "phy-cells", "thermal-sensor-cells", "sound-dai-cells",
            "iommu-cells", "mbox-cells", "power-domain-cells",
            // Interrupts, clocks, resets and DMA.
            "interrupts", "interrupt-parent", "interrupts-extended", "interrupt-names",
            "interrupt-controller", "interrupt-map", "interrupt-map-mask", "clocks", "clock-names",
            "clock-frequency", "clock-output-names", "assigned-clocks", "assigned-clock-parents",
            "assigned-clock-rates", "resets", "reset-names", "reset-gpios", "dmas", "dma-names",
            "dma-coherent", "dma-noncoherent", "iommus", "iommu-map", "iommu-map-mask", "msi-controller",
            "msi-parent", "mboxes", "mbox-names",
            // GPIO, pin control and power.
            "gpios", "gpio-controller", "gpio-ranges", "gpio-line-names", "gpio-reserved-ranges",
            "gpio-keys", "gpio-leds", "ngpios", "wakeup-source", "pinctrl-names", "pinctrl-0", "pinctrl-1",
            "pinctrl-2", "pinctrl-3", "pinctrl-4", "pinctrl-5", "pinctrl-6", "pinctrl-7", "pinctrl-8",
            "pinctrl-9", "pinctrl-assert-gpios", "power-domains", "power-domain-names", "opp-shared",
            "operating-points", "operating-points-v2", "opp-hz", "opp-microvolt", "opp-supported-hw",
            // Buses, storage and network.
            "phys", "phy-names", "phy-handle", "phy-mode", "phy-supply", "mdio", "mdio-parent-bus",
            "ethernet", "local-mac-address", "mac-address", "max-speed", "fixed-link", "speed", "duplex",
            "non-removable", "removable", "broken-cd", "cd-gpios", "wp-gpios", "bus-width", "max-frequency",
            "no-1-8-v", "no-sd", "no-sdio", "no-mmc", "sd-uhs-sdr104", "mmc-hs200-1_8v", "mmc-hs400-1_8v",
            "current-speed", "reg-shift", "reg-io-width", "regulator-name", "regulator-min-microvolt",
            "regulator-max-microvolt", "regulator-always-on", "regulator-boot-on", "vin-supply", "vdd-supply",
            // Common node names.
            "pmu", "l2-cache", "l2-cache-controller", "clock-controller", "reset-controller", "pinctrl",
            "gpio", "leds", "backlight", "regulator", "thermal-zones", "thermal", "cooling-device", "sound",
            "codec", "spi", "i2c", "uart", "serial", "usb", "usb-phy", "pcie", "pci", "mmc", "sdhci",
            "nand", "nor", "flash", "mtd", "ramoops", "nvmem", "eeprom", "rtc", "watchdog", "timer",
            "dma-controller", "dma", "mailbox", "power-domain", "display", "gpu", "video", "camera",
            "audio", "can", "adc", "dac", "pwm", "fan", "hwmon", "sensor", "keypad", "touchscreen",
            "chassis", "no-map", "reusable", "shared-dma-pool"
        };

        /// <summary>The C++ keywords; the C++ profile keeps them separate from C.</summary>
        private static readonly HashSet<string> CppKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor", "bool", "break",
            "case", "catch", "char", "char8_t", "char16_t", "char32_t", "class", "compl", "concept", "const",
            "consteval", "constexpr", "constinit", "const_cast", "continue", "co_await", "co_return", "co_yield",
            "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit",
            "export", "extern", "false", "float", "for", "friend", "goto", "if", "import", "inline", "int",
            "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq", "nullptr", "operator", "or",
            "or_eq", "private", "protected", "public", "register", "reinterpret_cast", "requires", "return",
            "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
            "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename",
            "union", "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while", "xor", "xor_eq",
            "final", "override", "module", "std"
        };

        private static readonly HashSet<string> ScriptKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            // R.
            "if", "else", "repeat", "while", "function", "for", "in", "next", "break", "library", "require",
            "source", "TRUE", "FALSE", "NULL", "NA", "NaN", "Inf",
            // Nix.
            "let", "with", "rec", "inherit", "assert", "or"
        };

        /// <summary>The AWK keywords, built-in functions and variables; the language has no API set.</summary>
        private static readonly HashSet<string> AwkKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            // Pattern blocks and control flow.
            "BEGIN", "END", "BEGINFILE", "ENDFILE", "function", "func", "if", "else", "for", "while",
            "do", "break", "continue", "next", "nextfile", "exit", "return", "delete", "in", "getline",
            // Output.
            "print", "printf",
            // Strings, numbers and types.
            "length", "substr", "index", "split", "patsplit", "sub", "gsub", "match", "sprintf", "tolower",
            "toupper", "asort", "asorti", "gensub", "strtonum", "int", "rand", "srand", "atan2", "cos", "sin",
            "exp", "log", "sqrt", "isarray", "typeof",
            // Bitwise operations and internationalization.
            "and", "or", "xor", "compl", "lshift", "rshift", "dcgettext", "dcngettext", "bindtextdomain",
            // Time, files and the environment.
            "close", "fflush", "system", "mktime", "strftime", "systime",
            // Built-in variables.
            "ARGC", "ARGV", "ARGIND", "CONVFMT", "ENVIRON", "ERRNO", "FIELDWIDTHS", "FILENAME", "FNR", "FPAT",
            "FS", "IGNORECASE", "NF", "NR", "OFMT", "OFS", "ORS", "RLENGTH", "RS", "RSTART", "RT", "SUBSEP",
            "PROCINFO", "BINMODE", "LINT", "TEXTDOMAIN"
        };

        /// <summary>The Tcl commands and control words; the language has no API set.</summary>
        private static readonly HashSet<string> TclKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            // Control flow and procedures.
            "if", "then", "else", "elseif", "for", "foreach", "while", "switch", "case", "default", "break",
            "continue", "return", "proc", "apply", "tailcall", "catch", "try", "finally", "throw", "error",
            "exit", "yield", "yieldto", "coroutine", "uplevel", "upvar", "variable", "global", "namespace",
            "eval", "expr", "rename", "unset", "set", "incr", "append", "lappend", "lassign", "lindex",
            "linsert", "list", "llength", "lmap", "lrange", "lrepeat", "lreplace", "lreverse", "lsearch",
            "lset", "lsort", "concat", "join", "split", "string", "subst", "format", "scan", "regexp",
            "regsub", "binary", "encoding", "clock", "time", "after", "update", "vwait",
            // Packages, introspection and the runtime.
            "package", "source", "load", "unload", "interp", "info", "trace", "history", "unknown", "bgerror",
            "tkerror", "pid", "exec", "platform", "msgcat", "zlib",
            // Channels and files.
            "open", "close", "read", "gets", "puts", "flush", "eof", "seek", "tell", "fblocked", "fconfigure",
            "fcopy", "fileevent", "glob", "cd", "pwd", "file", "socket", "chan", "refchan", "registry",
            // Data structures.
            "array", "dict", "parray",
            // Tk widgets and window management.
            "bell", "bind", "bindtags", "bitmap", "button", "canvas", "checkbutton", "clipboard", "destroy",
            "entry", "event", "focus", "font", "frame", "grab", "grid", "image", "label", "labelframe",
            "listbox", "lower", "menu", "menubutton", "message", "option", "pack", "panedwindow", "place",
            "radiobutton", "raise", "scale", "scrollbar", "selection", "send", "spinbox", "text", "tk",
            "tk_bisque", "tk_chooseColor", "tk_chooseDirectory", "tk_dialog", "tk_focusFollowsMouse",
            "tk_focusNext", "tk_focusPrev", "tk_getOpenFile", "tk_getSaveFile", "tk_messageBox", "tk_popup",
            "tk_setPalette", "tk_textCopy", "tk_textCut", "tk_textPaste", "tkwait", "toplevel", "winfo", "wm",
            // Operators and constants of expr.
            "in", "ni", "eq", "ne", "and", "or", "not", "true", "false", "yes", "no", "on", "off"
        };

        /// <summary>
        /// The Assembly instructions and directives of the common x86, ARM, AArch64, MIPS and RISC-V
        /// assemblers. Registers are a separate set: the tokenizer colours them as variables, not keywords.
        /// </summary>
        private static readonly HashSet<string> AssemblyKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // x86 and x86-64 data transfer.
            "mov", "movabs", "movapd", "movaps", "movbe", "movd", "movdqa", "movdqu", "movnti", "movntdq",
            "movntdqa", "movntpd", "movntps", "movntq", "movq", "movsb", "movsd", "movsq", "movss", "movsw",
            "movsx", "movsxd", "movupd", "movups", "movzx", "lea", "xchg", "xadd", "cmpxchg", "cmpxchg8b",
            "cmpxchg16b", "push", "pop", "pusha", "pushad", "pushf", "pushfd", "pushfq", "popa", "popad",
            "popf", "popfd", "popfq", "bswap", "xlat", "xlatb",
            // x86 arithmetic and logic.
            "add", "adc", "sub", "sbb", "mul", "imul", "div", "idiv", "inc", "dec", "neg", "cmp", "test",
            "and", "or", "xor", "not", "shl", "sal", "shr", "sar", "shld", "shrd", "rol", "ror", "rcl", "rcr",
            "bt", "btc", "btr", "bts", "bsf", "bsr", "popcnt", "lzcnt", "tzcnt", "andn", "bextr", "blsi",
            "blsmsk", "blsr", "bzhi", "pdep", "pext", "rorx", "sarx", "shlx", "shrx", "crc32",
            // x86 floating point, MMX, SSE and AVX.
            "fadd", "faddp", "fchs", "fdiv", "fdivp", "fild", "fist", "fistp", "fld", "fld1", "fldz",
            "fmul", "fmulp", "fst", "fstp", "fsub", "fsubp", "fxch", "fyl2x", "fwait",
            "emms", "movd", "movq", "packssdw", "packsswb", "packuswb", "paddb", "paddd", "paddq", "paddw",
            "pand", "pandn", "pcmpeqb", "pcmpeqd", "pcmpeqw", "pcmpgtb", "pcmpgtd", "pcmpgtw", "pmaddwd",
            "pmulhw", "pmullw", "por", "pshufd", "pshufhw", "pshuflw", "pslld", "psllq", "psllw", "psrad",
            "psraw", "psrld", "psrlq", "psrlw", "psubb", "psubd", "psubq", "psubw", "punpckhbw", "punpckhdq",
            "punpckhqdq", "punpckhwd", "punpcklbw", "punpckldq", "punpcklqdq", "punpcklwd", "pxor",
            "addpd", "addps", "addsd", "addss", "andnpd", "andnps", "andpd", "andps", "cmppd", "cmpps",
            "cmpsd", "cmpss", "comisd", "comiss", "cvtdq2pd", "cvtdq2ps", "cvtpd2dq", "cvtpd2ps", "cvtps2dq",
            "cvtps2pd", "cvtsd2si", "cvtsd2ss", "cvtsi2sd", "cvtsi2ss", "cvtss2sd", "cvtss2si", "cvttpd2dq",
            "cvttps2dq", "cvttsd2si", "cvttss2si", "divpd", "divps", "divsd", "divss", "dppd", "dpps",
            "maxpd", "maxps", "maxsd", "maxss", "minpd", "minps", "minsd", "minss", "mpsadbw", "mulpd",
            "mulps", "mulsd", "mulss", "orpd", "orps", "pabsb", "pabsd", "pabsw", "packusdw", "paddsb",
            "paddsw", "paddusb", "paddusw", "palignr", "pavgb", "pavgw", "pblendvb", "pblendw", "pcmpeqq",
            "pcmpgtq", "pextrb", "pextrd", "pextrq", "pextrw", "phaddd", "phaddsw", "phaddw", "phminposuw",
            "phsubd", "phsubsw", "phsubw", "pinsrb", "pinsrd", "pinsrq", "pinsrw", "pmaxsb", "pmaxsd",
            "pmaxsw", "pmaxub", "pmaxud", "pmaxuw", "pminsb", "pminsd", "pminsw", "pminub", "pminud",
            "pminuw", "pmovmskb", "pmovsxbd", "pmovsxbq", "pmovsxbw", "pmovsxdq", "pmovsxwd", "pmovsxwq",
            "pmovzxbd", "pmovzxbq", "pmovzxbw", "pmovzxdq", "pmovzxwd", "pmovzxwq", "pmuldq", "pmulld",
            "pmuludq", "psadbw", "pshufb", "psignb", "psignd", "psignw", "ptest", "rcpps", "rcpss",
            "roundpd", "roundps", "roundsd", "roundss", "rsqrtps", "rsqrtss", "sqrtpd", "sqrtps", "sqrtsd",
            "sqrtss", "subpd", "subps", "subsd", "subss", "ucomisd", "ucomiss", "unpckhpd", "unpckhps",
            "unpcklpd", "unpcklps", "xorpd", "xorps",
            // x86 control flow and strings.
            "call", "ret", "retf", "retn", "jmp", "ja", "jae", "jb", "jbe", "jc", "jcxz", "je", "jecxz",
            "jg", "jge", "jl", "jle", "jna", "jnae", "jnb", "jnbe", "jnc", "jne", "jng", "jnge", "jnl",
            "jnle", "jno", "jnp", "jns", "jnz", "jo", "jp", "jpe", "jpo", "js", "jz", "loop", "loope",
            "loopne", "loopnz", "loopz", "into", "int", "int3", "iret", "iretq", "syscall", "sysenter",
            "sysexit", "sysret", "ud2", "hlt", "nop", "pause", "wait", "leave", "enter", "bound",
            "cmps", "cmpsb", "cmpsd", "cmpsq", "cmpsw", "lods", "lodsb", "lodsd", "lodsq", "lodsw",
            "movs", "scas", "scasb", "scasd", "scasq", "scasw", "stos", "stosb", "stosd", "stosq", "stosw",
            "ins", "insb", "insd", "insw", "outs", "outsb", "outsd", "outsw", "in", "out",
            "rep", "repe", "repne", "repnz", "repz", "lock",
            "seta", "setae", "setb", "setbe", "sete", "setg", "setge", "setl", "setle", "setne", "setno",
            "setnp", "setns", "seto", "setp", "sets", "setz", "cmova", "cmovae", "cmovb", "cmovbe", "cmove",
            "cmovg", "cmovge", "cmovl", "cmovle", "cmovne", "cmovno", "cmovnp", "cmovns", "cmovnz", "cmovo",
            "cmovp", "cmovs", "cmovz",
            // x86 flags, conversion and system.
            "clc", "cld", "cli", "cmc", "stc", "std", "sti", "lahf", "sahf", "cbw", "cwde", "cdqe", "cwd",
            "cdq", "cqo", "aaa", "aad", "aam", "aas", "daa", "das", "arpl", "bound", "clflush", "clts",
            "cpuid", "invd", "invlpg", "lfence", "mfence", "sfence", "monitor", "mwait", "rdmsr", "wrmsr",
            "rdpmc", "rdrand", "rdseed", "rdtsc", "rdtscp", "rsm", "swapgs", "wbinvd", "xgetbv", "xsetbv",
            "xsave", "xrstor", "xsaveopt", "lgdt", "sgdt", "lidt", "sidt", "lldt", "sldt", "lmsw", "smsw",
            "lar", "lsl", "verr", "verw", "ltr", "str", "prefetchnta", "prefetcht0", "prefetcht1", "prefetcht2",
            "vmcall", "vmlaunch", "vmload", "vmmcall", "vmptrld", "vmptrst", "vmread", "vmresume", "vmrun",
            "vmsave", "vmwrite", "vmxoff", "vmxon",
            // ARM and AArch64.
            "adc", "adcs", "add", "adds", "adr", "adrp", "and", "ands", "asr", "b", "beq", "bne", "bcs",
            "bhs", "bcc", "blo", "bmi", "bpl", "bvs", "bvc", "bhi", "bls", "bge", "blt", "bgt", "ble",
            "bal", "bkpt", "bl", "blr", "br", "bx", "bxj", "cbz", "cbnz", "tbnz", "tbz", "cmn", "cmp",
            "csel", "csinc", "csinv", "csneg", "cpsid", "cpsie", "dc", "dmb", "dsb", "isb", "eor", "eret",
            "ic", "ldm", "ldmia", "ldmdb", "ldp", "ldr", "ldrb", "ldrh", "ldrsb", "ldrsh", "ldrsw", "ldur",
            "lsl", "lsr", "mcr", "mrc", "mov", "movk", "movn", "movz", "mrs", "msr", "mul", "mvn", "neg",
            "negs", "nop", "orn", "orr", "ret", "ror", "rrx", "sbc", "sbcs", "sbfiz", "sbfm", "sdiv",
            "smaddl", "smc", "smull", "stm", "stmia", "stmdb", "stp", "str", "strb", "strh", "stur", "sub",
            "subs", "svc", "sxtb", "sxth", "sxtw", "tst", "ubfiz", "ubfm", "udiv", "umaddl", "umull",
            "uxtb", "uxth", "wfe", "wfi", "yield",
            // MIPS.
            "addi", "addiu", "addu", "andi", "bgez", "bgtz", "blez", "bltz", "bne", "div", "divu", "j",
            "jal", "jalr", "jr", "lb", "lbu", "lh", "lhu", "lui", "lw", "lwl", "lwr", "mfhi", "mflo",
            "mthi", "mtlo", "mult", "multu", "ori", "sb", "sh", "sll", "sllv", "slt", "slti", "sltiu",
            "sltu", "sra", "srav", "srl", "srlv", "sub", "subu", "sw", "swl", "swr", "xori", "mfc0",
            "mtc0",
            // RISC-V.
            "auipc", "beqz", "bgeu", "bgez", "bltu", "bltz", "csrc", "csrr", "csrs", "csrw", "ecall",
            "ebreak", "fence", "jalr", "la", "li", "lwu", "mv", "sd", "slli", "sltiu", "sltu", "srai",
            "srli", "xor", "xori",
            // Common assembler directives (also accepted without the leading dot).
            "section", "segment", "global", "globl", "local", "extern", "public", "weak", "hidden",
            "protected", "internal", "visibility", "default", "bits", "org", "align", "balign", "p2align",
            "alignb", "times", "equ", "equiv", "eqv", "set", "db", "dw", "dd", "dq", "dt", "do", "dy", "dz",
            "resb", "resw", "resd", "resq", "rest", "byte", "short", "word", "long", "quad", "value",
            "zero", "space", "skip", "fill", "ascii", "asciz", "string", "string8", "string16", "uleb128",
            "sleb128", "float", "double", "single", "incbin", "include", "struc", "endstruc", "istruc",
            "iend", "at", "macro", "endmacro", "endm", "rept", "endrep", "irp", "irpc", "endr", "if",
            "ifdef", "ifndef", "ifmacro", "ifctx", "ifidn", "ifidni", "ifnum", "ifstr", "ifn", "ifnn",
            "else", "elif", "endif", "while", "endwhile", "assign", "iassign", "define", "xdefine",
            "idefine", "undef", "error", "warning", "fatal", "pragma", "rotate", "use16", "use32", "use64",
            "code16", "code32", "code64", "intel_syntax", "att_syntax", "syntax", "arch", "cpu", "fpu",
            "thumb", "arm", "thumb_func", "thumb_set", "func", "endfunc", "type", "size", "file", "ident",
            "loc", "comm", "lcomm", "symver", "previous", "pushsection", "popsection", "subsection",
            "cfi_startproc", "cfi_endproc", "cfi_def_cfa", "cfi_offset", "cfi_restore"
        };

        /// <summary>The registers of the common architectures, coloured as variables rather than keywords.</summary>
        private static readonly HashSet<string> AssemblyRegisters = BuildAssemblyRegisters();

        private static HashSet<string> BuildAssemblyRegisters()
        {
            HashSet<string> registers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // x86 8/16/32-bit registers.
                "al", "ah", "ax", "eax", "bl", "bh", "bx", "ebx", "cl", "ch", "cx", "ecx", "dl", "dh", "dx",
                "edx", "sil", "si", "esi", "dil", "di", "edi", "bpl", "bp", "ebp", "spl", "sp", "esp",
                "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "rip", "rflags", "eflags", "flags",
                "cs", "ds", "es", "fs", "gs", "ss", "st", "xmm0", "ymm0", "zmm0",
                // ARM, AArch64 and RISC-V special registers.
                "lr", "pc", "cpsr", "spsr", "apsr", "fpscr", "xzr", "wzr", "zero", "ra", "gp", "tp"
            };
            for (int i = 0; i <= 15; i++)
            {
                registers.Add("r" + i);
                registers.Add("cr" + i);
                registers.Add("dr" + i);
                registers.Add("tr" + i);
            }
            for (int i = 8; i <= 15; i++)
            {
                registers.Add("r" + i + "d");
                registers.Add("r" + i + "w");
                registers.Add("r" + i + "b");
            }
            for (int i = 0; i <= 7; i++)
            {
                registers.Add("mm" + i);
                registers.Add("k" + i);
                registers.Add("st" + i);
            }
            for (int i = 0; i <= 31; i++)
            {
                registers.Add("xmm" + i);
                registers.Add("ymm" + i);
                registers.Add("zmm" + i);
                registers.Add("x" + i);
                registers.Add("w" + i);
                registers.Add("v" + i);
                registers.Add("q" + i);
                registers.Add("d" + i);
                registers.Add("s" + i);
                registers.Add("h" + i);
                registers.Add("b" + i);
            }
            for (int i = 0; i <= 11; i++)
            {
                registers.Add("s" + i);
                registers.Add("a" + i);
            }
            for (int i = 0; i <= 7; i++)
            {
                registers.Add("t" + i);
            }
            return registers;
        }

        /// <summary>
        /// The PowerShell keywords. The commands themselves are not listed: a word of the Verb-Noun shape
        /// ("Get-ChildItem", "Where-Object", including commands of installed modules) is coloured as a keyword.
        /// </summary>
        private static readonly HashSet<string> PowerShellKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "begin", "break", "catch", "class", "configuration", "continue", "data", "default", "do",
            "dynamicparam", "else", "elseif", "end", "enum", "exit", "filter", "finally", "for",
            "foreach", "function", "hidden", "if", "in", "inlinescript", "node", "parallel", "param",
            "process", "return", "sequence", "static", "switch", "throw", "trap", "try", "until",
            "using", "while", "workflow"
        };

        /// <summary>The comparison, logical, bitwise and formatting operators of PowerShell, e.g. "-eq" and "-replace".</summary>
        private static readonly HashSet<string> PowerShellOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-and", "-or", "-xor", "-not", "-band", "-bor", "-bxor", "-bnot", "-shl", "-shr",
            "-eq", "-ne", "-gt", "-ge", "-lt", "-le", "-ceq", "-cne", "-cgt", "-cge", "-clt", "-cle",
            "-ieq", "-ine", "-igt", "-ige", "-ilt", "-ile", "-like", "-notlike", "-clike", "-cnotlike",
            "-ilike", "-inotlike", "-match", "-notmatch", "-cmatch", "-cnotmatch", "-imatch", "-inotmatch",
            "-contains", "-notcontains", "-ccontains", "-cnotcontains", "-icontains", "-inotcontains",
            "-in", "-notin", "-cin", "-cnotin", "-iin", "-inotin", "-is", "-isnot", "-as",
            "-replace", "-creplace", "-ireplace", "-split", "-csplit", "-isplit", "-join", "-f"
        };

        private static readonly HashSet<string> RubyKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "BEGIN", "END", "alias", "and", "begin", "break", "case", "class", "def", "do", "else", "elsif",
            "end", "ensure", "false", "for", "if", "in", "module", "next", "nil", "not", "or", "redo", "rescue",
            "retry", "return", "self", "super", "then", "true", "undef", "unless", "until", "when", "while",
            "yield", "__FILE__", "__LINE__", "__dir__", "require", "require_relative", "load", "include",
            "extend", "prepend", "attr_accessor", "attr_reader", "attr_writer", "private", "protected", "public",
            "module_function", "raise", "lambda", "proc", "catch", "throw", "loop"
        };

        private static readonly HashSet<string> PerlKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "BEGIN", "END", "CHECK", "INIT", "UNITCHECK", "package", "use", "no", "require", "sub", "my", "our",
            "local", "state", "if", "elsif", "else", "unless", "while", "until", "for", "foreach", "continue",
            "last", "next", "redo", "goto", "return", "do", "eval", "die", "warn", "defined", "undef", "ref",
            "bless", "wantarray", "scalar", "qw", "qq", "qr", "qx", "and", "or", "not", "xor", "eq", "ne", "lt",
            "gt", "le", "ge", "cmp", "__END__", "__DATA__"
        };

        private static readonly HashSet<string> CMakeKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add_compile_definitions", "add_compile_options", "add_custom_command", "add_custom_target",
            "add_definitions", "add_dependencies", "add_executable", "add_library", "add_link_options",
            "add_subdirectory", "add_test", "aux_source_directory", "break", "build_command",
            "cmake_host_system_information", "cmake_language", "cmake_minimum_required", "cmake_parse_arguments",
            "cmake_path", "cmake_policy", "configure_file", "continue", "create_test_sourcelist", "ctest_build",
            "ctest_configure", "ctest_coverage", "ctest_empty_binary_directory", "ctest_memcheck",
            "ctest_read_custom_files", "ctest_run_script", "ctest_sleep", "ctest_start", "ctest_submit",
            "ctest_test", "ctest_update", "ctest_upload", "define_property", "else", "elseif", "enable_language",
            "enable_testing", "endforeach", "endfunction", "endif", "endmacro", "endwhile", "execute_process",
            "export", "file", "find_file", "find_library", "find_package", "find_path", "find_program", "foreach",
            "function", "get_cmake_property", "get_directory_property", "get_filename_component", "get_property",
            "get_source_file_property", "get_target_property", "get_test_property", "if", "include",
            "include_directories", "include_external_msproject", "include_guard", "include_regular_expression",
            "install", "link_directories", "link_libraries", "list", "load_cache", "load_command", "macro",
            "make_directory", "mark_as_advanced", "math", "message", "option", "project", "remove",
            "remove_definitions", "return", "separate_arguments", "set", "set_directory_properties",
            "set_property", "set_source_files_properties", "set_target_properties", "set_tests_properties",
            "site_name", "source_group", "string", "target_compile_definitions", "target_compile_features",
            "target_compile_options", "target_include_directories", "target_link_directories",
            "target_link_libraries", "target_link_options", "target_precompile_headers", "target_sources",
            "try_compile", "try_run", "unset", "variable_watch", "while",
            // Common arguments and boolean constants.
            "ON", "OFF", "TRUE", "FALSE", "YES", "NO", "NOTFOUND", "REQUIRED", "QUIET", "INTERFACE", "PUBLIC",
            "PRIVATE", "EXISTS", "COMMAND", "TARGET", "POLICY", "VERSION", "STATUS", "FATAL_ERROR", "WARNING"
        };

        private static readonly HashSet<string> DockerfileKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "RUN", "CMD", "LABEL", "MAINTAINER", "EXPOSE", "ENV", "ADD", "COPY", "ENTRYPOINT", "VOLUME",
            "USER", "WORKDIR", "ARG", "ONBUILD", "STOPSIGNAL", "HEALTHCHECK", "SHELL"
        };

        private static readonly HashSet<string> SqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete",
            "create", "table", "drop", "alter", "add", "index", "view", "join", "left", "right", "inner", "outer",
            "on", "as", "group", "by", "order", "having", "limit", "offset", "union", "all", "distinct", "null",
            "is", "in", "like", "between", "exists", "primary", "key", "foreign", "references", "default",
            "case", "when", "then", "else", "end", "begin", "commit", "rollback", "grant", "revoke", "true", "false",
            "local", "function", "if", "elseif", "for", "while", "do", "repeat", "until", "return", "break", "nil"
        };

        private static readonly HashSet<string> PreprocessorDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "include", "define", "undef", "if", "ifdef", "ifndef", "elif", "else", "endif", "pragma",
            "error", "warning", "line", "import", "region", "endregion", "nullable"
        };

        private sealed class GenericSyntax
        {
            public bool HashComments;          // '#' starts a comment at the start of a line or after whitespace
            public bool HashCommentsAnywhere;  // '#' starts a comment even directly after code (Ruby, Perl, CMake)
            public bool HashCommentsAtLineStart; // '#' starts a comment only as the first non-blank character (Assembly)
            public bool KeywordsAtLineStart;   // only the first word of a line is a keyword (Dockerfile, CMake)
            public bool SlashComments;         // "//" comments
            public bool DashDashComments;      // "--" comments
            public bool SemicolonComments;     // ";" comments (Assembly)
            public bool AtComments;            // "@" comments at the start of a line or after whitespace (Assembly)
            public bool Preprocessor;          // #include-style directives at the start of a line
            public bool BacktickDirectives;    // `define-style directives (Verilog)
            public bool DollarIdentifiers;     // $display-style system tasks (Verilog)
            public bool ShellVariables;        // $name and ${name} shell-style variables (U-Boot)
            public bool SingleQuoteStrings = true;
            public bool MarkupTags;            // <tag> and </tag> names are colored as keywords
            public bool Brackets;              // emit paired bracket tokens
            public bool FoldCurlyBraces;       // multi-line { } blocks can be folded
            public bool BacktickStrings;       // `...` strings that may span lines
            public bool TripleQuoteStrings;    // """...""" strings that may span lines
            public bool VerbatimStrings;       // C# @"..." strings that may span lines
            public bool RustLiterals;          // 'a lifetimes and labels are not strings; r"..." and r#"..."# raw strings
            public bool HyphenatedKeywords;    // CSS and Device Tree words such as font-size and interrupt-parent
            public bool DeviceTreeSyntax;      // /directive/, &label, #cell-count, labels and node names
            public bool AssemblySyntax;        // label:, .directive, %directive and registers
            public bool PowerShellSyntax;      // $variable, Verb-Noun commands, -eq operators, here-strings
            public string BlockCommentStart;
            public string BlockCommentEnd;
            public HashSet<string> Keywords;
            public HashSet<string> Registers;
            public HashSet<string> PreprocessorWords = PreprocessorDirectives;

            private bool IsLineComment(string text, int i)
            {
                char c = text[i];
                if (SlashComments && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    return true;
                }
                if (DashDashComments && c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    return true;
                }
                if (SemicolonComments && c == ';')
                {
                    return true;
                }
                if (AtComments && c == '@' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
                {
                    return true;
                }
                if (HashCommentsAtLineStart && c == '#')
                {
                    int start = 0;
                    while (start < i && IsBlank(text[start]))
                    {
                        start++;
                    }
                    return start == i;
                }
                return HashComments && c == '#' &&
                    (HashCommentsAnywhere || i == 0 || char.IsWhiteSpace(text[i - 1]));
            }

            /// <summary>True when only blanks stand between the start of the line and the index.</summary>
            private static bool IsAtLineStart(string text, int index)
            {
                for (int i = 0; i < index; i++)
                {
                    if (!IsBlank(text[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            /// <summary>The index after the end of a string of the given kind (a state bit) searched from an index, or -1.</summary>
            private static int FindStringEnd(string text, int from, int kind)
            {
                for (int j = from; j < text.Length; j++)
                {
                    char c = text[j];
                    switch (kind)
                    {
                        case StBacktick:
                            if (c == '\\')
                            {
                                j++;
                            }
                            else if (c == '`')
                            {
                                return j + 1;
                            }
                            break;
                        case StTripleQuote:
                            if (c == '\\')
                            {
                                j++;
                            }
                            else if (StartsWithAt(text, j, "\"\"\""))
                            {
                                return j + 3;
                            }
                            break;
                        default:
                            if (c == '"')
                            {
                                if (j + 1 < text.Length && text[j + 1] == '"')
                                {
                                    j++;
                                }
                                else
                                {
                                    return j + 1;
                                }
                            }
                            break;
                    }
                }
                return -1;
            }

            /// <summary>The index after the closing quote of a PowerShell string searched from an index, or -1.</summary>
            private static int FindPowerShellStringEnd(string text, int from, char quote)
            {
                for (int i = from; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '`' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote)
                        {
                            i++;
                        }
                        else
                        {
                            return i + 1;
                        }
                    }
                }
                return -1;
            }

            private static int FindPowerShellQuoteEnd(string text, int start, char quote)
            {
                return FindPowerShellStringEnd(text, start + 1, quote);
            }

            /// <summary>The end of a PowerShell variable at "$" or "@": $name, ${name}, $env:PATH, $1, @args.</summary>
            private static int ReadPowerShellVariable(string text, int start)
            {
                int i = start + 1;
                if (i >= text.Length)
                {
                    return i;
                }
                if (text[i] == '{')
                {
                    int close = text.IndexOf('}', i + 1);
                    return close < 0 ? text.Length : close + 1;
                }
                if (char.IsDigit(text[i]))
                {
                    i++;
                    while (i < text.Length && char.IsDigit(text[i]))
                    {
                        i++;
                    }
                    return i;
                }
                if (text[i] != '?' && text[i] != '^' && text[i] != '$' && !IsIdStart(text[i]))
                {
                    return i;
                }
                i++;
                while (i < text.Length && IsIdChar(text[i]))
                {
                    i++;
                }
                // A scope or drive qualifier: $env:PATH, $global:name, $script:name.
                if (i < text.Length && text[i] == ':' && i + 1 < text.Length && IsIdStart(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && IsIdChar(text[i]))
                    {
                        i++;
                    }
                }
                return i;
            }

            /// <summary>A command name of the Verb-Noun shape, including commands from installed modules.</summary>
            private static bool IsPowerShellCommand(string word)
            {
                int start = 0;
                bool hyphen = false;
                for (int i = 0; i <= word.Length; i++)
                {
                    if (i < word.Length && word[i] != '-')
                    {
                        continue;
                    }
                    if (i == start || !IsIdStart(word[start]))
                    {
                        return false;
                    }
                    for (int j = start + 1; j < i; j++)
                    {
                        if (!IsIdChar(word[j]))
                        {
                            return false;
                        }
                    }
                    if (i < word.Length)
                    {
                        hyphen = true;
                    }
                    start = i + 1;
                }
                return hyphen;
            }

            /// <summary>
            /// A Rust lifetime ('a, 'static) or loop label ('outer:) at index: not a string. Only forms that cannot be
            /// a short string are taken (after &amp;, inside &lt;&gt;, after break/continue, in front of a loop).
            /// </summary>
            private static bool IsRustLifetime(string text, int index, out int end)
            {
                end = index;
                int len = text.Length;
                if (index + 1 >= len || !IsIdStart(text[index + 1]))
                {
                    return false;
                }
                int j = index + 2;
                while (j < len && IsIdChar(text[j]))
                {
                    j++;
                }
                if (j < len && text[j] == '\'')
                {
                    // 'a' is a character literal.
                    return false;
                }
                int p = index - 1;
                while (p >= 0 && IsBlank(text[p]))
                {
                    p--;
                }
                char prev = p >= 0 ? text[p] : '\0';
                bool lifetime = false;
                if (prev == '&')
                {
                    lifetime = !(p > 0 && text[p - 1] == '&');
                }
                else if (prev == '<')
                {
                    lifetime = true;
                }
                else if (prev == ',' || prev == '+')
                {
                    int depth = 0;
                    for (int k = 0; k < index; k++)
                    {
                        if (text[k] == '<')
                        {
                            depth++;
                        }
                        else if (text[k] == '>')
                        {
                            depth--;
                        }
                    }
                    lifetime = depth > 0;
                }
                else if (IsIdChar(prev))
                {
                    int wordStart = p;
                    while (wordStart > 0 && IsIdChar(text[wordStart - 1]))
                    {
                        wordStart--;
                    }
                    string word = text.Substring(wordStart, p - wordStart + 1);
                    lifetime = word == "break" || word == "continue";
                }
                else if ((prev == '\0' || prev == '{' || prev == ';') && j < len && text[j] == ':')
                {
                    int k = SkipBlanks(text, j + 1);
                    lifetime = StartsWithAt(text, k, "loop") || StartsWithAt(text, k, "while") || StartsWithAt(text, k, "for");
                }
                if (lifetime)
                {
                    end = j;
                }
                return lifetime;
            }

            public bool Tokenize(int startState, string text, TokenCache.ITokenSink sink, out int endState)
            {
                int len = text.Length;
                int i = 0;
                int last = 0;
                bool inTag = MarkupTags && (startState & StInTag) != 0;

                if (PowerShellSyntax)
                {
                    int hereString = startState & (StPowerShellHereDouble | StPowerShellHereSingle);
                    if (hereString != 0)
                    {
                        char closeQuote = (hereString == StPowerShellHereDouble) ? '"' : '\'';
                        if (len >= 2 && text[0] == closeQuote && text[1] == '@')
                        {
                            sink.AddToken(TypeString, 0, 2);
                            i = 2;
                            last = 2;
                        }
                        else
                        {
                            if (len > 0)
                            {
                                sink.AddToken(TypeString, 0, len);
                            }
                            endState = hereString;
                            return true;
                        }
                    }
                    else
                    {
                        int quoted = startState & (StPowerShellDouble | StPowerShellSingle);
                        if (quoted != 0)
                        {
                            char quote = (quoted == StPowerShellDouble) ? '"' : '\'';
                            int stringClose = FindPowerShellStringEnd(text, 0, quote);
                            if (stringClose < 0)
                            {
                                if (len > 0)
                                {
                                    sink.AddToken(TypeString, 0, len);
                                }
                                endState = quoted;
                                return true;
                            }
                            sink.AddToken(TypeString, 0, stringClose);
                            i = stringClose;
                            last = i;
                        }
                    }
                }

                int continuedString = startState & (StBacktick | StTripleQuote | StVerbatim);
                // The PowerShell states are handled above; a synthetic state may also carry the bits of another
                // language, and those must not be read a second time from the same line.
                if (PowerShellSyntax &&
                    (startState & (StPowerShellHereDouble | StPowerShellHereSingle | StPowerShellDouble | StPowerShellSingle)) != 0)
                {
                    continuedString = 0;
                }
                if (continuedString != 0)
                {
                    int stringClose = FindStringEnd(text, 0, continuedString);
                    if (stringClose < 0)
                    {
                        if (len > 0)
                        {
                            sink.AddToken(TypeString, 0, len);
                        }
                        endState = continuedString;
                        return true;
                    }
                    sink.AddToken(TypeString, 0, stringClose);
                    i = stringClose;
                    last = i;
                }

                if ((startState & StBlockComment) != 0 && BlockCommentEnd != null)
                {
                    int close = text.IndexOf(BlockCommentEnd, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        if (len > 0)
                        {
                            sink.AddToken(TypeComment, 0, len);
                        }
                        endState = StBlockComment;
                        return true;
                    }
                    i = close + BlockCommentEnd.Length;
                    sink.AddToken(TypeComment, 0, i);
                    last = i;
                }

                if (Preprocessor && i == 0)
                {
                    int hash = SkipBlanks(text, 0);
                    if (hash < len && text[hash] == '#')
                    {
                        int wordStart = SkipBlanks(text, hash + 1);
                        int wordEnd = wordStart;
                        while (wordEnd < len && IsIdChar(text[wordEnd]))
                        {
                            wordEnd++;
                        }
                        if (wordEnd > wordStart && PreprocessorWords.Contains(text.Substring(wordStart, wordEnd - wordStart)))
                        {
                            Normal(ref last, last, hash, sink);
                            sink.AddToken(TypePreprocessor, hash, wordEnd);
                            i = wordEnd;
                            last = i;
                        }
                    }
                }

                while (i < len)
                {
                    char c = text[i];

                    if (BlockCommentStart != null && StartsWithAt(text, i, BlockCommentStart))
                    {
                        Normal(ref last, last, i, sink);
                        int close = text.IndexOf(BlockCommentEnd, i + BlockCommentStart.Length, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            sink.AddToken(TypeComment, i, len);
                            endState = StBlockComment;
                            return true;
                        }
                        int end = close + BlockCommentEnd.Length;
                        sink.AddToken(TypeComment, i, end);
                        i = end;
                        last = i;
                        continue;
                    }

                    if (IsLineComment(text, i))
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeComment, i, len);
                        endState = 0;
                        return true;
                    }

                    // Verilog compiler directives: `timescale, `define, `include, `ifdef, ...
                    if (BacktickDirectives && c == '`' && i + 1 < len && IsIdStart(text[i + 1]))
                    {
                        int wordEnd = i + 2;
                        while (wordEnd < len && IsIdChar(text[wordEnd]))
                        {
                            wordEnd++;
                        }
                        string word = text.Substring(i + 1, wordEnd - i - 1);
                        if (PreprocessorWords != null && PreprocessorWords.Contains(word))
                        {
                            Normal(ref last, last, i, sink);
                            sink.AddToken(TypePreprocessor, i, wordEnd);
                            i = wordEnd;
                            last = i;
                            continue;
                        }
                    }

                    // Verilog system tasks and functions: $display, $finish, $random, ...
                    if (DollarIdentifiers && c == '$' && i + 1 < len && IsIdStart(text[i + 1]))
                    {
                        int wordEnd = i + 2;
                        while (wordEnd < len && IsIdChar(text[wordEnd]))
                        {
                            wordEnd++;
                        }
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeKeyword, i, wordEnd);
                        i = wordEnd;
                        last = i;
                        continue;
                    }

                    // PowerShell here-strings: @" ... "@ and @' ... '@, opened at the end of a line.
                    if (PowerShellSyntax && c == '@' && i + 1 < len &&
                        (text[i + 1] == '"' || text[i + 1] == '\'') && SkipBlanks(text, i + 2) == len)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeString, i, len);
                        endState = (text[i + 1] == '"') ? StPowerShellHereDouble : StPowerShellHereSingle;
                        return true;
                    }

                    // Assembly directives: ".text", ".globl", ".section" of the GNU assembler.
                    // A dot-prefixed name with a colon at the start of the line is a local label.
                    if (AssemblySyntax && c == '.' && i + 1 < len && IsIdStart(text[i + 1]))
                    {
                        int wordEnd = i + 2;
                        while (wordEnd < len && IsIdChar(text[wordEnd]))
                        {
                            wordEnd++;
                        }
                        bool label = wordEnd < len && text[wordEnd] == ':' &&
                            !(wordEnd + 1 < len && text[wordEnd + 1] == ':') && IsAtLineStart(text, i);
                        Normal(ref last, last, i, sink);
                        if (label)
                        {
                            sink.AddToken(TypeSection, i, wordEnd + 1);
                            i = wordEnd + 1;
                        }
                        else
                        {
                            sink.AddToken(TypePreprocessor, i, wordEnd);
                            i = wordEnd;
                        }
                        last = i;
                        continue;
                    }

                    // NASM preprocessor directives ("%define", "%include") and AT&T registers ("%eax", "%r8").
                    if (AssemblySyntax && c == '%' && i + 1 < len && IsIdStart(text[i + 1]))
                    {
                        int wordEnd = i + 2;
                        while (wordEnd < len && IsIdChar(text[wordEnd]))
                        {
                            wordEnd++;
                        }
                        string word = text.Substring(i + 1, wordEnd - i - 1);
                        Normal(ref last, last, i, sink);
                        sink.AddToken(Registers != null && Registers.Contains(word) ? TypeVariable : TypePreprocessor,
                            i, wordEnd);
                        i = wordEnd;
                        last = i;
                        continue;
                    }

                    // Device Tree directives are words between slashes: /include/, /delete-node/, /bits/.
                    if (DeviceTreeSyntax && c == '/' && i + 2 < len && IsIdStart(text[i + 1]))
                    {
                        int wordStart = i + 1;
                        int wordEnd = wordStart;
                        while (wordEnd < len && (IsIdChar(text[wordEnd]) || text[wordEnd] == '-'))
                        {
                            wordEnd++;
                        }
                        if (wordEnd < len && text[wordEnd] == '/' && Keywords != null &&
                            Keywords.Contains(text.Substring(wordStart, wordEnd - wordStart)))
                        {
                            Normal(ref last, last, i, sink);
                            sink.AddToken(TypeKeyword, i, wordEnd + 1);
                            i = wordEnd + 1;
                            last = i;
                            continue;
                        }
                    }

                    // A reference to a label or to a path: &gic, &{/soc/serial@10000000}.
                    if (DeviceTreeSyntax && c == '&' && i + 1 < len &&
                        (IsIdStart(text[i + 1]) || text[i + 1] == '{'))
                    {
                        int referenceStart = i;
                        i++;
                        if (text[i] == '{')
                        {
                            int close = text.IndexOf('}', i + 1);
                            i = (close < 0) ? len : close + 1;
                        }
                        else
                        {
                            while (i < len && (IsIdChar(text[i]) || text[i] == '-'))
                            {
                                i++;
                            }
                        }
                        Normal(ref last, last, referenceStart, sink);
                        sink.AddToken(TypeKeyword, referenceStart, i);
                        last = i;
                        continue;
                    }

                    // A cell-count property: #address-cells, #gpio-cells, #interrupt-cells.
                    if (DeviceTreeSyntax && c == '#' && i + 1 < len && IsIdStart(text[i + 1]))
                    {
                        int wordStart = i + 1;
                        int wordEnd = wordStart;
                        while (wordEnd < len && (IsIdChar(text[wordEnd]) || text[wordEnd] == '-'))
                        {
                            wordEnd++;
                        }
                        if (Keywords != null && Keywords.Contains(text.Substring(wordStart, wordEnd - wordStart)))
                        {
                            Normal(ref last, last, i, sink);
                            sink.AddToken(TypeKeyword, i, wordEnd);
                            i = wordEnd;
                            last = i;
                            continue;
                        }
                    }

                    // Strings that go on over lines.
                    int multiLineKind = 0;
                    int multiLineStart = i;
                    int multiLineBody = i + 1;
                    if (BacktickStrings && c == '`')
                    {
                        multiLineKind = StBacktick;
                    }
                    else if (c == '"' && VerbatimStrings && i > 0 && text[i - 1] == '@')
                    {
                        multiLineKind = StVerbatim;
                        multiLineStart = i - 1;
                    }
                    else if (c == '"' && TripleQuoteStrings && StartsWithAt(text, i, "\"\"\""))
                    {
                        multiLineKind = StTripleQuote;
                        multiLineBody = i + 3;
                    }
                    if (multiLineKind != 0)
                    {
                        int stringEnd = FindStringEnd(text, multiLineBody, multiLineKind);
                        // "@" was passed as plain text: the string starts where it stands.
                        last = Math.Min(last, multiLineStart);
                        Normal(ref last, last, multiLineStart, sink);
                        if (stringEnd < 0)
                        {
                            sink.AddToken(TypeString, multiLineStart, len);
                            endState = multiLineKind;
                            return true;
                        }
                        sink.AddToken(TypeString, multiLineStart, stringEnd);
                        i = stringEnd;
                        last = i;
                        continue;
                    }

                    if (c == '\'' && RustLiterals && IsRustLifetime(text, i, out int lifetimeEnd))
                    {
                        i = lifetimeEnd;
                        continue;
                    }

                    // In HTML and XML a quote is not special in the text between the tags.
                    if ((c == '"' || (c == '\'' && SingleQuoteStrings)) && (!MarkupTags || inTag))
                    {
                        int end = PowerShellSyntax ? FindPowerShellQuoteEnd(text, i, c) : FindQuoteEnd(text, i, c);
                        Normal(ref last, last, i, sink);
                        if (PowerShellSyntax && end < 0)
                        {
                            sink.AddToken(TypeString, i, len);
                            endState = (c == '"') ? StPowerShellDouble : StPowerShellSingle;
                            return true;
                        }
                        sink.AddToken(TypeString, i, (end < 0) ? len : end);
                        i = (end < 0) ? len : end;
                        last = i;
                        continue;
                    }

                    // PowerShell variables: $name, ${name}, $env:PATH, $1; splatting: @args.
                    if (PowerShellSyntax && (c == '$' || (c == '@' && i + 1 < len && IsIdStart(text[i + 1]))))
                    {
                        int variableEnd = ReadPowerShellVariable(text, i);
                        if (variableEnd > i + 1)
                        {
                            Normal(ref last, last, i, sink);
                            sink.AddToken(TypeVariable, i, variableEnd);
                            i = variableEnd;
                            last = i;
                            continue;
                        }
                    }

                    // Shell-style variables of U-Boot scripts and environment variables: $name, ${name}.
                    if (ShellVariables && c == '$' && i + 1 < len && (text[i + 1] == '{' || IsIdStart(text[i + 1])))
                    {
                        int variableEnd;
                        if (text[i + 1] == '{')
                        {
                            int close = text.IndexOf('}', i + 2);
                            variableEnd = (close < 0) ? len : close + 1;
                        }
                        else
                        {
                            variableEnd = i + 1;
                            while (variableEnd < len && IsIdChar(text[variableEnd]))
                            {
                                variableEnd++;
                            }
                        }
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeVariable, i, variableEnd);
                        i = variableEnd;
                        last = i;
                        continue;
                    }

                    // PowerShell comparison, logical and formatting operators: -eq, -not, -replace.
                    if (PowerShellSyntax && c == '-' && i + 1 < len && IsIdStart(text[i + 1]))
                    {
                        int operatorEnd = i + 2;
                        while (operatorEnd < len && IsIdChar(text[operatorEnd]))
                        {
                            operatorEnd++;
                        }
                        if (PowerShellOperators.Contains(text.Substring(i, operatorEnd - i)))
                        {
                            Normal(ref last, last, i, sink);
                            sink.AddToken(TypeKeyword, i, operatorEnd);
                            i = operatorEnd;
                            last = i;
                            continue;
                        }
                    }

                    if (MarkupTags && c == '>')
                    {
                        inTag = false;
                        i++;
                        continue;
                    }

                    if (MarkupTags && c == '<' && i + 1 < len && (IsIdStart(text[i + 1]) || text[i + 1] == '/' || text[i + 1] == '?' || text[i + 1] == '!'))
                    {
                        int s = i;
                        inTag = true;
                        i++;
                        if (text[i] == '/' || text[i] == '?' || text[i] == '!')
                        {
                            i++;
                        }
                        while (i < len && (IsIdChar(text[i]) || text[i] == ':' || text[i] == '-' || text[i] == '.'))
                        {
                            i++;
                        }
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                        continue;
                    }

                    if (IsDigit(c))
                    {
                        int s = i;
                        while (i < len && (IsIdChar(text[i]) || (text[i] == '.' && i + 1 < len && IsDigit(text[i + 1]))))
                        {
                            i++;
                        }
                        // A numeric local label of the GNU assembler: "1:".
                        if (AssemblySyntax && i < len && text[i] == ':' && IsAtLineStart(text, s))
                        {
                            Normal(ref last, last, s, sink);
                            sink.AddToken(TypeSection, s, i + 1);
                            i++;
                            last = i;
                            continue;
                        }
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeNumber, s, i);
                        last = i;
                        continue;
                    }

                    if (IsIdStart(c))
                    {
                        int s = i;
                        while (i < len && (IsIdChar(text[i]) || (HyphenatedKeywords && text[i] == '-')))
                        {
                            i++;
                        }
                        string word = text.Substring(s, i - s);
                        // An Assembly label is the first word of a line followed by a single colon.
                        if (AssemblySyntax && i < len && text[i] == ':' &&
                            !(i + 1 < len && text[i + 1] == ':') && IsAtLineStart(text, s))
                        {
                            Normal(ref last, last, s, sink);
                            sink.AddToken(TypeSection, s, i + 1);
                            i++;
                            last = i;
                            continue;
                        }
                        if (AssemblySyntax && Registers != null && Registers.Contains(word))
                        {
                            Normal(ref last, last, s, sink);
                            sink.AddToken(TypeVariable, s, i);
                            last = i;
                            continue;
                        }
                        // A Device Tree label ("myboard:") or the name of a node with a unit address ("uart@...").
                        bool deviceTreeLabel = DeviceTreeSyntax && i < len && text[i] == ':' &&
                            !(i + 1 < len && text[i + 1] == ':');
                        bool deviceTreeNode = DeviceTreeSyntax && i < len && text[i] == '@';
                        if (deviceTreeLabel || deviceTreeNode)
                        {
                            Normal(ref last, last, s, sink);
                            sink.AddToken(TypeKeyword, s, i);
                            last = i;
                            continue;
                        }
                        if (RustLiterals && (word == "r" || word == "br") && i < len && (text[i] == '"' || text[i] == '#'))
                        {
                            // Raw string r"..." / r#"..."#: no escapes, the quotes inside need the same number of #.
                            int hashes = 0;
                            while (i + hashes < len && text[i + hashes] == '#')
                            {
                                hashes++;
                            }
                            if (i + hashes < len && text[i + hashes] == '"')
                            {
                                string closing = "\"" + new string('#', hashes);
                                int closeAt = text.IndexOf(closing, i + hashes + 1, StringComparison.Ordinal);
                                int rawEnd = (closeAt < 0) ? len : closeAt + closing.Length;
                                Normal(ref last, last, s, sink);
                                sink.AddToken(TypeString, s, rawEnd);
                                i = rawEnd;
                                last = i;
                                continue;
                            }
                        }
                        bool keyword = Keywords != null && Keywords.Contains(word);
                        if (!keyword && PowerShellSyntax && IsPowerShellCommand(word))
                        {
                            keyword = true;
                        }
                        if (keyword && KeywordsAtLineStart)
                        {
                            // Dockerfile instructions and CMake commands are the first word of a line;
                            // the same word in an argument is plain text.
                            int before = s - 1;
                            while (before >= 0 && char.IsWhiteSpace(text[before]))
                            {
                                before--;
                            }
                            keyword = before < 0;
                        }
                        if (keyword)
                        {
                            Normal(ref last, last, s, sink);
                            sink.AddToken(TypeKeyword, s, i);
                            last = i;
                        }
                        continue;
                    }

                    if (Brackets && TryAddBracket(text, i, ref last, sink, FoldCurlyBraces))
                    {
                        i++;
                        continue;
                    }

                    i++;
                }

                Normal(ref last, last, len, sink);
                endState = inTag ? StInTag : 0;
                return true;
            }
        }

        private static TokenCache.LineTokenizer CreateCLikeTokenizer(HashSet<string> keywords, bool preprocessor = false,
            bool slashComments = true, bool hashComments = false, bool backticks = false, bool tripleQuotes = false,
            bool verbatim = false, bool rustLiterals = false, bool hyphenatedKeywords = false,
            bool deviceTreeSyntax = false, HashSet<string> preprocessorWords = null)
        {
            return new GenericSyntax
            {
                SlashComments = slashComments,
                HashComments = hashComments,
                Preprocessor = preprocessor,
                PreprocessorWords = preprocessorWords ?? PreprocessorDirectives,
                Brackets = true,
                FoldCurlyBraces = true,
                BacktickStrings = backticks,
                TripleQuoteStrings = tripleQuotes,
                VerbatimStrings = verbatim,
                RustLiterals = rustLiterals,
                HyphenatedKeywords = hyphenatedKeywords,
                DeviceTreeSyntax = deviceTreeSyntax,
                BlockCommentStart = "/*",
                BlockCommentEnd = "*/",
                Keywords = keywords
            }.Tokenize;
        }

        private static readonly TokenCache.LineTokenizer CLikeTokenizer =
            CreateCLikeTokenizer(CKeywords, preprocessor: true);

        private static readonly TokenCache.LineTokenizer CSharpTokenizer =
            CreateCLikeTokenizer(CSharpKeywords, preprocessor: true, tripleQuotes: true, verbatim: true,
                preprocessorWords: CSharpPreprocessorDirectives);

        private static readonly TokenCache.LineTokenizer JavaTokenizer =
            CreateCLikeTokenizer(JavaKeywords, tripleQuotes: true);

        private static readonly TokenCache.LineTokenizer KotlinTokenizer =
            CreateCLikeTokenizer(KotlinKeywords, tripleQuotes: true);

        private static readonly TokenCache.LineTokenizer ScalaTokenizer =
            CreateCLikeTokenizer(ScalaKeywords, tripleQuotes: true);

        private static readonly TokenCache.LineTokenizer GroovyTokenizer =
            CreateCLikeTokenizer(GroovyKeywords, tripleQuotes: true);

        private static readonly TokenCache.LineTokenizer GoTokenizer =
            CreateCLikeTokenizer(GoKeywords, backticks: true);

        private static readonly TokenCache.LineTokenizer RustTokenizer =
            CreateCLikeTokenizer(RustKeywords, rustLiterals: true);

        private static readonly TokenCache.LineTokenizer VerilogTokenizer = new GenericSyntax
        {
            SlashComments = true,
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            // Verilog uses the apostrophe in based literals (4'b1010), not as a string delimiter.
            SingleQuoteStrings = false,
            BacktickDirectives = true,
            PreprocessorWords = VerilogDirectives,
            DollarIdentifiers = true,
            Brackets = true,
            Keywords = VerilogKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer VhdlTokenizer = new GenericSyntax
        {
            DashDashComments = true,
            // VHDL attributes use the apostrophe (clk'event), so only "..." is a string.
            SingleQuoteStrings = false,
            Brackets = true,
            Keywords = VhdlKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer SwiftTokenizer =
            CreateCLikeTokenizer(SwiftKeywords, tripleQuotes: true);

        private static readonly TokenCache.LineTokenizer DartTokenizer =
            CreateCLikeTokenizer(DartKeywords, tripleQuotes: true);

        private static readonly TokenCache.LineTokenizer JavaScriptTokenizer =
            CreateCLikeTokenizer(JavaScriptKeywords, backticks: true);

        private static readonly TokenCache.LineTokenizer TypeScriptTokenizer =
            CreateCLikeTokenizer(TypeScriptKeywords, backticks: true);

        private static readonly TokenCache.LineTokenizer PhpTokenizer =
            CreateCLikeTokenizer(PhpKeywords, hashComments: true, backticks: true);

        private static readonly TokenCache.LineTokenizer CssTokenizer =
            CreateCLikeTokenizer(CssKeywords, slashComments: false, hyphenatedKeywords: true);

        private static readonly TokenCache.LineTokenizer ProtobufTokenizer =
            CreateCLikeTokenizer(ProtobufKeywords);

        private static readonly TokenCache.LineTokenizer DeviceTreeTokenizer =
            CreateCLikeTokenizer(DeviceTreeKeywords, preprocessor: true, hyphenatedKeywords: true, deviceTreeSyntax: true);

        private static readonly TokenCache.LineTokenizer CppTokenizer = new GenericSyntax
        {
            SlashComments = true,
            Preprocessor = true,
            Brackets = true,
            FoldCurlyBraces = true,
            BacktickStrings = true,
            TripleQuoteStrings = true,
            VerbatimStrings = true,
            RustLiterals = true,
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            Keywords = CppKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer ScriptTokenizer = new GenericSyntax
        {
            HashComments = true,
            Keywords = ScriptKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer AwkTokenizer = new GenericSyntax
        {
            HashComments = true,
            // AWK has no single-quoted strings: an apostrophe is an ordinary character.
            SingleQuoteStrings = false,
            Brackets = true,
            FoldCurlyBraces = true,
            Keywords = AwkKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer TclTokenizer = new GenericSyntax
        {
            HashComments = true,
            // Tcl has no single-quoted strings: an apostrophe is an ordinary character.
            SingleQuoteStrings = false,
            Brackets = true,
            FoldCurlyBraces = true,
            Keywords = TclKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer AssemblyTokenizer = new GenericSyntax
        {
            // NASM uses ';', GNU as uses '#', ARM assembly uses '@'; '//' and '/* */' are also common.
            // '#' counts only at the start of a line: elsewhere it introduces an immediate ('#1').
            SemicolonComments = true,
            HashCommentsAtLineStart = true,
            AtComments = true,
            SlashComments = true,
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            AssemblySyntax = true,
            Brackets = true,
            Keywords = AssemblyKeywords,
            Registers = AssemblyRegisters
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer PowerShellTokenizer = new GenericSyntax
        {
            // PowerShell comments and strings; commands are Verb-Noun words, operators start with '-'.
            HashComments = true,
            HashCommentsAnywhere = true,
            BlockCommentStart = "<#",
            BlockCommentEnd = "#>",
            PowerShellSyntax = true,
            HyphenatedKeywords = true,
            Brackets = true,
            FoldCurlyBraces = true,
            Keywords = PowerShellKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer RubyTokenizer = new GenericSyntax
        {
            HashComments = true,
            HashCommentsAnywhere = true,
            Brackets = true,
            FoldCurlyBraces = true,
            Keywords = RubyKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer PerlTokenizer = new GenericSyntax
        {
            HashComments = true,
            HashCommentsAnywhere = true,
            Brackets = true,
            FoldCurlyBraces = true,
            Keywords = PerlKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer CMakeTokenizer = new GenericSyntax
        {
            HashComments = true,
            HashCommentsAnywhere = true,
            KeywordsAtLineStart = true,
            Brackets = true,
            Keywords = CMakeKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer DockerfileTokenizer = new GenericSyntax
        {
            HashComments = true,
            KeywordsAtLineStart = true,
            Keywords = DockerfileKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer SqlTokenizer = new GenericSyntax
        {
            DashDashComments = true,
            Brackets = true,
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            Keywords = SqlKeywords
        }.Tokenize;

        private static readonly TokenCache.LineTokenizer MarkupTokenizer = new GenericSyntax
        {
            // Apostrophes are common in text content, so only "..." is a string.
            SingleQuoteStrings = false,
            MarkupTags = true,
            BlockCommentStart = "<!--",
            BlockCommentEnd = "-->"
        }.Tokenize;

        // ==================================================================
        // Block keywords: fold regions of "then ... fi", "do ... done", "function ... end"
        // ==================================================================

        // Pair ids of keyword blocks (bracket ids are 1..3).
        private const int PairThenBlock = 4;     // Bash then...fi, Lua then/do/function...end
        private const int PairCaseBlock = 5;     // Bash case ... in ... esac
        private const int PairRepeatBlock = 6;   // Lua repeat...until
        private const int PairLoopBlock = 7;     // Bash do...done
        private const int PairConditional = 8;   // Makefile ifeq...endif
        private const int PairDefineBlock = 9;   // Makefile define...endef
        private const int PairConfigBlock = 10;  // UCI "config" block up to the blank line

        /// <summary>The fold of a keyword block: "if x; then...fi". The text after the closing keyword stays visible.</summary>
        private static readonly TokenCache.RegionStyle KeywordBlockRegion = new TokenCache.RegionStyle(
            TokenCache.RegionStyle.FoldBoundary.TokenEnd, TokenCache.RegionStyle.FoldBoundary.TokenEnd, unusedFlag: false,
            TokenCache.RegionStyle.LabelSource.FixedText, "...", openingOnNextLine: false, trailingBlankLines: 0);

        private static void AddKeywordPair(string text, int start, int end, int pairId, bool isOpening, ref int last, TokenCache.ITokenSink sink)
        {
            Normal(ref last, last, start, sink);
            sink.AddPairedToken(TypeKeyword, start, end, pairId, isOpening, isOpening ? KeywordBlockRegion : null);
            last = end;
        }

        /// <summary>Opens a block at the end of the code on the line (a zero-width token): "function f(a)" ... "end".</summary>
        private static void AddEndOfLineOpener(string text, int codeEnd, int pairId, TokenCache.ITokenSink sink, TokenCache.RegionStyle style = null)
        {
            int end = codeEnd;
            while (end > 0 && IsBlank(text[end - 1]))
            {
                end--;
            }
            sink.AddPairedToken(TypeNormal, end, end, pairId, true, style ?? KeywordBlockRegion);
        }

        /// <summary>
        /// Collects the tokens of one line so that brackets that are not closed on the same line can be dropped:
        /// shell code is full of unbalanced ")" (case patterns) and "[" (globs) that would break the pairing
        /// of the enclosing blocks.
        /// </summary>
        private sealed class LineBuffer : TokenCache.ITokenSink
        {
            private struct Item
            {
                public int Type, Start, End, PairId;
                public bool IsPaired, IsOpening, Keep;
                public TokenCache.RegionStyle Style;
            }

            private readonly TokenCache.ITokenSink _target;
            private readonly List<Item> _items = new List<Item>();

            public LineBuffer(TokenCache.ITokenSink target)
            {
                _target = target;
            }

            public int CurrentLine => _target.CurrentLine;

            public bool IsLastLine => _target.IsLastLine;

            public void AddToken(int type, int start, int end)
            {
                _items.Add(new Item { Type = type, Start = start, End = end, Keep = true });
            }

            /// <summary>Adds a zero-width paired token in front of the tokens that start at or after the position.</summary>
            public void InsertPairedToken(int position, int pairId, bool isOpening, TokenCache.RegionStyle regionStyle)
            {
                int index = 0;
                while (index < _items.Count && _items[index].Start < position)
                {
                    index++;
                }
                _items.Insert(index, new Item { Type = TypeNormal, Start = position, End = position, PairId = pairId, IsPaired = true, IsOpening = isOpening, Style = regionStyle, Keep = true });
            }

            public void AddPairedToken(int type, int start, int end, int pairId, bool isOpening, TokenCache.RegionStyle regionStyle)
            {
                _items.Add(new Item { Type = type, Start = start, End = end, PairId = pairId, IsPaired = true, IsOpening = isOpening, Style = regionStyle, Keep = true });
            }

            /// <summary>Sends the tokens to the target; brackets with a "local" pair id need a partner on this line.</summary>
            public void Flush(int localPairIdA, int localPairIdB)
            {
                List<int> stackA = new List<int>();
                List<int> stackB = new List<int>();
                for (int n = 0; n < _items.Count; n++)
                {
                    Item item = _items[n];
                    if (!item.IsPaired || (item.PairId != localPairIdA && item.PairId != localPairIdB))
                    {
                        continue;
                    }
                    List<int> stack = (item.PairId == localPairIdA) ? stackA : stackB;
                    if (item.IsOpening)
                    {
                        item.Keep = false;
                        _items[n] = item;
                        stack.Add(n);
                    }
                    else if (stack.Count > 0)
                    {
                        int open = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        Item opening = _items[open];
                        opening.Keep = true;
                        _items[open] = opening;
                    }
                    else
                    {
                        item.Keep = false;
                        _items[n] = item;
                    }
                }
                foreach (Item item in _items)
                {
                    if (!item.Keep)
                    {
                        continue;
                    }
                    if (item.IsPaired)
                    {
                        _target.AddPairedToken(item.Type, item.Start, item.End, item.PairId, item.IsOpening, item.Style);
                    }
                    else
                    {
                        _target.AddToken(item.Type, item.Start, item.End);
                    }
                }
            }
        }

        // ==================================================================
        // Lua
        // ==================================================================

        // Line state: 0x10 - inside a long comment --[==[ ... ]==], 0x20 - inside a long string [==[ ... ]==];
        // the level (number of "=") is stored in bits 8 and up.
        private const int StLuaComment = 0x10;
        private const int StLuaString = 0x20;

        private static readonly HashSet<string> LuaKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
            "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"
        };

        /// <summary>Level of the long bracket "[==[" that starts at index, or -1.</summary>
        private static int LuaLongBracketLevel(string text, int index)
        {
            if (index >= text.Length || text[index] != '[')
            {
                return -1;
            }
            int j = index + 1;
            int level = 0;
            while (j < text.Length && text[j] == '=')
            {
                level++;
                j++;
            }
            return (j < text.Length && text[j] == '[') ? level : -1;
        }

        /// <summary>Index after the closing "]==]" of the given level, or -1.</summary>
        private static int FindLuaLongClose(string text, int from, int level)
        {
            string close = "]" + new string('=', level) + "]";
            int position = text.IndexOf(close, from, StringComparison.Ordinal);
            return (position < 0) ? -1 : (position + close.Length);
        }

        private static bool TokenizeLua(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;
            int openFunctions = 0;   // "function" keywords on this line that still wait for their "end"
            bool afterElseif = false;

            if ((startState & (StLuaComment | StLuaString)) != 0)
            {
                int level = startState >> 8;
                int type = ((startState & StLuaComment) != 0) ? TypeComment : TypeString;
                int close = FindLuaLongClose(text, 0, level);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(type, 0, len);
                    }
                    endState = startState;
                    return true;
                }
                sink.AddToken(type, 0, close);
                i = close;
                last = i;
            }
            else if (TryAddShebang(text, sink))
            {
                endState = 0;
                return true;
            }

            while (i < len)
            {
                char c = text[i];

                // Comments: "-- text" and "--[==[ long ]==]".
                if (c == '-' && i + 1 < len && text[i + 1] == '-')
                {
                    for (; openFunctions > 0; openFunctions--)
                    {
                        AddEndOfLineOpener(text, i, PairThenBlock, sink);
                    }
                    Normal(ref last, last, i, sink);
                    int level = LuaLongBracketLevel(text, i + 2);
                    if (level >= 0)
                    {
                        int close = FindLuaLongClose(text, i + level + 4, level);
                        if (close < 0)
                        {
                            sink.AddToken(TypeComment, i, len);
                            endState = StLuaComment | (level << 8);
                            return true;
                        }
                        sink.AddToken(TypeComment, i, close);
                        i = close;
                        last = i;
                        continue;
                    }
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                // Long strings [[ ... ]] and [==[ ... ]==].
                if (c == '[')
                {
                    int level = LuaLongBracketLevel(text, i);
                    if (level >= 0)
                    {
                        Normal(ref last, last, i, sink);
                        int close = FindLuaLongClose(text, i + level + 2, level);
                        if (close < 0)
                        {
                            sink.AddToken(TypeString, i, len);
                            endState = StLuaString | (level << 8);
                            return true;
                        }
                        sink.AddToken(TypeString, i, close);
                        i = close;
                        last = i;
                        continue;
                    }
                }

                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                if (IsDigit(c) || (c == '.' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    while (i < len && (IsIdChar(text[i]) || (text[i] == '.' && !(i + 1 < len && text[i + 1] == '.')) ||
                        ((text[i] == '+' || text[i] == '-') && i > s && "eEpP".IndexOf(text[i - 1]) >= 0)))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    string word = text.Substring(s, i - s);
                    // "t.end" and "obj:then" are fields, not keywords.
                    if (!LuaKeywords.Contains(word) || (s > 0 && (text[s - 1] == '.' || text[s - 1] == ':')))
                    {
                        continue;
                    }
                    switch (word)
                    {
                        case "function":
                            openFunctions++;
                            break;
                        case "elseif":
                            afterElseif = true;
                            break;
                        case "then":
                            if (afterElseif)
                            {
                                afterElseif = false;
                                break;
                            }
                            AddKeywordPair(text, s, i, PairThenBlock, true, ref last, sink);
                            continue;
                        case "do":
                            AddKeywordPair(text, s, i, PairThenBlock, true, ref last, sink);
                            continue;
                        case "repeat":
                            AddKeywordPair(text, s, i, PairRepeatBlock, true, ref last, sink);
                            continue;
                        case "until":
                            AddKeywordPair(text, s, i, PairRepeatBlock, false, ref last, sink);
                            continue;
                        case "end":
                            if (openFunctions > 0)
                            {
                                // "function() ... end" on one line: the block is closed where it was opened.
                                openFunctions--;
                                break;
                            }
                            AddKeywordPair(text, s, i, PairThenBlock, false, ref last, sink);
                            continue;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeKeyword, s, i);
                    last = i;
                    continue;
                }

                if (TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true))
                {
                    i++;
                    continue;
                }

                i++;
            }

            for (; openFunctions > 0; openFunctions--)
            {
                AddEndOfLineOpener(text, len, PairThenBlock, sink);
            }
            Normal(ref last, last, len, sink);
            endState = 0;
            return true;
        }

        // ==================================================================
        // JSON (comments are tolerated, so .jsonc files work too)
        // ==================================================================

        private static bool TokenizeJson(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int i = 0;
            int last = 0;

            if (startState == StBlockComment)
            {
                int close = text.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0)
                {
                    if (len > 0)
                    {
                        sink.AddToken(TypeComment, 0, len);
                    }
                    endState = StBlockComment;
                    return true;
                }
                i = close + 2;
                sink.AddToken(TypeComment, 0, i);
                last = i;
            }

            while (i < len)
            {
                char c = text[i];

                if (c == '/' && i + 1 < len && text[i + 1] == '/')
                {
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    endState = 0;
                    return true;
                }

                if (c == '/' && i + 1 < len && text[i + 1] == '*')
                {
                    Normal(ref last, last, i, sink);
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        sink.AddToken(TypeComment, i, len);
                        endState = StBlockComment;
                        return true;
                    }
                    sink.AddToken(TypeComment, i, close + 2);
                    i = close + 2;
                    last = i;
                    continue;
                }

                if (c == '"')
                {
                    int end = FindQuoteEnd(text, i, '"');
                    int stop = (end < 0) ? len : end;
                    // A string followed by ':' is an object key.
                    bool isKey = SkipBlanks(text, stop) < len && text[SkipBlanks(text, stop)] == ':';
                    Normal(ref last, last, i, sink);
                    sink.AddToken(isKey ? TypeVariable : TypeString, i, stop);
                    i = stop;
                    last = i;
                    continue;
                }

                if (IsDigit(c) || (c == '-' && i + 1 < len && IsDigit(text[i + 1])))
                {
                    int s = i;
                    i++;
                    while (i < len && (IsIdChar(text[i]) || text[i] == '.' ||
                        ((text[i] == '+' || text[i] == '-') && (text[i - 1] == 'e' || text[i - 1] == 'E'))))
                    {
                        i++;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                    continue;
                }

                if (IsIdStart(c))
                {
                    int s = i;
                    while (i < len && IsIdChar(text[i]))
                    {
                        i++;
                    }
                    string word = text.Substring(s, i - s);
                    if (word == "true" || word == "false" || word == "null")
                    {
                        Normal(ref last, last, s, sink);
                        sink.AddToken(TypeKeyword, s, i);
                        last = i;
                    }
                    continue;
                }

                if (TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true, foldSquareBrackets: true))
                {
                    i++;
                    continue;
                }

                i++;
            }

            Normal(ref last, last, len, sink);
            endState = 0;
            return true;
        }

        // ==================================================================
        // Makefile
        // ==================================================================

        private static readonly HashSet<string> MakeDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "include", "-include", "sinclude", "export", "unexport", "override", "vpath", "undefine",
            "else", "ifeq", "ifneq", "ifdef", "ifndef", "endif", "define", "endef"
        };

        /// <summary>Index after the closing bracket of "$(...)" / "${...}" that starts at index, or the end of the line.</summary>
        private static int FindMakeReferenceEnd(string text, int index)
        {
            char open = text[index + 1];
            char close = (open == '(') ? ')' : '}';
            int depth = 0;
            for (int j = index + 1; j < text.Length; j++)
            {
                if (text[j] == open)
                {
                    depth++;
                }
                else if (text[j] == close && --depth == 0)
                {
                    return j + 1;
                }
            }
            return text.Length;
        }

        // Line state: 0x01 - inside a rule (its recipe lines follow), 0x02 - inside "define ... endef".
        private const int StMakeRule = 0x01;
        private const int StMakeDefine = 0x02;

        private static bool TokenizeMakefile(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int state = startState & (StMakeRule | StMakeDefine);
            bool result = TokenizeMakefileLine(ref state, text, sink);
            // The end of the document ends the rule that is open.
            if ((state & StMakeRule) != 0 && sink.IsLastLine)
            {
                sink.AddPairedToken(TypeNormal, text.Length, text.Length, PairMakeRule, false, null);
                state &= ~StMakeRule;
            }
            endState = state;
            return result;
        }

        private static bool TokenizeMakefileLine(ref int state, string text, TokenCache.ITokenSink sink)
        {
            int len = text.Length;
            int last = 0;
            int i = 0;
            int pendingOpener = 0;  // pair id of a block that opens at the end of this line
            bool inDefine = (state & StMakeDefine) != 0;

            if (TryAddShebang(text, sink))
            {
                state = 0;
                return true;
            }

            bool recipe = len > 0 && text[0] == '\t';
            int first = SkipBlanks(text, 0);

            // A rule goes on while its recipe lines (they start with a tab) follow; a blank line or any other line ends it.
            if ((state & StMakeRule) != 0 && !inDefine && (first == len || !recipe))
            {
                sink.AddPairedToken(TypeNormal, 0, 0, PairMakeRule, false, null);
                state &= ~StMakeRule;
            }

            if (!recipe && first < len)
            {
                if (text[first] == '#')
                {
                    sink.AddToken(TypeComment, first, len);
                    return true;
                }

                int wordEnd = first;
                while (wordEnd < len && (IsIdChar(text[wordEnd]) || (wordEnd == first && text[wordEnd] == '-')))
                {
                    wordEnd++;
                }
                string word = text.Substring(first, wordEnd - first);
                bool isDirective = MakeDirectives.Contains(word) && (wordEnd == len || IsBlank(text[wordEnd]) || text[wordEnd] == '(');
                if (inDefine && word != "endef")
                {
                    isDirective = false;
                }
                if (isDirective)
                {
                    switch (word)
                    {
                        case "ifeq":
                        case "ifneq":
                        case "ifdef":
                        case "ifndef":
                            pendingOpener = PairConditional;
                            Normal(ref last, last, first, sink);
                            sink.AddToken(TypeKeyword, first, wordEnd);
                            break;
                        case "define":
                            pendingOpener = PairDefineBlock;
                            state |= StMakeDefine;
                            Normal(ref last, last, first, sink);
                            sink.AddToken(TypeKeyword, first, wordEnd);
                            break;
                        case "endif":
                            AddKeywordPair(text, first, wordEnd, PairConditional, false, ref last, sink);
                            break;
                        case "endef":
                            state &= ~StMakeDefine;
                            AddKeywordPair(text, first, wordEnd, PairDefineBlock, false, ref last, sink);
                            break;
                        default:
                            Normal(ref last, last, first, sink);
                            sink.AddToken(TypeKeyword, first, wordEnd);
                            break;
                    }
                    last = wordEnd;
                    i = wordEnd;
                }
                else if (!inDefine)
                {
                    // "VAR = value", "VAR := value", "VAR ?= value" and "target: deps".
                    int j = first;
                    int depth = 0;
                    while (j < len)
                    {
                        char c = text[j];
                        if (c == '$' && j + 1 < len && (text[j + 1] == '(' || text[j + 1] == '{'))
                        {
                            j = FindMakeReferenceEnd(text, j);
                            continue;
                        }
                        if (c == '#' || c == '"' || c == '\'')
                        {
                            j = len;
                            break;
                        }
                        if (depth == 0 && (c == '=' || c == ':'))
                        {
                            break;
                        }
                        j++;
                    }
                    if (j < len)
                    {
                        int keyEnd = j;
                        bool assignment = text[j] == '=';
                        if (text[j] == ':' && j + 1 < len && (text[j + 1] == '=' || (text[j + 1] == ':' && j + 2 < len && text[j + 2] == '=')))
                        {
                            assignment = true;
                        }
                        if (assignment)
                        {
                            while (keyEnd > first && ("+?!:".IndexOf(text[keyEnd - 1]) >= 0 || IsBlank(text[keyEnd - 1])))
                            {
                                keyEnd--;
                            }
                        }
                        else
                        {
                            while (keyEnd > first && IsBlank(text[keyEnd - 1]))
                            {
                                keyEnd--;
                            }
                        }
                        string name = text.Substring(first, keyEnd - first);
                        if (keyEnd > first && !assignment)
                        {
                            // The recipe lines below the rule fold.
                            pendingOpener = PairMakeRule;
                            state |= StMakeRule;
                        }
                        if (keyEnd > first && name.IndexOf('$') < 0)
                        {
                            Normal(ref last, last, first, sink);
                            sink.AddToken(assignment ? TypeVariable : TypeSection, first, keyEnd);
                            last = keyEnd;
                            i = keyEnd;
                        }
                    }
                }
            }

            while (i < len)
            {
                char c = text[i];

                if (c == '#' && (i == 0 || IsBlank(text[i - 1]) || recipe))
                {
                    if (pendingOpener != 0)
                    {
                        AddEndOfLineOpener(text, i, pendingOpener, sink, (pendingOpener == PairMakeRule) ? ExclusiveBlockRegion : null);
                        pendingOpener = 0;
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeComment, i, len);
                    return true;
                }

                if (c == '$' && i + 1 < len)
                {
                    int e;
                    if (text[i + 1] == '(' || text[i + 1] == '{')
                    {
                        e = FindMakeReferenceEnd(text, i);
                    }
                    else
                    {
                        e = i + 2;   // $@ $< $^ $* $? $$ $1
                    }
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeVariable, i, e);
                    i = e;
                    last = i;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    int end = FindQuoteEnd(text, i, c);
                    Normal(ref last, last, i, sink);
                    sink.AddToken(TypeString, i, (end < 0) ? len : end);
                    i = (end < 0) ? len : end;
                    last = i;
                    continue;
                }

                i++;
            }

            if (pendingOpener != 0)
            {
                AddEndOfLineOpener(text, len, pendingOpener, sink, (pendingOpener == PairMakeRule) ? ExclusiveBlockRegion : null);
            }
            Normal(ref last, last, len, sink);
            return true;
        }

        // ==================================================================
        // Blocks that follow the indentation (Python, YAML) and blocks that end at the next header
        // ==================================================================

        private const int PairIndentBlock = 11;  // Python def/class/if..., YAML "key:" and "- item"
        private const int PairMakeRule = 12;     // Makefile "target:" with its recipe

        /// <summary>
        /// The fold of a block that has no closing keyword: its closing token is a zero-width token at the start of the
        /// first line after the block, and that line stays visible: "def f(x):..." / "config interface 'lan'...".
        /// </summary>
        private static readonly TokenCache.RegionStyle ExclusiveBlockRegion = new TokenCache.RegionStyle(
            TokenCache.RegionStyle.FoldBoundary.TokenEnd, TokenCache.RegionStyle.FoldBoundary.TokenEnd, unusedFlag: false,
            TokenCache.RegionStyle.LabelSource.FixedText, "...", openingOnNextLine: false, trailingBlankLines: 0,
            endsBeforeClosingLine: true);

        /// <summary>The width of the indentation of a line in columns; a tab counts as 4.</summary>
        private static int IndentColumns(string text, int firstNonBlank)
        {
            int columns = 0;
            for (int i = 0; i < firstNonBlank; i++)
            {
                columns += (text[i] == '\t') ? 4 : 1;
            }
            return columns;
        }

        /// <summary>The indentation of the innermost of <paramref name="count"/> open blocks; every block stores its increase in 4 bits.</summary>
        private static int IndentLevelSum(int levels, int count, int deltaMask)
        {
            int sum = 0;
            for (int level = 0; level < count && level < 8; level++)
            {
                sum += (levels >> (4 * level)) & deltaMask;
            }
            return sum;
        }

        private const int PyMaxLevels = 5;
        private const int StPyStringMask = 0x03;
        private const int PyDepthShift = 2;     // 3 bits
        private const int PyBracketShift = 5;   // 2 bits
        private const int PyHeaderShift = 7;    // 5 bits
        private const int PyLevelShift = 12;    // 5 levels of 4 bits

        private static readonly HashSet<string> PyBlockKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "def", "class", "if", "elif", "else", "for", "while", "try", "except", "finally", "with", "async", "match", "case"
        };

        /// <summary>Whether the statement that starts at index starts with a keyword that opens a block.</summary>
        private static bool IsPythonBlockHeader(string text, int index)
        {
            int end = index;
            while (end < text.Length && IsIdChar(text[end]))
            {
                end++;
            }
            return end > index && PyBlockKeywords.Contains(text.Substring(index, end - index));
        }

        // YAML line state: bits 0-2 - open blocks (up to 6), then 4 bits per block:
        // bits 0-2 the increase of the indentation, bit 3 - the block is a "key:" (a list may sit at the same indentation).
        private const int YamlMaxLevels = 6;
        private const int YamlLevelShift = 3;

        // The innermost open block is a block scalar ("key: |"): its lines, indented deeper than the header, are text.
        private const int StYamlScalar = 1 << 30;

        /// <summary>The end of the code of a YAML line (the comment and the blanks before it are not included).</summary>
        private static int YamlCodeEnd(string text, int first)
        {
            char quote = '\0';
            int end = text.Length;
            for (int i = first; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if ((c == '"' || c == '\'') && (i == first || IsBlank(text[i - 1]) || text[i - 1] == ':' || text[i - 1] == '-' || text[i - 1] == '[' || text[i - 1] == '{' || text[i - 1] == ','))
                {
                    quote = c;
                }
                else if (c == '#' && (i == first || IsBlank(text[i - 1])))
                {
                    end = i;
                    break;
                }
            }
            while (end > first && IsBlank(text[end - 1]))
            {
                end--;
            }
            return end;
        }

        /// <summary>Whether the text contains "key:" (a colon followed by a blank or the end) outside quotes.</summary>
        private static bool HasYamlKey(string code, int start)
        {
            char quote = '\0';
            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == ':' && (i + 1 == code.Length || IsBlank(code[i + 1])))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>"key: |", "key: >-", "- |2": the text of a block scalar follows on the next lines.</summary>
        private static bool IsYamlBlockScalarHeader(string code)
        {
            int space = code.LastIndexOf(' ');
            int wordStart = space + 1;
            if (wordStart >= code.Length || (code[wordStart] != '|' && code[wordStart] != '>'))
            {
                return false;
            }
            for (int i = wordStart + 1; i < code.Length; i++)
            {
                if (code[i] != '+' && code[i] != '-' && !IsDigit(code[i]))
                {
                    return false;
                }
            }
            string before = code.Substring(0, Math.Max(0, space)).TrimEnd();
            return before.EndsWith(":", StringComparison.Ordinal) || before == "-";
        }

        /// <summary>YAML: the colors of the config tokenizer, and a fold for every "key:", "- key: value" and block scalar.</summary>
        private static bool TokenizeYaml(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int depth = startState & 7;
            int levels = (startState >> YamlLevelShift) & 0xFFFFFF;
            bool scalar = (startState & StYamlScalar) != 0;
            LineBuffer buffer = new LineBuffer(sink);
            int first = SkipBlanks(text, 0);
            bool code = first < len && text[first] != '#';

            // The text of a block scalar: no keys, comments or quotes in it, whatever it looks like. A blank line
            // belongs to it; a line that is not indented deeper than the header ends it.
            bool scalarText = false;
            if (scalar)
            {
                if (first == len || IndentColumns(text, first) > IndentLevelSum(levels, depth, 0x7))
                {
                    scalarText = true;
                    code = false;
                    if (first < len)
                    {
                        buffer.AddToken(TypeString, first, len);
                    }
                }
                else
                {
                    scalar = false;
                }
            }

            int codeEnd = len;
            bool isItem = false;
            if (code)
            {
                int indent = IndentColumns(text, first);
                isItem = text[first] == '-' && (first + 1 == len || IsBlank(text[first + 1]));
                // The blocks indented as deep or deeper than this line end here; a list at the indentation of its "key:" belongs to the key.
                while (depth > 0)
                {
                    int cumulative = IndentLevelSum(levels, depth, 0x7);
                    if (cumulative < indent)
                    {
                        break;
                    }
                    bool keyBlock = ((levels >> (4 * (depth - 1))) & 8) != 0;
                    if (isItem && keyBlock && cumulative == indent)
                    {
                        break;
                    }
                    buffer.AddPairedToken(TypeNormal, 0, 0, PairIndentBlock, false, null);
                    levels &= ~(0xF << (4 * (depth - 1)));
                    depth--;
                }
            }

            if (!scalarText)
            {
                TokenizeConfig(0, text, buffer, out int ignored);
            }

            if (code)
            {
                codeEnd = YamlCodeEnd(text, first);
                string statement = text.Substring(first, codeEnd - first);
                bool endsWithColon = statement.EndsWith(":", StringComparison.Ordinal);
                bool opensBlock = endsWithColon || IsYamlBlockScalarHeader(statement) || (isItem && HasYamlKey(statement, 1));
                if (opensBlock && depth < YamlMaxLevels)
                {
                    int indent = IndentColumns(text, first);
                    int cumulative = IndentLevelSum(levels, depth, 0x7);
                    int delta = Math.Max(0, Math.Min(7, indent - cumulative));
                    int flag = (endsWithColon && !isItem) ? 8 : 0;
                    levels |= (delta | flag) << (4 * depth);
                    depth++;
                    scalar = IsYamlBlockScalarHeader(statement);
                    buffer.InsertPairedToken(codeEnd, PairIndentBlock, true, ExclusiveBlockRegion);
                }
            }

            buffer.Flush(-1, -2);
            if (sink.IsLastLine)
            {
                for (; depth > 0; depth--)
                {
                    sink.AddPairedToken(TypeNormal, len, len, PairIndentBlock, false, null);
                    levels &= ~(0xF << (4 * (depth - 1)));
                }
            }
            endState = depth | (levels << YamlLevelShift) | (scalar && depth > 0 ? StYamlScalar : 0);
            return true;
        }

        // ==================================================================
        // UCI (OpenWrt /etc/config/*)
        // ==================================================================

        // Line state: 0x01 - inside a "config" block, which ends at the first blank line.
        private const int StUciInBlock = 0x01;

        private static bool TokenizeUci(int startState, string text, TokenCache.ITokenSink sink, out int endState)
        {
            int len = text.Length;
            int first = SkipBlanks(text, 0);
            bool inBlock = (startState & StUciInBlock) != 0;
            string word = null;
            int wordEnd = first;
            if (first < len && text[first] != '#')
            {
                while (wordEnd < len && !IsBlank(text[wordEnd]))
                {
                    wordEnd++;
                }
                word = text.Substring(first, wordEnd - first);
            }
            bool isConfig = word == "config";
            bool isStatement = isConfig || word == "option" || word == "list" || word == "package";

            // A blank line, the next "config" or "package" ends the block that is open: it stays visible below "config x...".
            if (inBlock && (first == len || isConfig || word == "package"))
            {
                sink.AddPairedToken(TypeNormal, 0, 0, PairConfigBlock, false, null);
                inBlock = false;
            }

            if (isStatement)
            {
                int last = 0;
                Normal(ref last, last, first, sink);
                sink.AddToken(TypeKeyword, first, wordEnd);
                last = wordEnd;

                // Arguments: "config <type> [name]", "option <name> <value>", "list <name> <value>".
                int i = SkipBlanks(text, wordEnd);
                int argument = 0;
                while (i < len)
                {
                    char c = text[i];
                    if (c == '#')
                    {
                        break;
                    }
                    int s = i;
                    int e;
                    if (c == '"' || c == '\'')
                    {
                        int close = FindQuoteEnd(text, i, c);
                        e = (close < 0) ? len : close;
                    }
                    else
                    {
                        e = i;
                        while (e < len && !IsBlank(text[e]))
                        {
                            e++;
                        }
                    }
                    string value = text.Substring(s, e - s);
                    int type;
                    if (argument == 0 && word != "package")
                    {
                        type = isConfig ? TypeSection : TypeVariable;
                    }
                    else if (c == '"' || c == '\'')
                    {
                        type = TypeString;
                    }
                    else
                    {
                        long number;
                        type = long.TryParse(value, out number) ? TypeNumber : TypeString;
                    }
                    Normal(ref last, last, s, sink);
                    sink.AddToken(type, s, e);
                    last = e;
                    argument++;
                    i = SkipBlanks(text, e);
                }

                if (isConfig)
                {
                    AddEndOfLineOpener(text, (i < len) ? i : len, PairConfigBlock, sink, ExclusiveBlockRegion);
                    inBlock = true;
                }
                if (i < len)
                {
                    sink.AddToken(TypeComment, i, len);
                }
            }
            else if (first < len && text[first] == '#')
            {
                sink.AddToken(TypeComment, first, len);
            }

            // The end of the document ends the block too.
            if (inBlock && sink.IsLastLine)
            {
                sink.AddPairedToken(TypeNormal, len, len, PairConfigBlock, false, null);
                inBlock = false;
            }
            endState = inBlock ? StUciInBlock : 0;
            return true;
        }
    }
}
