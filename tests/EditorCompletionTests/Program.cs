using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Regression harness for the editor autocompletion word list:
//  * the per-language keyword sets exposed by SyntaxHighlighting.GetKeywords,
//  * the completion source (keywords + the identifiers of the document, prefix filter, ordering).
// Run with: dotnet run --project tests/EditorCompletionTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static List<string> Names(L language, string text, string prefix)
    {
        return EditorCompletionSource.Build(language, text, prefix).Select(s => s.DisplayName).ToList();
    }

    private static void KeywordChecks()
    {
        // Every highlighted language offers its words; plain text and diff have none.
        foreach (L language in SyntaxHighlighting.SelectableLanguages)
        {
            // Plain and Diff have no words of their own; Markup (HTML/XML) and Markdown have no fixed keyword set
            // either (their tags and constructs are free-form), so they rely on the identifiers found in the document.
            bool expected = language != L.Plain && language != L.Diff && language != L.Markup && language != L.Markdown;
            bool hasWords = SyntaxHighlighting.GetKeywords(language).Count > 0;
            Equal(hasWords, expected, "GetKeywords(" + language + ") " + (expected ? "is not empty" : "is empty"));
        }

        Check(SyntaxHighlighting.GetKeywords(L.CLike).Contains("if"), "C-like keywords contain 'if'");
        Check(SyntaxHighlighting.GetKeywords(L.CLike).Contains("include"), "C-like keywords contain the preprocessor 'include'");
        Check(SyntaxHighlighting.GetKeywords(L.Python).Contains("def"), "Python keywords contain 'def'");
        Check(SyntaxHighlighting.GetKeywords(L.Python).Contains("match"), "Python block keywords are merged in ('match')");
        Check(SyntaxHighlighting.GetKeywords(L.Lua).Contains("elseif"), "Lua keywords contain 'elseif'");
        Check(SyntaxHighlighting.GetKeywords(L.Bash).Contains("fi"), "Bash keywords contain 'fi'");
        Check(SyntaxHighlighting.GetKeywords(L.Bash).Contains("coproc"), "Bash keywords contain 'coproc'");
        Check(SyntaxHighlighting.GetKeywords(L.Bash).Contains("mapfile"), "Bash keywords contain the builtin 'mapfile'");
        Check(SyntaxHighlighting.GetKeywords(L.Bash).Contains("shopt"), "Bash keywords contain the builtin 'shopt'");
        Check(SyntaxHighlighting.GetKeywords(L.Makefile).Contains("ifeq"), "Makefile keywords contain 'ifeq'");
        Check(SyntaxHighlighting.GetKeywords(L.Makefile).Contains("wildcard"), "Makefile keywords contain the function 'wildcard'");
        Check(SyntaxHighlighting.GetKeywords(L.Makefile).Contains("patsubst"), "Makefile keywords contain the function 'patsubst'");
        Check(SyntaxHighlighting.GetKeywords(L.Json).Contains("true"), "JSON literals contain 'true'");
        Check(SyntaxHighlighting.GetKeywords(L.Uci).Contains("config"), "UCI keywords contain 'config'");
        Check(SyntaxHighlighting.GetKeywords(L.Yaml).Contains("true"), "YAML literals contain 'true'");
        Check(SyntaxHighlighting.GetKeywords(L.Sql).Contains("select"), "SQL keywords contain 'select'");
        Check(SyntaxHighlighting.GetKeywords(L.Cpp).Contains("constexpr"), "C++ keywords contain 'constexpr'");
        Check(SyntaxHighlighting.GetKeywords(L.Cpp).Contains("nullptr"), "C++ keywords contain 'nullptr'");
        Check(!SyntaxHighlighting.GetKeywords(L.Cpp).Contains("interface"), "C++ keywords do not include the C# 'interface'");
        Check(SyntaxHighlighting.GetKeywords(L.Ruby).Contains("def"), "Ruby keywords contain 'def'");
        Check(SyntaxHighlighting.GetKeywords(L.Perl).Contains("sub"), "Perl keywords contain 'sub'");
        Check(SyntaxHighlighting.GetKeywords(L.CMake).Contains("add_executable"), "CMake keywords contain 'add_executable'");
        Check(SyntaxHighlighting.GetKeywords(L.Dockerfile).Contains("FROM"), "Dockerfile keywords contain 'FROM'");
        Check(SyntaxHighlighting.GetKeywords(L.Markup).Count == 0, "Markup has no keyword list of its own");

        // The C-like profiles keep their own keyword sets: the signature word of one is not offered by another.
        (L Language, string Keyword, string Foreign)[] profiles =
        {
            (L.CSharp, "namespace", "fun"), (L.Java, "synchronized", "fun"), (L.Kotlin, "fun", "synchronized"),
            (L.Scala, "implicit", "fun"), (L.Groovy, "def", "sub"), (L.Go, "defer", "class"),
            (L.Rust, "impl", "class"), (L.Swift, "guard", "impl"), (L.Dart, "mixin", "guard"),
            (L.JavaScript, "function", "fun"), (L.TypeScript, "interface", "fun"), (L.Php, "echo", "fun"),
            (L.Css, "display", "message"), (L.Protobuf, "message", "impl"), (L.DeviceTree, "compatible", "message"),
            (L.Awk, "BEGIN", "sizeof"), (L.Tcl, "proc", "sizeof"),
            (L.Assembly, "mov", "def"), (L.PowerShell, "function", "sizeof"),
            (L.Verilog, "module", "entity"), (L.Vhdl, "entity", "module")
        };
        foreach ((L language, string keyword, string foreign) in profiles)
        {
            string name = SyntaxHighlighting.GetDisplayName(language);
            Check(SyntaxHighlighting.GetKeywords(language).Contains(keyword), name + " keywords contain '" + keyword + "'");
            Check(!SyntaxHighlighting.GetKeywords(language).Contains(foreign), name + " does not see the foreign '" + foreign + "'");
        }
        Check(SyntaxHighlighting.GetKeywords(L.CSharp).Contains("region"), "C# keywords contain the 'region' directive");
        Check(!SyntaxHighlighting.GetKeywords(L.CSharp).Contains("include"), "C# keywords do not contain the C 'include' directive");
        Check(SyntaxHighlighting.GetKeywords(L.Verilog).Contains("timescale"),
            "Verilog keywords contain the `timescale directive");
        Check(!SyntaxHighlighting.GetKeywords(L.Verilog).Contains("MODULE"),
            "Verilog keywords are case sensitive");
        Check(((HashSet<string>)SyntaxHighlighting.GetKeywords(L.Vhdl)).Contains("ARCHITECTURE"),
            "VHDL keywords are case insensitive");
        Check(Names(L.DeviceTree, "", "").Contains("interrupt-parent"), "a hyphenated Device Tree property is offered");
        Check(Names(L.DeviceTree, "", "").Contains("gpio-controller"), "another hyphenated Device Tree property is offered");
        Check(Names(L.Css, "", "").Contains("font-size"), "a hyphenated CSS property is offered");
        Check(Names(L.Kconfig, "", "").Contains("menuconfig"), "the Kconfig directive 'menuconfig' is offered");
        Check(Names(L.Kconfig, "", "").Contains("depends"), "the Kconfig directive 'depends' is offered");
        Check(Names(L.Kconfig, "", "").Contains("rsource"), "the Kconfig directive 'rsource' is offered");
        Check(Names(L.Kconfig, "", "").Contains("osource"), "the Kconfig directive 'osource' is offered");
        Check(Names(L.UBoot, "", "").Contains("bootm"), "the U-Boot command 'bootm' is offered");
        Check(Names(L.UBoot, "", "").Contains("setenv"), "the U-Boot command 'setenv' is offered");
        Check(Names(L.UBoot, "", "").Contains("bootefi"), "the U-Boot command 'bootefi' is offered");
        Check(Names(L.UBoot, "", "").Contains("bootflow"), "the U-Boot command 'bootflow' is offered");
        Check(Names(L.UBoot, "", "").Contains("bdinfo"), "the U-Boot command 'bdinfo' is offered");
        Check(Names(L.Yocto, "", "").Contains("SRC_URI"), "the BitBake variable 'SRC_URI' is offered");
        Check(Names(L.Yocto, "", "").Contains("do_install"), "the BitBake task 'do_install' is offered");
        Check(Names(L.Yocto, "", "").Contains("addpylib"), "the BitBake directive 'addpylib' is offered");
        Check(Names(L.Yocto, "", "").Contains("addfragments"), "the BitBake directive 'addfragments' is offered");
        Check(Names(L.Yocto, "", "").Contains("BB_NUMBER_THREADS"), "the BitBake variable 'BB_NUMBER_THREADS' is offered");
        Check(Names(L.Yocto, "", "").Contains("BB_CONF_FRAGMENT_SUMMARY"), "the BitBake variable 'BB_CONF_FRAGMENT_SUMMARY' is offered");
        Check(Names(L.Yocto, "", "").Contains("OE_FRAGMENTS"), "the BitBake variable 'OE_FRAGMENTS' is offered");
        Check(Names(L.LinkerScript, "", "").Contains("SECTIONS"), "the linker command 'SECTIONS' is offered");
        Check(Names(L.LinkerScript, "", "").Contains("SIZEOF"), "the linker command 'SIZEOF' is offered");
        Check(Names(L.LinkerScript, "", "").Contains("VERSION"), "the linker command 'VERSION' is offered");
        Check(Names(L.LinkerScript, "", "").Contains("NOCROSSREFS"), "the linker command 'NOCROSSREFS' is offered");
        Check(Names(L.LinkerScript, "", "").Contains("NOCROSSREFS_TO"), "the linker command 'NOCROSSREFS_TO' is offered");
        Check(Names(L.Crontab, "", "").Contains("jan"), "the crontab month 'jan' is offered");
        Check(Names(L.Crontab, "", "").Contains("MAILTO"), "the crontab variable 'MAILTO' is offered");
        Check(Names(L.Crontab, "", "").Contains("RANDOM_DELAY"), "the crontab variable 'RANDOM_DELAY' is offered");
        Check(Names(L.Crontab, "", "").Contains("MLS_LEVEL"), "the crontab variable 'MLS_LEVEL' is offered");
        Check(Names(L.Crontab, "", "").Contains("CONTENT_TYPE"), "the crontab variable 'CONTENT_TYPE' is offered");
        Check(Names(L.Crontab, "", "").Contains("MAILFROM"), "the crontab variable 'MAILFROM' is offered");
        Check(Names(L.Fstab, "", "").Contains("ext4"), "the fstab filesystem 'ext4' is offered");
        Check(Names(L.Fstab, "", "").Contains("noatime"), "the fstab option 'noatime' is offered");
        Check(Names(L.Fstab, "", "").Contains("comment"), "the fstab option 'comment' is offered");
        Check(Names(L.Sudoers, "", "").Contains("NOPASSWD"), "the sudoers tag 'NOPASSWD' is offered");
        Check(Names(L.Sudoers, "", "").Contains("secure_path"), "the sudoers flag 'secure_path' is offered");
        Check(Names(L.MySql, "", "").Contains("innodb_buffer_pool_size"), "the MySQL option 'innodb_buffer_pool_size' is offered");
        Check(Names(L.MySql, "", "").Contains("skip-name-resolve"), "the MySQL option 'skip-name-resolve' is offered");
        Check(Names(L.MySql, "", "").Contains("loose-innodb_buffer_pool_size"), "the MySQL loose option 'loose-innodb_buffer_pool_size' is offered");
        Check(Names(L.MySql, "", "").Contains("loose_max_connections"), "the MySQL loose option 'loose_max_connections' is offered");
        Check(Names(L.Redis, "", "").Contains("maxmemory-policy"), "the Redis directive 'maxmemory-policy' is offered");
        Check(Names(L.Redis, "", "").Contains("appendfsync"), "the Redis directive 'appendfsync' is offered");
        Check(Names(L.Redis, "", "").Contains("sentinel"), "the Redis Sentinel directive 'sentinel' is offered");
        Check(Names(L.Redis, "", "").Contains("monitor"), "the Redis Sentinel command 'monitor' is offered");
        Check(Names(L.Redis, "", "").Contains("down-after-milliseconds"), "the Redis Sentinel command 'down-after-milliseconds' is offered");
        Check(Names(L.Redis, "", "").Contains("failover-timeout"), "the Redis Sentinel command 'failover-timeout' is offered");
        Check(Names(L.Redis, "", "").Contains("parallel-syncs"), "the Redis Sentinel command 'parallel-syncs' is offered");
        Check(Names(L.Redis, "", "").Contains("auth-pass"), "the Redis Sentinel command 'auth-pass' is offered");
        Check(Names(L.HAProxy, "", "").Contains("default_backend"), "the HAProxy directive 'default_backend' is offered");
        Check(Names(L.HAProxy, "", "").Contains("roundrobin"), "the HAProxy balance 'roundrobin' is offered");
        Check(Names(L.HAProxy, "", "").Contains("fcgi-app"), "the HAProxy section 'fcgi-app' is offered");
        Check(Names(L.HAProxy, "", "").Contains("httpclient"), "the HAProxy section 'httpclient' is offered");
        Check(Names(L.HAProxy, "", "").Contains("traces"), "the HAProxy section 'traces' is offered");
        Check(Names(L.HAProxy, "", "").Contains("acme"), "the HAProxy section 'acme' is offered");
        Check(Names(L.ApacheHttpd, "", "").Contains("DocumentRoot"), "the Apache directive 'DocumentRoot' is offered");
        Check(Names(L.ApacheHttpd, "", "").Contains("FollowSymLinks"), "the Apache option 'FollowSymLinks' is offered");
        Check(Names(L.ApacheHttpd, "", "").Contains("Define"), "the Apache directive 'Define' is offered");
        Check(Names(L.ApacheHttpd, "", "").Contains("UnDefine"), "the Apache directive 'UnDefine' is offered");
        Check(Names(L.ApacheHttpd, "", "").Contains("If"), "the Apache section 'If' is offered");
        Check(Names(L.Nginx, "", "").Contains("proxy_pass"), "the nginx directive 'proxy_pass' is offered");
        Check(Names(L.Nginx, "", "").Contains("js_import"), "the nginx directive 'js_import' is offered");
        Check(Names(L.Nginx, "", "").Contains("js_content"), "the nginx directive 'js_content' is offered");
        Check(Names(L.Nginx, "", "").Contains("js_set"), "the nginx directive 'js_set' is offered");
        Check(Names(L.Nginx, "", "").Contains("js_var"), "the nginx directive 'js_var' is offered");
        Check(Names(L.Nginx, "", "").Contains("js_periodic"), "the nginx directive 'js_periodic' is offered");
        Check(Names(L.Nginx, "", "").Contains("js_preload_object"), "the nginx directive 'js_preload_object' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("DHCP"), "the systemd-networkd key 'DHCP' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("LinkLocalAddressing"), "the systemd-networkd key 'LinkLocalAddressing' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("PrivateKey"), "the systemd-networkd WireGuard key 'PrivateKey' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("ListenPort"), "the systemd-networkd WireGuard key 'ListenPort' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("PublicKey"), "the systemd-networkd WireGuard key 'PublicKey' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("PresharedKey"), "the systemd-networkd WireGuard key 'PresharedKey' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("AllowedIPs"), "the systemd-networkd WireGuard key 'AllowedIPs' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("Endpoint"), "the systemd-networkd WireGuard key 'Endpoint' is offered");
        Check(Names(L.SystemdNetworkd, "", "").Contains("PersistentKeepalive"), "the systemd-networkd WireGuard key 'PersistentKeepalive' is offered");
        Check(Names(L.Ifupdown, "", "").Contains("iface"), "the ifupdown stanza 'iface' is offered");
        Check(Names(L.Ifupdown, "", "").Contains("bridge_ports"), "the ifupdown option 'bridge_ports' is offered");
        Check(Names(L.Dnsmasq, "", "").Contains("dhcp-range"), "the dnsmasq option 'dhcp-range' is offered");
        Check(Names(L.Dnsmasq, "", "").Contains("ra-names"), "the dnsmasq value 'ra-names' is offered");
        Check(Names(L.Hostapd, "", "").Contains("wpa_key_mgmt"), "the hostapd option 'wpa_key_mgmt' is offered");
        Check(Names(L.Hostapd, "", "").Contains("SAE"), "the hostapd value 'SAE' is offered");
        Check(Names(L.Wireguard, "", "").Contains("AllowedIPs"), "the WireGuard key 'AllowedIPs' is offered");
        Check(Names(L.Wireguard, "", "").Contains("auto"), "the WireGuard value 'auto' is offered");
        Check(Names(L.Udev, "", "").Contains("SUBSYSTEM"), "the udev key 'SUBSYSTEM' is offered");
        Check(Names(L.Udev, "", "").Contains("db_persist"), "the udev value 'db_persist' is offered");
        Check(Names(L.Nftables, "", "").Contains("masquerade"), "the nftables statement 'masquerade' is offered");
        Check(Names(L.Nftables, "", "").Contains("established"), "the nftables state 'established' is offered");
        Check(Names(L.Modprobe, "", "").Contains("softdep"), "the modprobe directive 'softdep' is offered");
        Check(Names(L.Modprobe, "", "").Contains("pre"), "the modprobe value 'pre' is offered");
        Check(Names(L.Logrotate, "", "").Contains("postrotate"), "the logrotate directive 'postrotate' is offered");
        Check(Names(L.Logrotate, "", "").Contains("weekly"), "the logrotate value 'weekly' is offered");
        Check(Names(L.Sysctl, "", "").Contains("bbr"), "the sysctl value 'bbr' is offered");
        Check(Names(L.Sysctl, "", "").Contains("fq_codel"), "the sysctl value 'fq_codel' is offered");
        Check(Names(L.Pam, "", "").Contains("requisite"), "the PAM control 'requisite' is offered");
        Check(Names(L.Pam, "", "").Contains("try_first_pass"), "the PAM argument 'try_first_pass' is offered");
        Check(Names(L.Samba, "", "").Contains("workgroup"), "the Samba parameter 'workgroup' is offered");
        Check(Names(L.Samba, "", "").Contains("tdbsam"), "the Samba value 'tdbsam' is offered");
        Check(Names(L.Postfix, "", "").Contains("myhostname"), "the Postfix parameter 'myhostname' is offered");
        Check(Names(L.Postfix, "", "").Contains("permit_mynetworks"), "the Postfix restriction 'permit_mynetworks' is offered");
        Check(Names(L.OpenVpn, "", "").Contains("proto"), "the OpenVPN option 'proto' is offered");
        Check(Names(L.OpenVpn, "", "").Contains("persist-key"), "the OpenVPN option 'persist-key' is offered");
        Check(Names(L.Netplan, "", "").Contains("renderer"), "the Netplan key 'renderer' is offered");
        Check(Names(L.Netplan, "", "").Contains("networkd"), "the Netplan renderer 'networkd' is offered");
        Check(Names(L.CloudInit, "", "").Contains("write_files"), "the cloud-init module 'write_files' is offered");
        Check(Names(L.CloudInit, "", "").Contains("reboot"), "the cloud-init power state 'reboot' is offered");
        Check(Names(L.Prometheus, "", "").Contains("scrape_configs"), "the Prometheus setting 'scrape_configs' is offered");
        Check(Names(L.Prometheus, "", "").Contains("relabel_configs"), "the Prometheus setting 'relabel_configs' is offered");
        Check(Names(L.Alertmanager, "", "").Contains("receivers"), "the Alertmanager setting 'receivers' is offered");
        Check(Names(L.Alertmanager, "", "").Contains("saturday"), "the Alertmanager weekday 'saturday' is offered");
        Check(Names(L.MongoDb, "", "").Contains("wiredTiger"), "the MongoDB setting 'wiredTiger' is offered");
        Check(Names(L.MongoDb, "", "").Contains("requireTLS"), "the MongoDB TLS mode 'requireTLS' is offered");
        Check(Names(L.Elasticsearch, "", "").Contains("single-node"), "the Elasticsearch discovery mode 'single-node' is offered");
        Check(Names(L.Elasticsearch, "", "").Contains("data_hot"), "the Elasticsearch node role 'data_hot' is offered");
        Check(Names(L.Meson, "", "").Contains("executable"), "the Meson function 'executable' is offered");
        Check(Names(L.Meson, "", "").Contains("meson"), "the Meson object 'meson' is offered");
        Check(Names(L.Hcl, "", "").Contains("resource"), "the HCL block 'resource' is offered");
        Check(Names(L.Hcl, "", "").Contains("jsonencode"), "the HCL function 'jsonencode' is offered");
        Check(Names(L.Hcl, "", "").Contains("var"), "the HCL object 'var' is offered");
        Check(Names(L.Starlark, "", "").Contains("load"), "the Starlark keyword 'load' is offered");
        Check(Names(L.Starlark, "", "").Contains("cc_library"), "the Bazel rule 'cc_library' is offered");
        Check(Names(L.Starlark, "", "").Contains("depset"), "the Starlark function 'depset' is offered");
        Check(Names(L.Nix, "", "").Contains("inherit"), "the Nix keyword 'inherit' is offered");
        Check(Names(L.Nix, "", "").Contains("derivation"), "the Nix function 'derivation' is offered");
        Check(Names(L.Nix, "", "").Contains("builtins"), "the Nix object 'builtins' is offered");
        Check(Names(L.Fish, "", "").Contains("function"), "the Fish keyword 'function' is offered");
        Check(Names(L.Fish, "", "").Contains("string"), "the Fish builtin 'string' is offered");
        Check(Names(L.Batch, "", "").Contains("if"), "the Batch keyword 'if' is offered");
        Check(Names(L.Batch, "", "").Contains("setlocal"), "the Batch command 'setlocal' is offered");
        Check(Names(L.Batch, "", "").Contains("robocopy"), "the Batch command 'robocopy' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("apiVersion"), "the Kubernetes key 'apiVersion' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("Deployment"), "the Kubernetes kind 'Deployment' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("b64enc"), "the Helm function 'b64enc' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("sha256sum"), "the Helm function 'sha256sum' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("merge"), "the Helm function 'merge' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("coalesce"), "the Helm function 'coalesce' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("ternary"), "the Helm function 'ternary' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("dig"), "the Helm function 'dig' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("toPrettyJson"), "the Helm function 'toPrettyJson' is offered");
        Check(Names(L.Kubernetes, "", "").Contains("genCA"), "the Helm function 'genCA' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("services"), "the Compose key 'services' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("depends_on"), "the Compose key 'depends_on' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("include"), "the Compose key 'include' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("gpus"), "the Compose key 'gpus' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("develop"), "the Compose key 'develop' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("models"), "the Compose key 'models' is offered");
        Check(Names(L.DockerCompose, "", "").Contains("provider"), "the Compose key 'provider' is offered");
        Check(Names(L.Ansible, "", "").Contains("hosts"), "the Ansible key 'hosts' is offered");
        Check(Names(L.Ansible, "", "").Contains("apt"), "the Ansible module 'apt' is offered");
        Check(Names(L.PostgreSql, "", "").Contains("listen_addresses"), "the PostgreSQL setting 'listen_addresses' is offered");
        Check(Names(L.PostgreSql, "", "").Contains("scram-sha-256"), "the PostgreSQL auth method 'scram-sha-256' is offered");
        Check(Names(L.Toml, "", "").Contains("true"), "the TOML literal 'true' is offered");
        Check(Names(L.Toml, "title = \"x\"", "").Contains("title"), "a TOML key from the document is offered");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("interface"), "C keywords do not include the C# 'interface'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("fun"), "C keywords do not include the Kotlin 'fun'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("impl"), "C keywords do not include the Rust 'impl'");
        Check(!SyntaxHighlighting.GetKeywords(L.CLike).Contains("message"), "C keywords do not include the Protobuf 'message'");
    }

    private static void SourceChecks()
    {
        // Keywords and document words are both offered.
        List<string> all = Names(L.CLike, "int counter; counter = 1;", "");
        Check(all.Contains("if"), "the C-like keyword 'if' is offered");
        Check(all.Contains("counter"), "the document identifier 'counter' is offered");
        Equal(all.Count(x => x == "counter"), 1, "a word appears only once");
        Check(all.IndexOf("if") < all.IndexOf("counter"), "keywords come before the document words");

        // Words that differ only in case come in a fixed order, whatever the order in the document.
        Equal(string.Join("|", Names(L.Plain, "foo Foo foo FOO", "f")), "FOO|Foo|foo", "case variants are ordered the same way every time");
        Equal(string.Join("|", Names(L.Plain, "FOO foo Foo", "f")), "FOO|Foo|foo", "and independently of the order of the text");

        // The prefix filter is case insensitive.
        List<string> prefixed = Names(L.Python, "def doWork():\n    done = 1\n", "do");
        Check(prefixed.Contains("doWork"), "the prefix filter keeps 'doWork'");
        Check(prefixed.Contains("done"), "the prefix filter keeps 'done'");
        Check(!prefixed.Contains("def"), "'def' does not match the prefix 'do'");

        // An empty prefix returns everything, a missing document is fine.
        Check(Names(L.Lua, null, "").Count > 0, "keywords are offered without a document");

        // Plain text has no keywords, but the words of the document itself are still offered.
        Equal(Names(L.Plain, "", "").Count, 0, "an empty plain document has no completions");
        Check(Names(L.Plain, "hello world", "").Contains("hello"), "plain text offers the words of the document");

        // Numbers, keywords of other languages and over-long words are dropped.
        List<string> words = Names(L.CLike, "value1 _tmp 123abc", "");
        Check(words.Contains("value1"), "an identifier with a digit is kept");
        Check(words.Contains("_tmp"), "an identifier starting with '_' is kept");
        Check(!words.Contains("123abc"), "a token that starts with a digit is dropped");
        Check(!words.Contains("select"), "a keyword of another language is not offered");

        Check(EditorCompletionSource.IsIdentifier("foo_bar2"), "foo_bar2 is an identifier");
        Check(!EditorCompletionSource.IsIdentifier("2foo"), "2foo is not an identifier");
        Check(!EditorCompletionSource.IsIdentifier(""), "an empty string is not an identifier");

        // The C++ profile has its own keyword set and still offers the identifiers of the document.
        List<string> cpp = Names(L.Cpp, "std::vector<int> values;", "");
        Check(cpp.Contains("constexpr"), "the C++ keyword 'constexpr' is offered");
        Check(cpp.Contains("values"), "a C++ document identifier is offered");
        Check(!cpp.Contains("interface"), "a C# keyword is not offered for C++");

        // Ruby, CMake and Dockerfile have separate keyword sets.
        Check(Names(L.Ruby, "", "").Contains("def"), "the Ruby keyword 'def' is offered");
        Check(!Names(L.Ruby, "", "").Contains("FROM"), "a Dockerfile instruction is not offered for Ruby");
        Check(Names(L.Perl, "", "").Contains("sub"), "the Perl keyword 'sub' is offered");
        Check(Names(L.CMake, "", "add_").Contains("add_executable"), "the CMake command 'add_executable' is offered");
        Check(Names(L.Dockerfile, "", "FR").Contains("FROM"), "the Dockerfile instruction 'FROM' is offered");
        Check(!Names(L.Dockerfile, "", "add_").Contains("add_executable"), "the CMake command is not offered for Dockerfile");
    }

    private static void FileLanguageChecks()
    {
        // The completion list follows the language detected from the file name or the #! line.
        L python = SyntaxHighlighting.DetectLanguage("/home/user/app.PY");
        Equal(python, L.Python, "app.PY is detected as Python");
        List<string> pythonWords = Names(python, "", "");
        Check(pythonWords.Contains("def"), "a .py file is offered the Python keyword 'def'");
        Check(!pythonWords.Contains("select"), "a .py file is not offered the SQL keyword 'select'");
        Check(!pythonWords.Contains("include"), "a .py file is not offered the C preprocessor word 'include'");

        L sql = SyntaxHighlighting.DetectLanguage("/srv/query.sql");
        Equal(sql, L.Sql, "query.sql is detected as SQL");
        List<string> sqlWords = Names(sql, "", "");
        Check(sqlWords.Contains("select"), "a .sql file is offered the SQL keyword 'select'");
        Check(!sqlWords.Contains("def"), "a .sql file is not offered the Python keyword 'def'");

        L verilog = SyntaxHighlighting.DetectLanguage("/srv/counter.sv");
        Equal(verilog, L.Verilog, "counter.sv is detected as Verilog");
        List<string> verilogWords = Names(verilog, "", "");
        Check(verilogWords.Contains("always_ff"), "a .sv file is offered the Verilog keyword 'always_ff'");
        Check(!verilogWords.Contains("entity"), "a .sv file is not offered the VHDL keyword 'entity'");
        Check(Names(L.Verilog, "", "alw").Contains("always"), "the Verilog completion prefix finds 'always'");

        L vhdl = SyntaxHighlighting.DetectLanguage("/srv/counter.vhdl");
        Equal(vhdl, L.Vhdl, "counter.vhdl is detected as VHDL");
        List<string> vhdlWords = Names(vhdl, "", "");
        Check(vhdlWords.Contains("architecture"), "a .vhdl file is offered the VHDL keyword 'architecture'");
        Check(!vhdlWords.Contains("always_ff"), "a .vhdl file is not offered the Verilog keyword 'always_ff'");
        Check(Names(L.Vhdl, "", "ENT").Contains("entity"),
            "the VHDL completion prefix is case insensitive");

        L shell = SyntaxHighlighting.DetectLanguage("/usr/local/bin/backup", "#!/bin/bash");
        Equal(shell, L.Bash, "a #!/bin/bash script without an extension is detected as Bash");
        Check(Names(shell, "", "").Contains("fi"), "a Bash script is offered the keyword 'fi'");

        L csharp = SyntaxHighlighting.DetectLanguage("/srv/Program.cs");
        Equal(csharp, L.CSharp, "Program.cs is detected as C#");
        List<string> csharpWords = Names(csharp, "", "");
        Check(csharpWords.Contains("namespace"), "a .cs file is offered the C# keyword 'namespace'");
        Check(!csharpWords.Contains("fun"), "a .cs file is not offered the Kotlin keyword 'fun'");

        L typescript = SyntaxHighlighting.DetectLanguage("/srv/app.ts");
        Equal(typescript, L.TypeScript, "app.ts is detected as TypeScript");
        List<string> typeScriptWords = Names(typescript, "", "");
        Check(typeScriptWords.Contains("interface"), "a .ts file is offered the TypeScript keyword 'interface'");
        Check(!typeScriptWords.Contains("include"), "a .ts file is not offered the C preprocessor word 'include'");

        L assembly = SyntaxHighlighting.DetectLanguage("/srv/boot.S");
        Equal(assembly, L.Assembly, "boot.S is detected as Assembly");
        List<string> assemblyWords = Names(assembly, "", "");
        Check(assemblyWords.Contains("mov"), "an Assembly file is offered the instruction 'mov'");
        Check(assemblyWords.Contains("rax"), "an Assembly file is offered the register 'rax'");
        Check(!assemblyWords.Contains("def"), "an Assembly file is not offered the Python keyword 'def'");

        Equal(SyntaxHighlighting.DetectLanguage("/tmp/notes.unknown"), L.Plain, "an unknown extension is plain text");
    }

    private static void SuggestionFilterChecks()
    {
        // The rule the completion popup filters its list with. A name shorter than the prefix cannot start with
        // it; the old catalog test asked for a substring past the end of a short key and threw ArgumentOutOfRange.
        Check(SuggestionFilter.MatchesPrefix("done", null), "a null prefix matches everything");
        Check(SuggestionFilter.MatchesPrefix("done", ""), "an empty prefix matches everything");
        Check(SuggestionFilter.MatchesPrefix("done", "do"), "'done' matches the prefix 'do'");
        Check(!SuggestionFilter.MatchesPrefix("def", "do"), "'def' does not match the prefix 'do'");
        Check(!SuggestionFilter.MatchesPrefix("d", "do"), "a name shorter than the prefix does not match");
        Check(!SuggestionFilter.MatchesPrefix("do", "done"), "a name shorter than a longer prefix does not match");
        Check(SuggestionFilter.MatchesPrefix("do", "do"), "a name equal to the prefix matches");
        Check(SuggestionFilter.MatchesPrefix("Done", "Done"), "the prefix is matched case-sensitively");
        Check(!SuggestionFilter.MatchesPrefix("done", "Done"), "a different case does not match");
        Check(!SuggestionFilter.MatchesPrefix(null, "do"), "a null name matches nothing");
        Check(SuggestionFilter.MatchesPrefix("x", null), "a null prefix matches even a short name");
    }

    private static void Main()
    {
        KeywordChecks();
        SourceChecks();
        FileLanguageChecks();
        SuggestionFilterChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " completion checks passed.");
    }
}
