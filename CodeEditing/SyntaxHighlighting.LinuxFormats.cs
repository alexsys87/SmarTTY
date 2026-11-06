using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

// The formats of the Linux kernel, OpenWrt and embedded builds that are not programs with a configuration file:
// Kconfig and the .config it produces. They use the colours of Config with the words of the language known, so a
// misspelt directive stands out and the editor offers the real ones.
public static partial class SyntaxHighlighting
{
    // ---------------------------------------------------------------- Kconfig

    // Line state: the indented text of a "help" block, which is prose and not keys and values.
    private const int StKconfigHelp = 0x01;

    private static readonly HashSet<string> KconfigKeywords = Words(StringComparer.Ordinal,
        // structure
        @"config menuconfig menu endmenu choice endchoice comment mainmenu source if endif
          depends select imply default prompt help range visible option optional modules transition
          bool tristate string int hex def_bool def_tristate def_hex def_string def_int");

    private static readonly HashSet<string> KconfigValues = Words(StringComparer.Ordinal, @"y m n");

    private static readonly TokenCache.LineTokenizer KconfigTokenizer = TokenizeKconfig;

    /// <summary>
    /// Kconfig: the colours of Config with the Kconfig words as keywords. A directive that stands alone ("endmenu",
    /// "help", "choice") is a keyword too, which the Config tokenizer cannot tell without a value after it. The text
    /// of a help block is left plain.
    /// </summary>
    private static bool TokenizeKconfig(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        int first = SkipBlanks(text, 0);
        if ((startState & StKconfigHelp) != 0 && (first == text.Length || first > 0))
        {
            endState = StKconfigHelp;
            return true;
        }

        if (first < text.Length && text[first] != '#' && text[first] != ';')
        {
            if (StartsWithAt(text, first, "---help---") && SkipBlanks(text, first + 10) == text.Length)
            {
                sink.AddToken(TypeKeyword, first, first + 10);
                endState = StKconfigHelp;
                return true;
            }
            int end = first;
            while (end < text.Length && IsIdChar(text[end]))
            {
                end++;
            }
            if (end > first && SkipBlanks(text, end) == text.Length &&
                KconfigKeywords.Contains(text.Substring(first, end - first)))
            {
                sink.AddToken(TypeKeyword, first, end);
                endState = (text.Substring(first, end - first) == "help") ? StKconfigHelp : 0;
                return true;
            }
        }
        return TokenizeConfigCore(KconfigKeywords, startState, text, sink, out endState, dollarParentheses: true, valueKeywords: KconfigValues);
    }

    // ---------------------------------------------------------------- U-Boot

    private static readonly HashSet<string> UBootKeywords = Words(StringComparer.Ordinal,
        // commands of the U-Boot shell
        @"setenv saveenv run bootm bootz booti bootd bootelf bootvx boot go reset
          tftpboot tftp fatload fatwrite fatls fatmkdir fatrm ext4load ext4write ext4ls ext4size
          ext2load ext2write ext2ls ext2size mmc nand nor sf usb scsi sata dhcp
          load loadb loads loadx loady printenv env import export source
          if then else elif fi for while do done until case esac in
          test echo sleep mw md mm nm cp cmp mv
          setexpr protect erase
          ubi ubifs mtd ubifsmount ubifsumount ubifswrite ubifsload
          i2c gpio adc fuse dm part disk ls size
          fdt addr resize move mknode setprop print rm
          fastboot dfu acpi button
          bootargs bootcmd bootdelay baudrate ethaddr eth1addr ipaddr serverip gatewayip netmask
          fileaddr filesize loadaddr fdtaddr fdtcontroladdr kernel_addr_r fdt_addr_r ramdisk_addr_r
          scriptaddr pxefile_addr_r fdtoverlay_addr_r stdin stdout stderr
          boot_targets distro_bootcmd boot_scripts prefix fdtfile board board_name soc vendor ver
          serial# ethact netretry tftpblocksize");

    private static readonly TokenCache.LineTokenizer UBootTokenizer = new GenericSyntax
    {
        HashComments = true,
        ShellVariables = true,
        Keywords = UBootKeywords
    }.Tokenize;

    // ----------------------------------------------------------------- Yocto

    private static readonly HashSet<string> YoctoDirectives = Words(StringComparer.Ordinal,
        // metadata of a recipe
        @"SUMMARY DESCRIPTION HOMEPAGE LICENSE LIC_FILES_CHKSUM SECTION PRIORITY
          DEPENDS RDEPENDS RRECOMMENDS RSUGGESTS RPROVIDES RCONFLICTS RREPLACES
          SRC_URI SRCREV SRCREV_FORMAT S B D WORKDIR FILESPATH FILES FILESEXTRAPATHS FILESOVERRIDES
          PACKAGES PACKAGE_ARCH PACKAGE_BEFORE_PN PACKAGECONFIG PACKAGE_CLASSES
          EXTRA_OECONF EXTRA_OEMAKE EXTRA_OECMAKE EXTRA_OEMESON EXTRA_OE_MESON
          PV PR PE PKGV PKGR PACKAGE_NAME
          INHERIT IMAGE_INSTALL IMAGE_FSTYPES IMAGE_BASENAME IMAGE_NAME IMAGE_LINGUAS IMAGE_FEATURES
          IMAGE_ROOTFS_SIZE KERNEL_IMAGETYPE KERNEL_DEVICETREE KERNEL_CLASSES KERNEL_FEATURES
          MACHINE DISTRO DISTRO_VERSION DISTRO_FEATURES MACHINEOVERRIDES DISTROOVERRIDES MACHINE_FEATURES
          PREFERRED_PROVIDER PREFERRED_VERSION PREFERRED_RPROVIDER
          BBMASK BBLAYERS BBFILES BBPATH BB_HASHSERVE BB_SIGNATURE_HANDLER
          TARGET_ARCH TARGET_OS TARGET_CFLAGS TARGET_CXXFLAGS TARGET_LDFLAGS TARGET_CC TARGET_CXX TARGET_AR TARGET_RANLIB
          STAGING_DIR_TARGET STAGING_DIR_HOST STAGING_INCDIR STAGING_LIBDIR
          SYSROOT_DESTDIR DESTDIR bindir libdir includedir datadir sysconfdir systemd_unitdir systemd_system_unitdir
          SYSTEMD_SERVICE SYSTEMD_AUTO_ENABLE USERADD_PACKAGES USERADD_PARAM GROUPADD_PARAM
          CARGO_SRC_DIR CARGO_BUILD_ARGS CARGO_INSTALL_ARGS GO_IMPORT GO_INSTALL GO_WORKDIR
          TMPDIR DL_DIR SSTATE_DIR SSTATE_MIRRORS SOURCE_MIRROR_URL",
        // the language of a recipe
        @"inherit require include export python fakeroot def addtask deltask addhandler",
        // tasks
        @"do_fetch do_unpack do_patch do_configure do_compile do_install do_package do_package_write_ipk
          do_package_write_deb do_package_write_rpm do_deploy do_build do_populate_sysroot do_populate_lic
          do_rootfs do_image do_image_complete do_testimage");

    private static readonly TokenCache.LineTokenizer YoctoTokenizer = TokenizeYocto;

    /// <summary>
    /// Yocto/BitBake: the colours of Config with the BitBake variables and tasks as keywords. A task definition
    /// ("do_install() {") has no value after its first word, so it is recognised here before the Config tokenizer.
    /// </summary>
    private static bool TokenizeYocto(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        int first = SkipBlanks(text, 0);
        if (first < text.Length && text[first] != '#' && text[first] != ';')
        {
            int keyEnd = first;
            if (IsIdStart(text[keyEnd]))
            {
                while (keyEnd < text.Length && IsConfigKeyChar(text[keyEnd]))
                {
                    keyEnd++;
                }
            }
            int probe = SkipBlanks(text, keyEnd);
            bool hasValue = probe < text.Length && (text[probe] == '=' || text[probe] == ':' ||
                (probe > keyEnd && text[probe] != '#'));
            if (keyEnd > first && !hasValue && YoctoDirectives.Contains(text.Substring(first, keyEnd - first)))
            {
                sink.AddToken(TypeKeyword, first, keyEnd);
                endState = 0;
                return true;
            }
        }
        return TokenizeConfigCore(YoctoDirectives, startState, text, sink, out endState);
    }
}
