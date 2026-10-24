using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SmarTTY.IO;

/// <summary>
/// The icon of an item in the file list. The XAML of the list (GUI/FileListControl.xaml) picks the picture by the name of the
/// value, so a new value needs a trigger there and a picture in icons/filetypes (icons/MainWindow for the first ones).
/// </summary>
public enum ItemIcon
{
    /// <summary>A file of an unknown type.</summary>
    File,
    Folder,

    /// <summary>An opkg package (.ipk).</summary>
    PackageIpk,

    /// <summary>An apk package (.apk).</summary>
    PackageApk,

    /// <summary>A Debian package (.deb).</summary>
    PackageDeb,

    /// <summary>An RPM package (.rpm).</summary>
    PackageRpm,

    /// <summary>tar, gz, xz, zip, 7z and other archives and compressed files.</summary>
    Archive,
    Image,
    Audio,
    Video,

    /// <summary>Plain text, Markdown, README, LICENSE.</summary>
    Text,

    /// <summary>doc, odt, rtf, epub and other word-processor documents.</summary>
    Document,
    Pdf,

    /// <summary>xls, ods, csv.</summary>
    Spreadsheet,
    Presentation,

    /// <summary>conf, ini, yaml, systemd units, dotfiles such as .bashrc, files of /etc such as fstab.</summary>
    Config,
    Log,

    /// <summary>sh, bash, zsh and other shell scripts.</summary>
    Shell,
    Python,

    /// <summary>C and C++ sources and headers.</summary>
    CSource,

    /// <summary>Sources in other languages (js, php, go, rs, lua, Makefile...).</summary>
    Code,

    /// <summary>html, css.</summary>
    Web,

    /// <summary>json, xml and other structured data.</summary>
    Data,
    Database,

    /// <summary>Executables, libraries, kernel modules, firmware and other binary files.</summary>
    Binary,

    /// <summary>iso, img, qcow2 and other disk images.</summary>
    DiskImage,

    /// <summary>Keys and certificates (pem, crt, pub, gpg...), SSH key files.</summary>
    Certificate,
    Font,

    /// <summary>Backup and temporary copies (.bak, .old, .orig, .swp, name~).</summary>
    Backup,

    /// <summary>patch and diff files.</summary>
    Patch
}

/// <summary>
/// Which icon a file gets in the file list, by its name only (the list does not read the files): the extension of popular Linux
/// file types, a few well-known names (Makefile, README, fstab, id_rsa) and dotfiles. The match is case-insensitive.
/// Pure logic without SSH, tested by tests/RemoteFileOperationsTests.
/// </summary>
public static class RemoteFileTypes
{
    private static readonly Dictionary<string, ItemIcon> ByExtension = BuildExtensionTable();

    private static readonly Dictionary<string, ItemIcon> ByName = BuildNameTable();

    // libfoo.so, libfoo.so.1, libfoo.so.1.2.3
    private static readonly Regex SharedLibrary = new Regex(@"\.so(\.\d+)*$", RegexOptions.Compiled);

    // messages.log, messages.log.1, messages.log.2.gz, messages.log-20240101
    private static readonly Regex RotatedLog = new Regex(@"\.log([.-]\d+)?(\.(gz|xz|bz2|zst|lz4))?$", RegexOptions.Compiled);

    // Left by dpkg/rpm/ucf when a changed config file is upgraded: sshd_config.dpkg-dist, foo.conf.rpmnew
    private static readonly Regex PackageLeftover = new Regex(@"\.(dpkg-(old|new|dist|bak|tmp)|rpm(save|new|orig)|ucf-(old|new|dist))$", RegexOptions.Compiled);

    /// <summary>The icon of a file with the given name (a folder is not decided here: it always gets <see cref="ItemIcon.Folder"/>).</summary>
    public static ItemIcon IconFor(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return ItemIcon.File;
        }
        string lower = name.ToLowerInvariant();

        if (RemotePackageInstall.IsIpk(lower))
        {
            return ItemIcon.PackageIpk;
        }
        if (RemotePackageInstall.IsApk(lower))
        {
            return ItemIcon.PackageApk;
        }
        if (ByName.TryGetValue(lower, out ItemIcon byName))
        {
            return byName;
        }
        if (lower.EndsWith("~") || PackageLeftover.IsMatch(lower))
        {
            return ItemIcon.Backup;
        }
        if (RotatedLog.IsMatch(lower))
        {
            return ItemIcon.Log;
        }
        if (SharedLibrary.IsMatch(lower))
        {
            return ItemIcon.Binary;
        }

        int dot = lower.LastIndexOf('.');
        if (dot > 0 && dot < lower.Length - 1)
        {
            return ByExtension.TryGetValue(lower.Substring(dot + 1), out ItemIcon byExtension) ? byExtension : ItemIcon.File;
        }
        // ".bashrc", ".vimrc", ".screenrc": hidden run-control files without an extension
        if (lower.Length > 3 && lower[0] == '.' && lower.EndsWith("rc") && lower.IndexOf('.', 1) < 0)
        {
            return ItemIcon.Config;
        }
        if (lower.Length > 8 && lower[0] == '.' && lower.EndsWith("_history"))
        {
            return ItemIcon.Log;
        }
        return ItemIcon.File;
    }

    private static void Add(Dictionary<string, ItemIcon> table, ItemIcon icon, string names)
    {
        foreach (string name in names.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            table[name] = icon;
        }
    }

    private static Dictionary<string, ItemIcon> BuildExtensionTable()
    {
        Dictionary<string, ItemIcon> t = new Dictionary<string, ItemIcon>();
        Add(t, ItemIcon.PackageDeb, "deb udeb");
        Add(t, ItemIcon.PackageRpm, "rpm");
        Add(t, ItemIcon.Archive, "tar gz tgz bz2 tbz tbz2 xz txz zst tzst lz lz4 lzma lzo z zip zipx 7z rar cpio jar war cab arj");
        Add(t, ItemIcon.Image, "png jpg jpeg jpe gif bmp svg svgz webp ico tif tiff xcf psd heic heif avif pnm pbm pgm ppm xpm xbm tga");
        Add(t, ItemIcon.Audio, "mp3 wav flac ogg oga opus m4a aac wma mid midi aif aiff ape amr mka");
        Add(t, ItemIcon.Video, "mp4 mkv avi mov webm flv wmv mpg mpeg m4v 3gp ogv vob m2ts mts");
        Add(t, ItemIcon.Text, "txt text md markdown rst adoc asciidoc org nfo tex bib");
        Add(t, ItemIcon.Document, "doc docx dot dotx odt ott rtf epub mobi fb2 djvu ps eps xps pages wpd");
        Add(t, ItemIcon.Pdf, "pdf");
        Add(t, ItemIcon.Spreadsheet, "xls xlsx xlsm ods csv tsv numbers");
        Add(t, ItemIcon.Presentation, "ppt pptx pps ppsx odp");
        Add(t, ItemIcon.Config, "conf cfg config ini toml yaml yml properties env cnf desktop service timer socket mount target path rules netdev network link preset repo list sources ovpn cron editorconfig");
        Add(t, ItemIcon.Log, "log journal");
        Add(t, ItemIcon.Shell, "sh bash zsh fish ksh csh tcsh ash dash command bats bat cmd ps1");
        Add(t, ItemIcon.Python, "py pyw pyc pyo pyi ipynb");
        Add(t, ItemIcon.CSource, "c h cc cpp cxx c++ hpp hh hxx h++ ipp inl ino");
        Add(t, ItemIcon.Code, "js mjs cjs jsx ts tsx vue php phtml rb pl pm lua go rs java kt kts swift cs vb fs scala groovy gradle r jl dart ex exs erl hrl hs ml clj lisp el vim awk asm s nasm mk cmake bzl sol tcl sv zig nim cr qml ld lds dts dtsi uc ut");
        Add(t, ItemIcon.Web, "html htm xhtml shtml css scss sass less asp aspx jsp ejs");
        Add(t, ItemIcon.Data, "json jsonl ndjson geojson xml xsd xsl xslt rss atom kml gpx ics vcf");
        Add(t, ItemIcon.Database, "db db3 sqlite sqlite3 sdb sql mdb accdb mdf rdb dbf frm myd myi ibd");
        Add(t, ItemIcon.Binary, "bin elf out o a ko exe dll dtb dtbo hex srec trx fw rom itb squashfs ubi jffs2 class wasm pcap pcapng appimage");
        Add(t, ItemIcon.DiskImage, "iso img qcow qcow2 vdi vmdk vhd vhdx dmg wim ova");
        Add(t, ItemIcon.Certificate, "pem crt cer der key pub p12 pfx p7b p7c csr crl gpg pgp asc sig kbx jks keystore ppk");
        Add(t, ItemIcon.Font, "ttf otf ttc woff woff2 eot pfb pfa fon pcf bdf psf");
        Add(t, ItemIcon.Backup, "bak old orig save tmp temp swp swo bkp");
        Add(t, ItemIcon.Patch, "patch diff rej");
        return t;
    }

    private static Dictionary<string, ItemIcon> BuildNameTable()
    {
        Dictionary<string, ItemIcon> t = new Dictionary<string, ItemIcon>();
        Add(t, ItemIcon.Code, "makefile gnumakefile cmakelists.txt dockerfile rakefile gemfile vagrantfile");
        Add(t, ItemIcon.Text, "readme license licence copying copyright authors changelog changes todo notice install news contributors hacking");
        Add(t, ItemIcon.Config, "passwd shadow group gshadow fstab hosts hostname sudoers crontab exports inittab profile environment motd issue shells services protocols nsswitch.conf resolv.conf .profile .env .gitconfig .gitignore .gitattributes .gitmodules .inputrc .bash_aliases .bash_logout .bash_profile");
        Add(t, ItemIcon.Certificate, "authorized_keys known_hosts id_rsa id_dsa id_ecdsa id_ed25519");
        Add(t, ItemIcon.Log, "syslog messages dmesg wtmp btmp lastlog faillog .lesshst .viminfo");
        Add(t, ItemIcon.Binary, "vmlinuz vmlinux initrd initramfs zimage uimage");
        return t;
    }
}
