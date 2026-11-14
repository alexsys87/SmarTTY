using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

// The formats that administrators edit and read on a server: systemd units, nginx and ssh configuration, logs. The first three are
// the "Config" colours with the directives of the program known (a known directive is a keyword, so a misspelt one stands out, and the
// editor offers them as completions); logs get the colours of their timestamps and levels.
public static partial class SyntaxHighlighting
{
    /// <summary>A set of whole directives, some of which consist of several words ("map to guest").</summary>
    private static HashSet<string> Phrases(StringComparer comparer, string phrases)
    {
        HashSet<string> result = new HashSet<string>(comparer);
        foreach (string line in phrases.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string phrase = line.Trim();
            if (phrase.Length != 0)
            {
                result.Add(phrase);
            }
        }
        return result;
    }

    private static HashSet<string> Words(StringComparer comparer, params string[] groups)
    {
        HashSet<string> result = new HashSet<string>(comparer);
        foreach (string group in groups)
        {
            foreach (string word in group.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(word);
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- systemd

    private static readonly HashSet<string> SystemdDirectives = Words(StringComparer.Ordinal,
        // [Unit]
        @"Description Documentation Wants Requires Requisite BindsTo PartOf Upholds Conflicts Before After OnFailure OnSuccess
          PropagatesReloadTo ReloadPropagatedFrom PropagatesStopTo StopPropagatedFrom JoinsNamespaceOf RequiresMountsFor
          OnFailureJobMode IgnoreOnIsolate StopWhenUnneeded RefuseManualStart RefuseManualStop AllowIsolate DefaultDependencies
          SurviveFinalKillSignal CollectMode FailureAction SuccessAction FailureActionExitStatus SuccessActionExitStatus
          JobTimeoutSec JobRunningTimeoutSec JobTimeoutAction JobTimeoutRebootArgument StartLimitIntervalSec StartLimitBurst
          StartLimitAction RebootArgument SourcePath
          ConditionArchitecture ConditionFirmware ConditionVirtualization ConditionHost ConditionKernelCommandLine ConditionKernelVersion
          ConditionCredential ConditionEnvironment ConditionSecurity ConditionCapability ConditionACPower ConditionNeedsUpdate
          ConditionFirstBoot ConditionPathExists ConditionPathExistsGlob ConditionPathIsDirectory ConditionPathIsSymbolicLink
          ConditionPathIsMountPoint ConditionPathIsReadWrite ConditionPathIsEncrypted ConditionDirectoryNotEmpty ConditionFileNotEmpty
          ConditionFileIsExecutable ConditionUser ConditionGroup ConditionControlGroupController ConditionMemory ConditionCPUs
          ConditionCPUFeature ConditionOSRelease ConditionMemoryPressure ConditionCPUPressure ConditionIOPressure
          AssertArchitecture AssertVirtualization AssertHost AssertKernelCommandLine AssertSecurity AssertCapability AssertACPower
          AssertNeedsUpdate AssertFirstBoot AssertPathExists AssertPathExistsGlob AssertPathIsDirectory AssertPathIsSymbolicLink
          AssertPathIsMountPoint AssertPathIsReadWrite AssertDirectoryNotEmpty AssertFileNotEmpty AssertFileIsExecutable AssertUser
          AssertGroup AssertControlGroupController",
        // [Install]
        @"Alias WantedBy RequiredBy UpheldBy Also DefaultInstance",
        // [Service]: what runs and when it restarts
        @"Type ExecStart ExecStartPre ExecStartPost ExecCondition ExecReload ExecStop ExecStopPost Restart RestartSec RestartSteps
          RestartMaxDelaySec RestartPreventExitStatus RestartForceExitStatus SuccessExitStatus RemainAfterExit GuessMainPID PIDFile BusName
          NotifyAccess TimeoutStartSec TimeoutStopSec TimeoutAbortSec TimeoutSec TimeoutStartFailureMode TimeoutStopFailureMode
          RuntimeMaxSec RuntimeRandomizedExtraSec WatchdogSec FileDescriptorStoreMax FileDescriptorStorePreserve OOMPolicy
          USBFunctionDescriptors USBFunctionStrings OpenFile ExitType RootDirectoryStartOnly NonBlocking",
        // execution environment
        @"User Group DynamicUser SupplementaryGroups WorkingDirectory RootDirectory RootImage RootImageOptions RootHash RootVerity
          MountAPIVFS Environment EnvironmentFile PassEnvironment UnsetEnvironment StandardInput StandardOutput StandardError
          StandardInputText StandardInputData TTYPath TTYReset TTYVHangup TTYVTDisallocate SyslogIdentifier SyslogFacility SyslogLevel
          SyslogLevelPrefix LogLevelMax LogExtraFields LogRateLimitIntervalSec LogRateLimitBurst LogFilterPatterns LogNamespace
          Nice OOMScoreAdjust IOSchedulingClass IOSchedulingPriority CPUSchedulingPolicy CPUSchedulingPriority CPUSchedulingResetOnFork
          CPUAffinity NUMAPolicy NUMAMask UMask LimitCPU LimitFSIZE LimitDATA LimitSTACK LimitCORE LimitRSS LimitNOFILE LimitAS LimitNPROC
          LimitMEMLOCK LimitLOCKS LimitSIGPENDING LimitMSGQUEUE LimitNICE LimitRTPRIO LimitRTTIME Personality ExtensionImages
          MountImages BindLogSockets SetCredential LoadCredential LoadCredentialEncrypted ImportCredential SetLoginEnvironment",
        // sandboxing
        @"NoNewPrivileges PrivateTmp PrivateDevices PrivateNetwork PrivateUsers PrivateMounts PrivateIPC ProtectSystem ProtectHome
          ProtectKernelTunables ProtectKernelModules ProtectKernelLogs ProtectControlGroups ProtectClock ProtectHostname ProtectProc
          ProcSubset ReadWritePaths ReadOnlyPaths InaccessiblePaths ExecPaths NoExecPaths BindPaths BindReadOnlyPaths TemporaryFileSystem
          MountFlags RestrictAddressFamilies RestrictNamespaces RestrictRealtime RestrictSUIDSGID RestrictFileSystems
          MemoryDenyWriteExecute LockPersonality SystemCallFilter SystemCallArchitectures SystemCallErrorNumber SystemCallLog
          CapabilityBoundingSet AmbientCapabilities SecureBits RemoveIPC KeyringMode DevicePolicy DeviceAllow IPAddressAllow IPAddressDeny
          IPCNamespacePath NetworkNamespacePath PAMName UtmpIdentifier UtmpMode SELinuxContext AppArmorProfile SmackProcessLabel
          IgnoreSIGPIPE KillMode KillSignal RestartKillSignal FinalKillSignal SendSIGHUP SendSIGKILL WatchdogSignal",
        // directories
        @"RuntimeDirectory RuntimeDirectoryMode RuntimeDirectoryPreserve StateDirectory StateDirectoryMode CacheDirectory
          CacheDirectoryMode LogsDirectory LogsDirectoryMode ConfigurationDirectory ConfigurationDirectoryMode",
        // resource control
        @"Slice Delegate DelegateSubgroup CPUAccounting CPUWeight StartupCPUWeight CPUQuota CPUQuotaPeriodSec AllowedCPUs
          StartupAllowedCPUs MemoryAccounting MemoryMin MemoryLow MemoryHigh MemoryMax MemorySwapMax MemoryZSwapMax AllowedMemoryNodes
          TasksAccounting TasksMax IOAccounting IOWeight StartupIOWeight IODeviceWeight IOReadBandwidthMax IOWriteBandwidthMax
          IOReadIOPSMax IOWriteIOPSMax IODeviceLatencyTargetSec IPAccounting IPIngressFilterPath IPEgressFilterPath DisableControllers
          ManagedOOMSwap ManagedOOMMemoryPressure ManagedOOMMemoryPressureLimit ManagedOOMPreference",
        // [Socket]
        @"ListenStream ListenDatagram ListenSequentialPacket ListenFIFO ListenSpecial ListenNetlink ListenMessageQueue ListenUSBFunction
          SocketMode DirectoryMode Accept Writable MaxConnections MaxConnectionsPerSource KeepAlive KeepAliveTimeSec KeepAliveIntervalSec
          KeepAliveProbes NoDelay Priority ReceiveBuffer SendBuffer IPTOS IPTTL Mark ReusePort FreeBind Transparent Broadcast
          PassCredentials PassSecurity PassPacketInfo TCPCongestion SocketUser SocketGroup BindIPv6Only BindToDevice Backlog
          FileDescriptorName Service TriggerLimitIntervalSec TriggerLimitBurst RemoveOnStop Symlinks DeferTrigger PipeSize Timestamping
          SELinuxContextFromNet ExecStartPre ExecStartPost ExecStopPre ExecStopPost",
        // [Timer]
        @"OnActiveSec OnBootSec OnStartupSec OnUnitActiveSec OnUnitInactiveSec OnCalendar OnClockChange OnTimezoneChange AccuracySec
          RandomizedDelaySec FixedRandomDelay Persistent WakeSystem RemainAfterElapse Unit",
        // [Mount], [Automount], [Swap], [Path]
        @"What Where Options SloppyOptions LazyUnmount ReadWriteOnly ForceUnmount TimeoutIdleSec PathExists PathExistsGlob PathChanged
          PathModified DirectoryNotEmpty MakeDirectory");

    private static readonly string[] SystemdSectionNames =
    {
        "Unit", "Install", "Service", "Socket", "Timer", "Mount", "Automount", "Swap", "Path", "Slice", "Scope"
    };

    // Values that are typed often, offered with the directives (a word with a dash or a dot is not completed).
    private static readonly string[] SystemdValues =
    {
        "simple", "forking", "oneshot", "notify", "exec", "idle", "dbus", "always", "journal", "inherit", "null", "full", "strict", "tty",
        "syslog", "kmsg", "socket", "process", "mixed", "none"
    };

    // -------------------------------------------------------------------- nginx

    private static readonly HashSet<string> NginxDirectives = Words(StringComparer.Ordinal,
        // main, events, http, server, location
        @"user worker_processes worker_rlimit_nofile worker_priority worker_cpu_affinity worker_shutdown_timeout daemon master_process
          timer_resolution pcre_jit ssl_engine load_module env thread_pool lock_file pid error_log include events http server location
          upstream stream mail types map geo split_clients limit_except if set break return rewrite
          // njs
          js_import js_content js_set js_var js_periodic js_preload_object
          worker_connections use multi_accept accept_mutex accept_mutex_delay
          listen server_name server_name_in_redirect server_names_hash_bucket_size server_names_hash_max_size root alias index try_files
          error_page internal default_type charset charset_types source_charset override_charset
          absolute_redirect port_in_redirect merge_slashes recursive_error_pages msie_padding disable_symlinks satisfy allow deny
          sendfile sendfile_max_chunk tcp_nopush tcp_nodelay keepalive_timeout keepalive_requests keepalive_time keepalive keepalive_disable
          types_hash_max_size types_hash_bucket_size variables_hash_max_size variables_hash_bucket_size map_hash_max_size map_hash_bucket_size
          client_max_body_size client_body_buffer_size client_body_timeout client_body_temp_path client_body_in_file_only
          client_body_in_single_buffer client_header_buffer_size client_header_timeout large_client_header_buffers send_timeout send_lowat
          reset_timedout_connection lingering_close lingering_time lingering_timeout ignore_invalid_headers underscores_in_headers
          resolver resolver_timeout server_tokens open_file_cache open_file_cache_valid open_file_cache_min_uses open_file_cache_errors
          output_buffers postpone_output directio directio_alignment aio read_ahead max_ranges
          add_header add_trailer expires etag more_set_headers
          auth_basic auth_basic_user_file auth_request auth_request_set auth_delay
          limit_req limit_req_zone limit_req_status limit_req_log_level limit_req_dry_run limit_conn limit_conn_zone limit_conn_status
          limit_conn_log_level limit_rate limit_rate_after
          real_ip_header set_real_ip_from real_ip_recursive
          access_log log_format log_not_found log_subrequest rewrite_log open_log_file_cache
          autoindex autoindex_exact_size autoindex_localtime autoindex_format
          sub_filter sub_filter_once sub_filter_types sub_filter_last_modified add_before_body add_after_body addition_types
          mirror mirror_request_body stub_status valid_referers referer_hash_bucket_size referer_hash_max_size
          secure_link secure_link_md5 secure_link_secret random_index image_filter slice
          least_conn ip_hash hash random zone state sticky ntlm queue",
        // proxy
        @"proxy_pass proxy_set_header proxy_hide_header proxy_pass_header proxy_pass_request_headers proxy_pass_request_body
          proxy_redirect proxy_http_version proxy_buffering proxy_buffers proxy_buffer_size proxy_busy_buffers_size proxy_connect_timeout
          proxy_read_timeout proxy_send_timeout proxy_cache proxy_cache_path proxy_cache_valid proxy_cache_key proxy_cache_bypass
          proxy_cache_use_stale proxy_cache_lock proxy_cache_lock_timeout proxy_cache_methods proxy_cache_min_uses proxy_cache_revalidate
          proxy_cache_background_update proxy_no_cache proxy_ignore_headers proxy_ignore_client_abort proxy_next_upstream
          proxy_next_upstream_timeout proxy_next_upstream_tries proxy_intercept_errors proxy_request_buffering proxy_max_temp_file_size
          proxy_temp_file_write_size proxy_temp_path proxy_cookie_domain proxy_cookie_path proxy_cookie_flags proxy_bind proxy_socket_keepalive
          proxy_ssl_verify proxy_ssl_server_name proxy_ssl_name proxy_ssl_protocols proxy_ssl_ciphers proxy_ssl_certificate
          proxy_ssl_certificate_key proxy_ssl_trusted_certificate proxy_ssl_session_reuse proxy_ssl_verify_depth proxy_headers_hash_max_size
          proxy_headers_hash_bucket_size proxy_method proxy_set_body proxy_force_ranges proxy_store proxy_store_access",
        // fastcgi, uwsgi, scgi, grpc
        @"fastcgi_pass fastcgi_param fastcgi_index fastcgi_split_path_info fastcgi_read_timeout fastcgi_send_timeout fastcgi_connect_timeout
          fastcgi_buffers fastcgi_buffer_size fastcgi_busy_buffers_size fastcgi_intercept_errors fastcgi_keep_conn fastcgi_cache
          fastcgi_cache_path fastcgi_cache_key fastcgi_cache_valid fastcgi_cache_bypass fastcgi_no_cache fastcgi_hide_header
          fastcgi_ignore_headers fastcgi_pass_header fastcgi_temp_path fastcgi_catch_stderr fastcgi_next_upstream
          uwsgi_pass uwsgi_param uwsgi_read_timeout uwsgi_send_timeout uwsgi_connect_timeout uwsgi_buffers uwsgi_buffer_size uwsgi_modifier1
          uwsgi_cache uwsgi_cache_path uwsgi_cache_key uwsgi_cache_valid
          scgi_pass scgi_param scgi_read_timeout scgi_send_timeout scgi_connect_timeout
          grpc_pass grpc_set_header grpc_read_timeout grpc_send_timeout grpc_connect_timeout grpc_ssl_verify grpc_ssl_certificate",
        // ssl, http2, gzip, brotli
        @"ssl ssl_certificate ssl_certificate_key ssl_protocols ssl_ciphers ssl_prefer_server_ciphers ssl_session_cache ssl_session_timeout
          ssl_session_tickets ssl_session_ticket_key ssl_stapling ssl_stapling_verify ssl_stapling_file ssl_stapling_responder
          ssl_trusted_certificate ssl_dhparam ssl_ecdh_curve ssl_verify_client ssl_client_certificate ssl_verify_depth ssl_crl
          ssl_early_data ssl_reject_handshake ssl_buffer_size ssl_handshake_timeout ssl_password_file ssl_conf_command ssl_ocsp
          ssl_preread http2 http2_max_concurrent_streams http2_chunk_size http2_idle_timeout http3 http3_hq quic_retry quic_gso quic_bpf
          gzip gzip_types gzip_vary gzip_proxied gzip_comp_level gzip_min_length gzip_buffers gzip_disable gzip_http_version gzip_static
          gunzip gunzip_buffers brotli brotli_types brotli_comp_level brotli_static
          deflate_types");

    // Words that follow a directive and the names of variables ($host, $uri, ...): typed often.
    private static readonly string[] NginxValues =
    {
        "on", "off", "all", "none", "default", "always", "any", "close", "upgrade", "uri", "host", "scheme", "remote_addr", "request_uri",
        "http_upgrade", "proxy_add_x_forwarded_for", "binary_remote_addr", "request_method", "args", "break", "last", "permanent", "redirect",
        "default_server", "reuseport", "backlog", "weight", "max_fails", "fail_timeout", "backup", "down"
    };

    // ---------------------------------------------------------------- ssh, sshd

    private static readonly HashSet<string> SshDirectives = Words(StringComparer.OrdinalIgnoreCase,
        // sshd_config
        @"Port AddressFamily ListenAddress HostKey HostKeyAgent HostCertificate RekeyLimit SyslogFacility LogLevel LogVerbose LoginGraceTime
          PermitRootLogin StrictModes MaxAuthTries MaxSessions PubkeyAuthentication AuthorizedKeysFile AuthorizedKeysCommand
          AuthorizedKeysCommandUser AuthorizedPrincipalsFile AuthorizedPrincipalsCommand AuthorizedPrincipalsCommandUser TrustedUserCAKeys
          RevokedKeys HostbasedAuthentication IgnoreUserKnownHosts IgnoreRhosts PasswordAuthentication PermitEmptyPasswords
          ChallengeResponseAuthentication KbdInteractiveAuthentication KerberosAuthentication KerberosOrLocalPasswd KerberosTicketCleanup
          KerberosGetAFSToken GSSAPIAuthentication GSSAPICleanupCredentials GSSAPIStrictAcceptorCheck GSSAPIKeyExchange UsePAM
          AllowAgentForwarding AllowTcpForwarding AllowStreamLocalForwarding GatewayPorts X11Forwarding X11DisplayOffset X11UseLocalhost
          PermitTTY PrintMotd PrintLastLog TCPKeepAlive PermitUserEnvironment PermitUserRC Compression ClientAliveInterval ClientAliveCountMax
          UseDNS PidFile MaxStartups PermitTunnel ChrootDirectory ForceCommand Banner AcceptEnv Subsystem Match AllowUsers AllowGroups
          DenyUsers DenyGroups PermitOpen PermitListen ExposeAuthInfo StreamLocalBindMask StreamLocalBindUnlink UnusedConnectionTimeout
          RequiredRSASize DisableForwarding PerSourcePenalties PerSourceMaxStartups PerSourceNetBlockSize ModuliFile RDomain
          AuthenticationMethods PubkeyAuthOptions VersionAddendum SecurityKeyProvider",
        // both
        @"Ciphers MACs KexAlgorithms HostKeyAlgorithms PubkeyAcceptedAlgorithms PubkeyAcceptedKeyTypes CASignatureAlgorithms FingerprintHash
          IPQoS Include SetEnv",
        // ssh_config
        @"Host HostName User IdentityFile IdentitiesOnly IdentityAgent ProxyJump ProxyCommand ForwardAgent ForwardX11 ForwardX11Trusted
          ForwardX11Timeout LocalForward RemoteForward DynamicForward ServerAliveInterval ServerAliveCountMax StrictHostKeyChecking
          UserKnownHostsFile GlobalKnownHostsFile HashKnownHosts VerifyHostKeyDNS UpdateHostKeys CheckHostIP ControlMaster ControlPath
          ControlPersist ConnectTimeout ConnectionAttempts BatchMode AddKeysToAgent CanonicalizeHostname CanonicalDomains
          CanonicalizeFallbackLocal CanonicalizeMaxDots CanonicalizePermittedCNAMEs CertificateFile EscapeChar ExitOnForwardFailure
          NumberOfPasswordPrompts PreferredAuthentications RequestTTY RemoteCommand SendEnv Tunnel TunnelDevice VisualHostKey
          GSSAPIDelegateCredentials AddressFamily BindAddress BindInterface ClearAllForwardings EnableSSHKeysign ForkAfterAuthentication
          HostKeyAlias KnownHostsCommand LocalCommand PermitLocalCommand NoHostAuthenticationForLocalhost ObscureKeystrokeTiming
          PKCS11Provider ProxyUseFdpass RevokedHostKeys SessionType StdinNull XAuthLocation Protocol");

    private static readonly string[] SshValues =
    {
        "yes", "no", "ask", "confirm", "auto", "autoask", "force", "any", "inet", "inet6", "all", "none", "local", "delay", "publickey",
        "password", "hostbased", "always", "never", "dynamic"
    };

    // ---------------------------------------------------------------- crontab

    private static readonly HashSet<string> CrontabKeywords = Words(StringComparer.Ordinal,
        // shortcuts in place of the five fields
        @"@reboot @yearly @annually @monthly @weekly @daily @midnight @hourly",
        // names of months and days of the week
        @"jan feb mar apr may jun jul aug sep oct nov dec
          sun mon tue wed thu fri sat",
        // the environment names a crontab may set
        @"SHELL PATH MAILTO HOME LOGNAME CRON_TZ RANDOM_DELAY MLS_LEVEL CONTENT_TYPE MAILFROM");

    /// <summary>
    /// cron/crontab: a comment, an environment assignment (SHELL, PATH, MAILTO), a @reboot-style shortcut or the five
    /// schedule fields followed by the command. The schedule fields are numbers (or keywords for *, ranges and names);
    /// the command is left as it is except for its quoted strings and its trailing comment.
    /// </summary>
    private static bool TokenizeCrontab(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        endState = 0;
        int len = text.Length;
        int first = SkipBlanks(text, 0);
        if (first >= len)
        {
            return true;
        }
        if (text[first] == '#')
        {
            sink.AddToken(TypeComment, first, len);
            return true;
        }

        // An environment assignment: NAME=value, NAME = value.
        int wordEnd = first;
        while (wordEnd < len && (IsIdChar(text[wordEnd]) || text[wordEnd] == '@'))
        {
            wordEnd++;
        }
        if (wordEnd > first && IsIdStart(text[first]))
        {
            int equals = SkipBlanks(text, wordEnd);
            if (equals < len && text[equals] == '=')
            {
                sink.AddToken(TypeVariable, first, wordEnd);
                ScanCronRest(text, SkipBlanks(text, equals + 1), sink);
                return true;
            }
        }

        // @reboot, @daily and the other shortcuts.
        if (text[first] == '@')
        {
            string shortcut = text.Substring(first, wordEnd - first);
            if (CrontabKeywords.Contains(shortcut))
            {
                sink.AddToken(TypeKeyword, first, wordEnd);
                ScanCronRest(text, SkipBlanks(text, wordEnd), sink);
                return true;
            }
        }

        // The five fields: minute, hour, day of month, month, day of the week.
        int i = first;
        for (int field = 0; field < 5 && i < len; field++)
        {
            int s = i;
            while (i < len && !IsBlank(text[i]))
            {
                i++;
            }
            if (i == s)
            {
                break;
            }
            sink.AddToken(IsDigit(text[s]) ? TypeNumber : TypeKeyword, s, i);
            i = SkipBlanks(text, i);
        }
        ScanCronRest(text, i, sink);
        return true;
    }

    /// <summary>The command of a cron line: quoted strings and a trailing comment.</summary>
    private static void ScanCronRest(string text, int start, TokenCache.ITokenSink sink)
    {
        int len = text.Length;
        int i = start;
        while (i < len)
        {
            char c = text[i];
            if (c == '#' && i > 0 && IsBlank(text[i - 1]))
            {
                sink.AddToken(TypeComment, i, len);
                return;
            }
            if (c == '"' || c == '\'')
            {
                int end = FindQuoteEnd(text, i, c);
                sink.AddToken(TypeString, i, (end < 0) ? len : end);
                i = (end < 0) ? len : end;
                continue;
            }
            i++;
        }
    }

    // ------------------------------------------------------------------ fstab

    private static readonly HashSet<string> FstabTypes = Words(StringComparer.Ordinal,
        @"ext2 ext3 ext4 xfs btrfs zfs vfat fat msdos exfat ntfs ntfs3 iso9660 udf f2fs reiserfs jfs nilfs2
          bcachefs squashfs erofs overlay tmpfs devtmpfs ramfs proc sysfs devpts cgroup cgroup2
          securityfs debugfs tracefs pstore bpf configfs fusectl mqueue hugetlbfs autofs
          nfs nfs4 cifs smb3 fuse fuse.sshfs sshfs glusterfs ceph lustre gpfs 9p virtiofs
          swap none auto");

    private static readonly HashSet<string> FstabOptions = Words(StringComparer.Ordinal,
        @"defaults rw ro suid nosuid dev nodev exec noexec auto noauto user nouser users owner group
          sync async atime noatime relatime norelatime strictatime lazytime nodiratime diratime
          discard nodiscard fail nofail _netdev
          umask dmask fmask uid gid mode acl noacl
          errors x-systemd.automount x-systemd.device-timeout x-systemd.idle-timeout
          x-systemd.growfs x-systemd.makefs x-systemd.mount-timeout
          x-systemd.required-by x-systemd.wanted-by x-systemd.before x-systemd.after
          soft hard intr nointr timeo retrans rsize wsize nolock lock
          vers proto tcp udp port mountproto mountport
          credentials guest sec iocharset codepage comment
          size nr_inodes bind rbind move remount compress compress-force subvol subvolid");

    /// <summary>
    /// /etc/fstab: the device, the mount point, the filesystem, the options, the dump and the pass fields. A known
    /// filesystem and option are keywords, an unknown one is a plain variable (so a typo stands out), the dump and pass
    /// columns are numbers, and a trailing # starts a comment.
    /// </summary>
    private static bool TokenizeFstab(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        endState = 0;
        int len = text.Length;
        int i = SkipBlanks(text, 0);
        if (i >= len)
        {
            return true;
        }
        if (text[i] == '#')
        {
            sink.AddToken(TypeComment, i, len);
            return true;
        }

        for (int field = 0; field < 6 && i < len; field++)
        {
            if (text[i] == '#')
            {
                sink.AddToken(TypeComment, i, len);
                return true;
            }
            int s = i;
            while (i < len && !IsBlank(text[i]))
            {
                i++;
            }
            if (i == s)
            {
                break;
            }
            if (field == 0 || field == 1)
            {
                sink.AddToken(TypeString, s, i);
            }
            else if (field == 2)
            {
                sink.AddToken(FstabTypes.Contains(text.Substring(s, i - s)) ? TypeKeyword : TypeVariable, s, i);
            }
            else if (field == 3)
            {
                ScanFstabOptions(text, s, i, sink);
            }
            else
            {
                sink.AddToken(TypeNumber, s, i);
            }
            i = SkipBlanks(text, i);
        }
        if (i < len && text[i] == '#')
        {
            sink.AddToken(TypeComment, i, len);
        }
        return true;
    }

    /// <summary>The mount options of a fstab line: known options are keywords, unknown ones are variables.</summary>
    private static void ScanFstabOptions(string text, int start, int end, TokenCache.ITokenSink sink)
    {
        int s = start;
        for (int i = start; i <= end; i++)
        {
            if (i < end && text[i] != ',')
            {
                continue;
            }
            if (i > s)
            {
                string option = text.Substring(s, i - s);
                int equals = option.IndexOf('=');
                string name = (equals > 0) ? option.Substring(0, equals) : option;
                sink.AddToken(FstabOptions.Contains(name) ? TypeKeyword : TypeVariable, s, i);
            }
            s = i + 1;
        }
    }

    // ---------------------------------------------------------------- sudoers

    private static readonly HashSet<string> SudoersDirectives = Words(StringComparer.OrdinalIgnoreCase,
        @"Defaults User_Alias Runas_Alias Host_Alias Cmnd_Alias");

    private static readonly HashSet<string> SudoersValues = Words(StringComparer.OrdinalIgnoreCase,
        // tags of a command
        @"ALL NOPASSWD PASSWD NOEXEC EXEC SETENV NOSETENV LOG_INPUT NOLOG_INPUT LOG_OUTPUT NOLOG_OUTPUT
          MAIL NOMAIL FOLLOW NOFOLLOW",
        // Defaults flags
        @"env_reset env_keep env_check env_file secure_path timestamp_timeout timestamp_type passwd_tries passwd_timeout
          umask insults requiretty mail_badpass mail_no_user mail_no_perms mail_always mail_no_host use_pty
          logfile log_input log_output syslog syslog_goodpri syslog_badpri lecture lecture_file always_set_home set_home
          preserve_groups targetpw rootpw runaspw visiblepw noexec fqdn hostname command_timeout maxseq fast_glob glob
          shell_noargs setenv noenv editor env_editor verifypw listpw pwfeedback stay_setuid closefrom
          restrict_inherit nohost authenticate intercept log_subcmds log_servers log_server_timeout iolog_dir iolog_file
          utmp_runas case_insensitive");

    /// <summary>
    /// sudoers: the colours of Config with the directives (Defaults, the *_Alias definitions), the tags (NOPASSWD,
    /// PASSWD, SETENV) and the Defaults flags known. "#include" and "@include" are directives of the file, not comments.
    /// </summary>
    private static bool TokenizeSudoers(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        endState = 0;
        int first = SkipBlanks(text, 0);
        if (first < text.Length && (text[first] == '#' || text[first] == '@'))
        {
            int wordEnd = first + 1;
            while (wordEnd < text.Length && IsIdChar(text[wordEnd]))
            {
                wordEnd++;
            }
            string word = text.Substring(first + 1, wordEnd - first - 1);
            if (word == "include" || word == "includedir")
            {
                sink.AddToken(TypeKeyword, first, wordEnd);
                int pathStart = SkipBlanks(text, wordEnd);
                int pathEnd = pathStart;
                while (pathEnd < text.Length && !IsBlank(text[pathEnd]))
                {
                    pathEnd++;
                }
                if (pathEnd > pathStart)
                {
                    sink.AddToken(TypeString, pathStart, pathEnd);
                }
                ScanCronRest(text, pathEnd, sink);
                return true;
            }
        }
        return TokenizeConfigCore(SudoersDirectives, startState, text, sink, out endState, valueKeywords: SudoersValues);
    }

    // ----------------------------------------------------------- MySQL/MariaDB

    private static readonly HashSet<string> MySqlOptions = Words(StringComparer.OrdinalIgnoreCase,
        // server, client and replication settings of my.cnf / my.ini
        @"user port socket bind-address admin-address datadir basedir tmpdir pid-file plugin-dir
          log-error log-error-verbosity log-warnings general-log general_log general-log-file general_log_file
          slow-query-log slow_query_log slow-query-log-file slow_query_log_file long-query-time long_query_time
          log-queries-not-using-indexes log-slow-admin-statements log-slow-replica-statements
          max-connections max_connections max-user-connections max_user_connections max-allowed-packet max_allowed_packet
          max-connect-errors max_connect_errors connect-timeout connect_timeout wait-timeout wait_timeout
          interactive-timeout interactive_timeout net-read-timeout net_read_timeout net-write-timeout net_write_timeout
          net-buffer-length net_buffer_length thread-cache-size thread_cache_size thread-stack thread_stack
          table-open-cache table_open_cache table-definition-cache table_definition_cache open-files-limit open_files_limit
          key-buffer-size key_buffer_size sort-buffer-size sort_buffer_size join-buffer-size join_buffer_size
          read-buffer-size read_buffer_size read-rnd-buffer-size read_rnd_buffer_size
          tmp-table-size tmp_table_size max-heap-table-size max_heap_table_size
          bulk-insert-buffer-size bulk_insert_buffer_size myisam-recover-options myisam_sort_buffer_size
          innodb-buffer-pool-size innodb_buffer_pool_size innodb-buffer-pool-instances innodb_buffer_pool_instances
          innodb-buffer-pool-chunk-size innodb_buffer_pool_chunk_size innodb-log-buffer-size innodb_log_buffer_size
          innodb-log-file-size innodb_log_file_size innodb-redo-log-capacity innodb_redo_log_capacity
          innodb-flush-log-at-trx-commit innodb_flush_log_at_trx_commit innodb-flush-method innodb_flush_method
          innodb-file-per-table innodb_file_per_table innodb-io-capacity innodb_io_capacity
          innodb-io-capacity-max innodb_io_capacity_max innodb-read-io-threads innodb_read_io_threads
          innodb-write-io-threads innodb_write_io_threads innodb-thread-concurrency innodb_thread_concurrency
          innodb-lock-wait-timeout innodb_lock_wait_timeout innodb-buffer-pool-dump-at-shutdown innodb_buffer_pool_dump_at_shutdown
          innodb-buffer-pool-load-at-startup innodb_buffer_pool_load_at_startup innodb-strict-mode innodb_strict_mode
          innodb-autoinc-lock-mode innodb_autoinc_lock_mode innodb-page-size innodb_page_size
          innodb-rollback-on-timeout innodb_rollback_on_timeout
          character-set-server character_set_server collation-server collation_server
          character-set-client character_set_client character-set-results character_set_results
          default-storage-engine default_storage_engine default-authentication-plugin default_authentication_plugin
          authentication-policy authentication_policy
          skip-name-resolve skip-networking skip-grant-tables skip-external-locking skip-symbolic-links
          bind-address secure-file-priv secure_file_priv local-infile local_infile protocol
          server-id server_id server-id-bits server_id_bits
          log-bin log_bin log-bin-index log_bin_index log-bin-trust-function-creators log_bin_trust_function_creators
          binlog-format binlog_format binlog-expire-logs-seconds binlog_expire_logs_seconds expire-logs-days expire_logs_days
          sync-binlog sync_binlog max-binlog-size max_binlog_size max-binlog-cache-size max_binlog_cache_size
          relay-log relay_log relay-log-index relay_log_index relay-log-info-file relay_log_info_file
          log-replica-updates log_replica_updates log-slave-updates log_slave_updates
          replicate-do-db replicate_do_db replicate-ignore-db replicate_ignore_db
          replicate-do-table replicate_do_table replicate-ignore-table replicate_ignore_table
          replicate-wild-do-table replicate_wild_do_table replicate-wild-ignore-table replicate_wild_ignore_table
          read-only read_only super-read-only super_read_only
          auto-increment-increment auto_increment_increment auto-increment-offset auto_increment_offset
          sql-mode sql_mode optimizer-switch optimizer_switch transaction-isolation transaction_isolation
          lock-wait-timeout lock_wait_timeout
          lower-case-table-names lower_case_table_names lc-messages-dir lc_messages_dir
          event-scheduler event_scheduler performance-schema performance_schema
          ssl-ca ssl_cert ssl_key ssl-cipher ssl_crl ssl_crlpath tls-version tls_version
          require-secure-transport require_secure_transport
          wsrep-on wsrep_on wsrep-provider wsrep_provider wsrep-cluster-name wsrep_cluster_name
          wsrep-cluster-address wsrep_cluster_address wsrep-node-address wsrep_node_address wsrep-node-name wsrep_node_name
          galera-provider-options galera_provider_options
          super-user super_user max-seeks-for-key max_seeks_for_key
          delayed-insert-timeout delayed_insert_timeout low-priority-updates low_priority_updates
          concurrent-insert concurrent_insert div-precision-increment div_precision_increment
          group-concat-max-len group_concat_max_len");

    private static readonly HashSet<string> MySqlGroups = Words(StringComparer.OrdinalIgnoreCase,
        @"mysqld mysqld_safe mysqldump mysql client server mariadb mariadbd mariadb-client mariadb-server
          embedded client-server client-mariadb galera mariadb-galera");

    /// <summary>The options of my.cnf plus their "loose-" and "loose_" forms: a loose option may be unknown to the server.</summary>
    private static readonly HashSet<string> MySqlDirectives = WithLoosePrefixes(MySqlOptions, MySqlGroups);

    private static HashSet<string> WithLoosePrefixes(HashSet<string> options, params HashSet<string>[] extra)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HashSet<string> set in extra)
        {
            result.UnionWith(set);
        }
        foreach (string option in options)
        {
            result.Add(option);
            result.Add("loose-" + option);
            result.Add("loose_" + option);
        }
        return result;
    }

    private static readonly HashSet<string> MySqlValues = Words(StringComparer.OrdinalIgnoreCase,
        @"innodb myisam csv memory archive blackhole ndb aria tokudb rocksdb mysql mariadb
          row statement mixed
          read-uncommitted read-committed repeatable-read serializable
          latin1 utf8 utf8mb3 utf8mb4 utf16 utf16le utf32 ucs2 binary ascii
          general_log slow_query_log
          mysql_native_password caching_sha2_password ed25519 unix_socket
          strict_trans_tables strict_all_tables no_zero_date no_zero_in_date
          on off true false yes no none");

    /// <summary>
    /// MySQL/MariaDB: the colours of Config with the options of my.cnf / my.ini known, and "!include"/"!includedir"
    /// recognised as directives of the file rather than as ordinary values.
    /// </summary>
    private static bool TokenizeMySql(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        endState = 0;
        int first = SkipBlanks(text, 0);
        if (first < text.Length && text[first] == '!')
        {
            int wordEnd = first + 1;
            while (wordEnd < text.Length && IsIdChar(text[wordEnd]))
            {
                wordEnd++;
            }
            string word = text.Substring(first + 1, wordEnd - first - 1);
            if (word == "include" || word == "includedir")
            {
                sink.AddToken(TypeKeyword, first, wordEnd);
                int pathStart = SkipBlanks(text, wordEnd);
                int pathEnd = pathStart;
                while (pathEnd < text.Length && !IsBlank(text[pathEnd]))
                {
                    pathEnd++;
                }
                if (pathEnd > pathStart)
                {
                    sink.AddToken(TypeString, pathStart, pathEnd);
                }
                ScanCronRest(text, pathEnd, sink);
                return true;
            }
        }
        return TokenizeConfigCore(MySqlDirectives, startState, text, sink, out endState, valueKeywords: MySqlValues);
    }

    // ------------------------------------------------------------------ Redis

    private static readonly HashSet<string> RedisDirectives = Words(StringComparer.OrdinalIgnoreCase,
        @"include loadmodule bind protected-mode port tcp-backlog unixsocket unixsocketperm timeout tcp-keepalive
          daemonize supervised pidfile loglevel logfile syslog-enabled syslog-ident syslog-facility crash-log-enabled
          databases always-show-logo set-proc-title proc-title-template locale-collate
          save stop-writes-on-bgsave-error rdbcompression rdbchecksum dbfilename rdb-del-sync-files dir
          replicaof slaveof masterauth masteruser replica-serve-stale-data replica-read-only
          repl-diskless-sync repl-diskless-sync-delay repl-diskless-load repl-disable-tcp-nodelay replica-priority
          min-replicas-to-write min-replicas-max-lag min-slaves-to-write min-slaves-max-lag
          aclfile requirepass user rename-command
          maxclients maxmemory maxmemory-policy maxmemory-samples maxmemory-clients
          lazyfree-lazy-eviction lazyfree-lazy-expire lazyfree-lazy-server-del replica-lazy-flush lazyfree-lazy-user-del
          lazyfree-lazy-user-flush
          appendonly appendfilename appenddirname appendfsync no-appendfsync-on-rewrite auto-aof-rewrite-percentage
          auto-aof-rewrite-min-size aof-load-truncated aof-use-rdb-preamble aof-timestamp-enabled
          slowlog-log-slower-than slowlog-max-len latency-monitor-threshold latency-tracking
          latency-tracking-info-percentiles
          notify-keyspace-events hash-max-listpack-entries hash-max-ziplist-entries hash-max-listpack-value
          hash-max-ziplist-value list-max-listpack-size list-max-ziplist-size list-compress-depth
          set-max-intset-entries set-max-listpack-entries set-max-listpack-value
          zset-max-listpack-entries zset-max-ziplist-entries zset-max-listpack-value zset-max-ziplist-value
          hll-sparse-max-bytes stream-node-max-bytes stream-node-max-entries
          activerehashing client-output-buffer-limit hz dynamic-hz aof-rewrite-incremental-fsync
          rdb-save-incremental-fsync jemalloc-bg-thread activedefrag
          sentinel monitor down-after-milliseconds failover-timeout parallel-syncs auth-pass auth-user
          resolve-hostnames announce-hostnames announce-ip announce-port deny-scripts-reconfig
          notification-script client-reconfig-script config-epoch leader-epoch current-epoch
          known-replica known-sentinel rename-command sentinel-user sentinel-pass myid cluster-name");

    /// <summary>The two-word commands of sentinel.conf: the whole "sentinel monitor" is coloured as one keyword.</summary>
    private static readonly HashSet<string> RedisSentinelPhrases = Phrases(StringComparer.OrdinalIgnoreCase,
        @"sentinel
          sentinel monitor
          sentinel down-after-milliseconds
          sentinel failover-timeout
          sentinel parallel-syncs
          sentinel auth-pass
          sentinel auth-user
          sentinel resolve-hostnames
          sentinel announce-hostnames
          sentinel announce-ip
          sentinel announce-port
          sentinel deny-scripts-reconfig
          sentinel notification-script
          sentinel client-reconfig-script
          sentinel config-epoch
          sentinel leader-epoch
          sentinel current-epoch
          sentinel known-replica
          sentinel known-sentinel
          sentinel rename-command
          sentinel sentinel-user
          sentinel sentinel-pass
          sentinel myid
          sentinel cluster-name");

    private static readonly HashSet<string> RedisValues = Words(StringComparer.OrdinalIgnoreCase,
        @"yes no always everysec debug verbose notice warning
          noeviction allkeys-lru allkeys-lfu allkeys-random volatile-lru volatile-lfu volatile-random volatile-ttl
          on off nopass resetpass allkeys allchannels all
          normal slave replica master");

    /// <summary>
    /// Redis: the colours of Config with the directives of redis.conf and sentinel.conf known, the "sentinel monitor" commands
    /// of Sentinel coloured as whole phrases, and the values of the eviction policies, appendfsync and loglevel modes coloured
    /// as keywords.
    /// </summary>
    private static bool TokenizeRedis(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(RedisDirectives, startState, text, sink, out endState, valueKeywords: RedisValues, phraseDirectives: RedisSentinelPhrases);
    }

    // ---------------------------------------------------------------- HAProxy

    private static readonly HashSet<string> HAProxyDirectives = Words(StringComparer.OrdinalIgnoreCase,
        // sections
        @"global defaults frontend backend listen peers resolvers userlist program cache http-errors ring log-forward mailers
          fcgi-app httpclient traces acme",
        // global and defaults
        @"log log-format log-format-sd log-tag maxconn daemon user group uid gid chroot pidfile
          stats nbproc nbthread cpu-map master-worker mworker-max-reloads expose-fd
          mode balance hash-type server default-server default_backend bind server-template
          timeout retries option acl use_backend
          http-request http-response http-after-response http-check redirect
          tcp-request tcp-response tcp-check capture cookie stick-table stick store-request store-response
          compression filter errorfile errorloc errorloc302 errorloc303 unique-id-header unique-id-format
          dispatch source monitor-uri external-check
          peers peer nameserver parse-resolv-conf resolve_retries
          userlist program command ring size format log-forward");

    private static readonly HashSet<string> HAProxyValues = Words(StringComparer.OrdinalIgnoreCase,
        // the words a section may stand alone with, and the choices of the directives
        @"global defaults frontend backend listen peers resolvers userlist program cache http-errors ring log-forward mailers
          fcgi-app httpclient traces acme
          daemon master-worker
          http tcp health
          roundrobin static-rr leastconn first source uri url_param rdp-cookie random
          inter fastinter downinter rise fall observe
          connect client server queue tunnel http-request http-keep-alive
          check ssl verify none required prefer
          enabled disabled normal admin drain enable disable
          on off yes no
          local0 local1 local2 local3 local4 local5 local6 local7
          GET POST HEAD PUT DELETE OPTIONS");

    /// <summary>
    /// HAProxy: the colours of Config with the sections (global, defaults, frontend, backend, listen, ...), the
    /// directives and their common values known. A section name that stands alone is a keyword too.
    /// </summary>
    private static bool TokenizeHAProxy(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(HAProxyDirectives, startState, text, sink, out endState, valueKeywords: HAProxyValues);
    }

    // ------------------------------------------------------------ Apache httpd

    private static readonly HashSet<string> ApacheDirectives = Words(StringComparer.OrdinalIgnoreCase,
        @"ServerRoot ServerName ServerAdmin ServerAlias ServerPath UseCanonicalName
          Listen PidFile Mutex ScoreBoardFile Timeout KeepAlive MaxKeepAliveRequests KeepAliveTimeout
          User Group LoadModule LoadFile
          DocumentRoot DirectoryIndex Options AllowOverride Require Order Allow Deny
          ErrorLog CustomLog LogLevel LogFormat TransferLog ScriptLog
          Include IncludeOptional
          TypesConfig AddType AddEncoding AddLanguage AddHandler AddCharset AddDefaultCharset DefaultType
          AccessFileName SetHandler
          Directory Files FileMatch Location LocationMatch VirtualHost IfModule IfDefine IfVersion ElseIf Else
          Define UnDefine If
          Limit LimitExcept Proxy
          ProxyRequests ProxyPass ProxyPassReverse ProxyPreserveHost ProxyTimeout ProxyBadHeader ProxyVia
          RewriteEngine RewriteBase RewriteCond RewriteRule RewriteMap RewriteOptions
          Header RequestHeader SetEnvIf SetEnvIfNoCase BrowserMatch
          SSLEngine SSLProtocol SSLCipherSuite SSLCertificateFile SSLCertificateKeyFile SSLCertificateChainFile
          SSLVerifyClient SSLVerifyDepth
          AuthType AuthName AuthUserFile AuthGroupFile AuthBasicProvider
          AddDefaultCharset ErrorDocument Alias ScriptAlias
          ExtendedStatus AllowEncodedSlashes AcceptPathInfo EnableMMAP EnableSendfile
          FileETag HostnameLookups NameVirtualHost
          MaxRequestWorkers MaxConnectionsPerChild StartServers MinSpareThreads MaxSpareThreads ThreadsPerChild
          ServerLimit ThreadLimit StartThreads MinSpareServers MaxSpareServers MaxClients MaxRequestsPerChild");

    private static readonly HashSet<string> ApacheValues = Words(StringComparer.OrdinalIgnoreCase,
        // choices of the directives
        @"on off all none granted denied valid-user
          Indexes FollowSymLinks SymLinksIfOwnerMatch ExecCGI Includes IncludesNOEXEC MultiViews
          All None
          emerg alert crit error warn notice info debug trace1 trace2 trace3 trace4 trace5 trace6 trace7 trace8
          Directory Files FileMatch Location LocationMatch VirtualHost IfModule IfDefine IfVersion ElseIf Else
          If
          Limit LimitExcept Proxy
          GET POST HEAD PUT DELETE OPTIONS");

    /// <summary>
    /// Apache httpd: the colours of Config with the directives of httpd.conf, apache2.conf and .htaccess known, and
    /// the section names of &lt;Directory&gt;, &lt;VirtualHost&gt; and the other containers highlighted as words too.
    /// </summary>
    private static bool TokenizeApache(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(ApacheDirectives, startState, text, sink, out endState, valueKeywords: ApacheValues);
    }

    // -------------------------------------------------------- systemd-networkd

    private static readonly HashSet<string> SystemdNetworkdDirectives = Words(StringComparer.OrdinalIgnoreCase,
        @"Name MACAddress Driver Type Kind Path Property Host Virtual
          MACAddressPolicy NamePolicy AddressPolicy Address AlternativeName AlternativeNames
          DHCP DHCPv4 DHCPv6 DHCPServer DHCPServerStaticLease Address Gateway DNS Domains NTP
          LinkLocalAddressing IPv4LLRoute IPv6AcceptRA IPv6DuplicateAddressDetection IPv6PrivacyExtensions
          IPv6SendRA IPv6Token IPv6MTUBytes IPv6StableSecretAddress IPv6LinkLocalAddressGenerationMode
          ConfigureWithoutCarrier IgnoreCarrierLoss KeepConfiguration RequiredForOnline RequiredFamilyForOnline
          BindCarrier VLAN Bridge Bond Tunnel VXLAN MACsec IPoIB CAN MultiqueueSRv6
          Peer Broadcast Label PreferredLifetime ValidLifetime Scope
          Destination Source Metric Table Protocol Type
          ClientIdentifier VendorClassIdentifier UseDNS UseNTP UseDomains UseHostname UseRoutes RouteMetric
          UseMTU UseGateway Token PoolOffset PoolSize EmitDNS EmitNTP EmitRouter Router EmitTimezone Timezone
          PrefixDelegationHint
          STP Priority ForwardDelay HelloTime MaxAge AgeingTime VLANFiltering
          Id Mode MIIMonitorSec LACPTransmitRate UpDelaySec DownDelaySec
          Local Remote TTL
          RouterLifetime
          PrivateKey ListenPort PublicKey PresharedKey AllowedIPs Endpoint PersistentKeepalive");

    private static readonly HashSet<string> SystemdNetworkdValues = Words(StringComparer.OrdinalIgnoreCase,
        @"yes no on off true false ipv4 ipv6 both none static dhcp routable carrier up down any
          promiscuous auto forever link-local global host site universe nowhere success degraded
          stable-privacy mac random persistent onlink mtu unspecified unicast blackhole unreachable
          prohibit throw local main default");

    /// <summary>
    /// systemd-networkd: the colours of Config with the keys of .network, .netdev and .link files known. The sections
    /// ([Match], [Network], [Address], [Route], ...) are coloured by the Config tokenizer.
    /// </summary>
    private static bool TokenizeSystemdNetworkd(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(SystemdNetworkdDirectives, startState, text, sink, out endState, valueKeywords: SystemdNetworkdValues);
    }

    // ---------------------------------------------------------------- ifupdown

    private static readonly HashSet<string> IfupdownDirectives = Words(StringComparer.Ordinal,
        // stanzas and includes
        @"auto iface allow-hotplug allow-auto source source-directory mapping
          no-auto-down no-scripts rename template
          address netmask gateway broadcast network metric mtu hwaddress pointopoint scope
          pre-up up post-up pre-down down post-down
          dns-nameservers dns-search dns-domain dns-sortlist
          bridge_ports bridge_stp bridge_fd bridge_maxwait bridge_maxage bridge_hello bridge_priority
          bridge_ageing bridge_bridgeprio bridge_gcint bridge_hw bridge_pathcost bridge_portcost bridge_waitport
          bond-slaves bond-mode bond-miimon bond-downdelay bond-updelay bond-lacp-rate bond-xmit-hash-policy
          bond-primary bond-primary-reselect
          vlan-raw-device vlan_id
          wpa-ssid wpa-psk wpa-conf wpa-driver wpa-key-mgmt wpa-ap-scan wpa-proto wpa-pairwise wpa-group
          wpa-eap wpa-identity wpa-password wpa-phase2 wpa-ca-cert wpa-client-cert wpa-private-key
          wpa-private-key-password
          accept_ra autoconf privext dhcp request
          tunnel-mode tunnel-local tunnel-remote tunnel-ttl tunnel-mtu
          preferred-lifetime valid-lifetime");

    private static readonly HashSet<string> IfupdownValues = Words(StringComparer.Ordinal,
        // address families, methods and the usual values of the options
        @"inet inet6 ipx can ppp loopback static manual dhcp bootp wvdial tunnel v4tunnel 6to4 ipv6auto ipv6dhcp
          yes no on off true false
          ether
          noauth open shared key
          wep wpa wpa2 wpa3");

    /// <summary>
    /// Debian/OpenWrt ifupdown: the colours of Config with the stanzas (auto, iface, source) and the options of
    /// /etc/network/interfaces known; the address families and methods are coloured wherever they stand.
    /// </summary>
    private static bool TokenizeIfupdown(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(IfupdownDirectives, startState, text, sink, out endState, valueKeywords: IfupdownValues);
    }

    // ---------------------------------------------------------------- dnsmasq

    private static readonly HashSet<string> DnsmasqDirectives = Words(StringComparer.Ordinal,
        // files, daemon and logging
        @"conf-file conf-dir conf-script
          no-daemon daemon user group pid-file chroot
          log-queries log-debug log-async log-facility log-dhcp
          no-hosts addn-hosts hostsdir no-resolv resolv-file servers-file no-poll clear-on-reload
          localise-queries local-service bogus-priv filterwin2k domain-needed
          interface except-interface listen-address bind-interfaces bind-dynamic ignore-address
          no-dhcp-interface no-dhcp6-interface",
        // DNS
        @"port edns-packet-max dns-forward-max cache-size cache-rr no-negcache no-round-robin
          dnssec dnssec-check-unsigned dnssec-no-timecheck dnssec-timestamp dnssec-debug trust-anchor dns-rr
          max-cache-ttl min-cache-ttl max-ttl local-ttl neg-ttl auth-ttl
          enable-ra ra-param
          domain local expand-hosts
          server rev-server address ipset nftset
          stop-dns-rebind rebind-localhost-ok rebind-domain-ok
          synth-domain add-mac add-cpe-id add-subnet umbrella
          connmark-allowlist-enable connmark-allowlist
          host-record cname ptr-record txt-record srv-host mx-host naptr-record alias interface-name",
        // DHCP, RA and PXE
        @"dhcp-range dhcp-host dhcp-option dhcp-option-force dhcp-option-vendor dhcp-option-class
          dhcp-boot dhcp-sequential-ip dhcp-lease-max dhcp-leasefile leasefile-ro
          dhcp-authoritative dhcp-no-override dhcp-script dhcp-scriptuser dhcp-luascript
          dhcp-match dhcp-vendorclass dhcp-userclass dhcp-mac dhcp-circuitid dhcp-remoteid dhcp-subscrid
          dhcp-proxy dhcp-relay dhcp-ignore dhcp-ignore-names dhcp-rapid-commit dhcp-alternate-port
          dhcp-client-update dhcp-fqdn dhcp-name-match dhcp-generate-names dhcp-broadcast
          dhcp-reply-delay dhcp-pxe-vendor dhcp-ttl
          quiet-dhcp quiet-dhcp6 quiet-ra
          bridge-interface shared-network tag-if",
        // TFTP and debugging
        @"enable-tftp tftp-root tftp-no-fail tftp-secure tftp-lowercase tftp-unique-root tftp-max
          tftp-port-range tftp-single-port pxe-service pxe-prompt
          dumpfile dumpmask no-ping no-ident script-arp use-stale-cache fast-dns-retry");

    private static readonly HashSet<string> DnsmasqValues = Words(StringComparer.Ordinal,
        // the flags written without a value, and the choices of the options
        @"bogus-priv filterwin2k domain-needed no-resolv no-poll no-hosts clear-on-reload localise-queries
          local-service expand-hosts bind-interfaces bind-dynamic
          dnssec dnssec-check-unsigned dnssec-no-timecheck dnssec-debug
          stop-dns-rebind rebind-localhost-ok no-negcache no-round-robin dhcp-authoritative dhcp-no-override
          dhcp-sequential-ip dhcp-rapid-commit dhcp-client-update dhcp-fqdn dhcp-generate-names dhcp-broadcast
          dhcp-proxy dhcp-ignore-names leasefile-ro enable-ra log-dhcp log-queries log-debug quiet-dhcp
          quiet-dhcp6 quiet-ra enable-tftp tftp-no-fail tftp-secure tftp-lowercase tftp-single-port script-arp
          no-ping no-ident no-daemon daemon use-stale-cache fast-dns-retry connmark-allowlist-enable
          static net host tag set vendor user circuit remote subscr bootp proxy
          ra-only ra-names ra-stateless ra-advrouter off-link slaac
          dhcp dhcp6 router dns domain local both ipv4 ipv6
          base64 text ip mac");

    /// <summary>
    /// dnsmasq: the colours of Config with the options of dnsmasq.conf and dnsmasq.d known; the options written without
    /// a value (bogus-priv, no-resolv, dhcp-authoritative) are keywords too, and so are the usual values (static, tag,
    /// ra-names, ...).
    /// </summary>
    private static bool TokenizeDnsmasq(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(DnsmasqDirectives, startState, text, sink, out endState, valueKeywords: DnsmasqValues);
    }

    // ---------------------------------------------------------------- hostapd

    private static readonly HashSet<string> HostapdDirectives = Words(StringComparer.Ordinal,
        // interface, radio and network
        @"interface driver ctrl_interface ctrl_interface_group bridge bridge_hairpin ssid ssid2 bssid
          bssid_blacklist bssid_whitelist macaddr_acl accept_mac_file deny_mac_file own_ip_addr nas_identifier
          hw_mode channel beacon_int dtim_period max_num_sta rts_threshold fragm_threshold
          country_code ieee80211d ieee80211h ieee80211n ieee80211ac ieee80211ax ieee80211be
          wmm_enabled wmm_ac_bk_cwmin wmm_ac_bk_cwmax wmm_ac_bk_aifs wmm_ac_bk_txop_limit wmm_ac_bk_acm
          wmm_ac_be_cwmin wmm_ac_be_cwmax wmm_ac_be_aifs wmm_ac_be_txop_limit wmm_ac_be_acm
          wmm_ac_vi_cwmin wmm_ac_vi_cwmax wmm_ac_vi_aifs wmm_ac_vi_txop_limit wmm_ac_vi_acm
          wmm_ac_vo_cwmin wmm_ac_vo_cwmax wmm_ac_vo_aifs wmm_ac_vo_txop_limit wmm_ac_vo_acm
          ap_isolate disassoc_low_ack skip_inactivity_poll proxy_arp",
        // WEP, WPA/WPA2/WPA3, SAE, OWE and FT
        @"auth_algs wep_key0 wep_key1 wep_key2 wep_key3 wep_default_key wep_key_len_broadcast wep_key_len_unicast
          wep_rekey_period wep_rekey_rate
          wpa wpa_key_mgmt wpa_pairwise rsn_pairwise wpa_group_rekey wpa_strict_rekey wpa_gmk_rekey wpa_ptk_rekey
          wpa_deny_ptk0_rekey wpa_ppk wpa_ppk_only wpa_psk_radius wpa_psk_file wpa_passphrase wpa_psk
          sae_password sae_pwe sae_require_mfp sae_groups sae_anti_clogging_threshold sae_sync
          owe_groups owe_transition_bssid owe_transition_ssid owe_transition_ifname owe_ptk_workaround
          ft_psk_generate_local ft_eap_generate_local ft_over_ds ft_pmk_cache r0kh r1kh pmk_r1_name
          group_cipher rsn_preauth rsn_preauth_interfaces",
        // RADIUS and EAP
        @"radius_server_addr radius_server_port radius_server_secret radius_client_addr radius_client_port
          radius_acct_server_addr radius_acct_server_port radius_acct_server_secret
          radius_acct_interim_interval radius_retry_primary_interval radius_das_port radius_das_client radius_das_secret
          acct_server_addr acct_server_port acct_server_secret auth_server_addr auth_server_port auth_server_shared_secret
          eap_server eap_user_file eap_message eap_sim_db eap_reauth_period eapol_key_index_workaround
          ca_cert server_cert private_key private_key_passwd dh_file cert_type",
        // WPS
        @"wps_state ap_setup_locked config_methods uuid device_name manufacturer model_name model_number serial_number
          device_type os_version wps_pin wps_pbc wps_cred_processing wps_rf_bands wps_pri_dev_type wps_sec_dev_type_list
          wps_ap_pin wps_cred_add_sae wps_skip_ads",
        // Hotspot 2.0, MBO, logging, vendor elements and P2P
        @"interworking internet access_network_type network_auth_type venue_group venue_type hessid domain_name
          roaming_consortium venue_name venue_url mbo mbo_cell_capa mbo_oce
          logger_syslog logger_syslog_level logger_stdout logger_stdout_level dump_file
          vendor_elements assocresp_elements multi_ap
          p2p_device_addr p2p_interface_addr p2p_listen_reg_class p2p_listen_channel p2p_oper_reg_class p2p_oper_channel");

    private static readonly HashSet<string> HostapdValues = Words(StringComparer.Ordinal,
        // hw_mode, WPA key management and pairwise ciphers
        @"a b g ad
          WPA-PSK WPA-EAP WPA-PSK-SHA256 WPA-EAP-SHA256 SAE FT-PSK FT-EAP FT-EAP-SHA256 FT-SAE
          FILS-SHA256 FILS-SHA384 SAE-EXT-KEY OWE DPP
          CCMP TKIP GCMP CCMP-256 GCMP-256
          PSK EAP 802.1X
          label display push_button keypad virtual_display physical_display virtual_push_button physical_push_button
          none radius mac open shared");

    /// <summary>
    /// hostapd: the colours of Config with the options of hostapd.conf known; the hw_mode letters, the WPA key
    /// management suites and the pairwise ciphers are keywords too.
    /// </summary>
    private static bool TokenizeHostapd(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(HostapdDirectives, startState, text, sink, out endState, valueKeywords: HostapdValues);
    }

    // -------------------------------------------------------------- WireGuard

    private static readonly HashSet<string> WireguardDirectives = Words(StringComparer.OrdinalIgnoreCase,
        // [Interface] and [Peer]
        @"Address ListenPort PrivateKey DNS MTU FwMark Table
          PreUp PostUp PreDown PostDown SaveConfig
          PublicKey PresharedKey AllowedIPs Endpoint PersistentKeepalive",
        // AmneziaWG, a WireGuard fork with obfuscation parameters
        @"Jc Jmin Jmax S1 S2 H1 H2 H3 H4 I1 I2 I3 I4 I5");

    private static readonly HashSet<string> WireguardValues = Words(StringComparer.OrdinalIgnoreCase,
        @"Interface Peer auto main default off");

    /// <summary>
    /// WireGuard: the colours of Config with the [Interface] and [Peer] keys known; the values of Table (auto, main,
    /// default, off) are keywords too, and the keys match in any case, as wg-quick reads them.
    /// </summary>
    private static bool TokenizeWireguard(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(WireguardDirectives, startState, text, sink, out endState, valueKeywords: WireguardValues);
    }

    // ------------------------------------------------------------------- udev

    private static readonly HashSet<string> UdevKeywords = Words(StringComparer.Ordinal,
        // the keys of the rules
        @"ACTION SUBSYSTEM SUBSYSTEMS KERNEL KERNELS DEVPATH DEVPATH_OLD DRIVER DRIVERS
          ATTR ATTRS SYSFS ENV TAG TAGS NAME SYMLINK OWNER GROUP MODE
          PROGRAM RESULT IMPORT WAIT_FOR WAIT_FOR_SYSFS OPTIONS TEST LABEL GOTO RUN SECLABEL CONST PRODUCT NETLINK
          udev_log",
        // the usual unquoted values of ACTION, OPTIONS and udev.conf
        @"add remove change move online offline bind unbind
          db_persist nowatch watch last_rule string_escape none replace err info debug");

    /// <summary>
    /// udev: the colours of Config with the keys of the rules known wherever they stand, since a rule is a list of
    /// "KEY==value" pairs separated by commas; the usual unquoted values are keywords too.
    /// </summary>
    private static bool TokenizeUdev(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(UdevKeywords, startState, text, sink, out endState, valueKeywords: UdevKeywords);
    }

    // --------------------------------------------------------------- nftables

    private static readonly HashSet<string> NftablesKeywords = Words(StringComparer.Ordinal,
        // commands and the objects they act on
        @"flush table chain rule set map element define include add create delete list monitor describe export import get reset
          rename replace destroy",
        // statements, actions, protocols and match keys
        @"accept drop reject queue continue return jump goto counter log limit quota snat dnat masquerade redirect tproxy
          dup fwd notrack meta ct ip ip6 tcp udp sctp dccp icmp icmpv6 arp ether vlan bridge inet netdev route socket
          fib osf xdp numgen jhash symhash mark tos dscp ecn ttl hoplimit flowlabel length frag hdrlength checksum seq
          ack window flags option version type code mtu protocol priority policy devices hook device state status
          saddr daddr sport dport iif oif iifname oifname iiftype oiftype cgroup cpu pkttype skuid skgid nfproto l4proto
          ipsec rt bh count packets bytes over until timeout size auto-merge dynamic interval elem gc-interval expires
          evaluation vmap prefix nexthop via dev weight persistent secret name value host ifname id num label zone
          domain interface dei pcp cfi sequence gateway classid nexthdr hlimit frag-off mh-type mh hbh rt0 rt1 rt2 rt4 sr
          dstopts ah esp comp level",
        // the usual values: families, hooks, chain types, set types and log levels
        @"ruleset handle position comment
          new established related invalid untracked
          input output forward prerouting postrouting ingress
          filter nat mangle raw security
          src dst srcnat dstnat ipv4_addr ipv6_addr ether_addr inet_service
          emerg alert crit err warning notice info debug");

    /// <summary>
    /// nftables: the colours of Config with the commands, statements and match keys of nft known wherever they stand;
    /// strings, variables and the addresses keep the colours of Config. Comments are '#'.
    /// </summary>
    private static bool TokenizeNftables(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(NftablesKeywords, startState, text, sink, out endState, valueKeywords: NftablesKeywords);
    }

    // --------------------------------------------------------------- modprobe

    private static readonly HashSet<string> ModprobeDirectives = Words(StringComparer.Ordinal,
        @"alias blacklist options install remove softdep weakdep include");

    private static readonly HashSet<string> ModprobeValues = Words(StringComparer.Ordinal,
        // the stages of a soft dependency and the value that disables an alias
        @"pre post off");

    /// <summary>
    /// modprobe.d: the colours of Config with the directives of the module loader known; the pre/post stages of softdep
    /// and the off of an alias are coloured as keywords.
    /// </summary>
    private static bool TokenizeModprobe(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(ModprobeDirectives, startState, text, sink, out endState, valueKeywords: ModprobeValues);
    }

    // -------------------------------------------------------------- logrotate

    private static readonly HashSet<string> LogrotateDirectives = Words(StringComparer.Ordinal,
        // the file itself and the included drop-ins
        @"include tabooext taboopat
          hourly daily weekly monthly yearly
          rotate size maxsize minsize maxage minage start
          create nocreate createolddir nocreateolddir copy nocopy renamecopy copytruncate
          compress nocompress delaycompress nodelaycompress compresscmd uncompresscmd compressext compressoptions
          notifempty ifempty missingok nomissingok olddir noolddir
          dateext nodateext dateformat dateyesterday datehourago
          sharedscripts nosharedscripts prerotate postrotate firstaction lastaction endscript
          mail nomail mailfirst maillast su extension addextension errors
          shred noshred shredcycles allowhardlink noallowhardlink merge nomerge");

    private static readonly HashSet<string> LogrotateValues = Words(StringComparer.Ordinal,
        // the directives that are written without a value, and the values of the script stages
        @"hourly daily weekly monthly yearly
          compress nocompress delaycompress nodelaycompress
          copy copytruncate nocopy nocreate nocreateolddir noshred noolddir
          notifempty ifempty missingok nomissingok
          sharedscripts nosharedscripts prerotate postrotate firstaction lastaction endscript
          dateext nodateext dateyesterday datehourago
          nomail mailfirst maillast allowhardlink noallowhardlink merge nomerge");

    /// <summary>
    /// logrotate: the colours and the folds of Config with the directives of /etc/logrotate.conf and logrotate.d known.
    /// The braces that enclose the rules of one log file are foldable; the directives written without a value (weekly,
    /// compress, sharedscripts) are coloured wherever they stand; comments are '#'.
    /// </summary>
    private static bool TokenizeLogrotate(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(LogrotateDirectives, startState, text, sink, out endState,
            foldBraces: true, valueKeywords: LogrotateValues);
    }

    // ----------------------------------------------------------------- sysctl

    private static readonly HashSet<string> SysctlDirectives = Words(StringComparer.Ordinal,
        // kernel
        @"kernel.sysrq kernel.panic kernel.panic_on_oops kernel.panic_on_warn kernel.core_pattern kernel.core_uses_pid
          kernel.dmesg_restrict kernel.kptr_restrict kernel.perf_event_paranoid kernel.unprivileged_bpf_disabled
          kernel.unprivileged_userns_clone kernel.yama.ptrace_scope kernel.pid_max kernel.printk kernel.printk_ratelimit
          kernel.shmmax kernel.shmall kernel.shmmni kernel.msgmax kernel.msgmnb kernel.msgmni kernel.sem
          kernel.randomize_va_space kernel.modules_disabled kernel.kexec_load_disabled kernel.io_uring_disabled
          kernel.softlockup_panic kernel.hung_task_timeout_secs kernel.nmi_watchdog kernel.watchdog kernel.watchdog_thresh
          kernel.sched_rt_runtime_us kernel.sched_autogroup_enabled kernel.numa_balancing kernel.random.boot_id
          kernel.core_pipe_limit kernel.threads-max kernel.cad_pid",
        // virtual memory
        @"vm.swappiness vm.dirty_ratio vm.dirty_background_ratio vm.dirty_expire_centisecs vm.dirty_writeback_centisecs
          vm.vfs_cache_pressure vm.min_free_kbytes vm.max_map_count vm.overcommit_memory vm.overcommit_ratio
          vm.panic_on_oom vm.oom_kill_allocating_task vm.nr_hugepages vm.nr_overcommit_hugepages vm.hugetlb_shm_group
          vm.stat_interval vm.laptop_mode vm.zone_reclaim_mode vm.mmap_min_addr vm.mmap_rnd_bits vm.mmap_rnd_compat_bits
          vm.unprivileged_userfaultfd vm.compaction_proactiveness vm.watermark_boost_factor vm.watermark_scale_factor",
        // file systems and pipes
        @"fs.file-max fs.nr_open fs.inotify.max_user_watches fs.inotify.max_user_instances fs.inotify.max_queued_events
          fs.protected_hardlinks fs.protected_symlinks fs.protected_fifos fs.protected_regular fs.suid_dumpable
          fs.pipe-max-size fs.aio-max-nr fs.epoll.max_user_watches",
        // networking: queues, buffers and congestion
        @"net.core.somaxconn net.core.netdev_max_backlog net.core.rmem_default net.core.rmem_max net.core.wmem_default
          net.core.wmem_max net.core.optmem_max net.core.message_cost net.core.message_burst net.core.hot_list_length
          net.core.dev_weight net.core.bpf_jit_enable net.core.bpf_jit_harden net.core.default_qdisc net.core.busy_poll
          net.core.busy_read",
        // IPv4
        @"net.ipv4.ip_forward net.ipv4.conf.all.forwarding net.ipv4.conf.default.forwarding
          net.ipv4.conf.all.rp_filter net.ipv4.conf.default.rp_filter
          net.ipv4.conf.all.accept_redirects net.ipv4.conf.default.accept_redirects
          net.ipv4.conf.all.send_redirects net.ipv4.conf.default.send_redirects
          net.ipv4.conf.all.accept_source_route net.ipv4.conf.default.accept_source_route
          net.ipv4.conf.all.log_martians net.ipv4.conf.default.log_martians
          net.ipv4.conf.all.arp_ignore net.ipv4.conf.all.arp_announce net.ipv4.conf.all.arp_filter
          net.ipv4.icmp_echo_ignore_all net.ipv4.icmp_echo_ignore_broadcasts net.ipv4.icmp_ignore_bogus_error_responses
          net.ipv4.icmp_ratelimit net.ipv4.icmp_ratemask
          net.ipv4.tcp_syncookies net.ipv4.tcp_max_syn_backlog net.ipv4.tcp_synack_retries net.ipv4.tcp_syn_retries
          net.ipv4.tcp_retries1 net.ipv4.tcp_retries2 net.ipv4.tcp_tw_reuse net.ipv4.tcp_fin_timeout
          net.ipv4.tcp_keepalive_time net.ipv4.tcp_keepalive_intvl net.ipv4.tcp_keepalive_probes
          net.ipv4.tcp_rmem net.ipv4.tcp_wmem net.ipv4.tcp_mtu_probing net.ipv4.tcp_congestion_control
          net.ipv4.tcp_fastopen net.ipv4.tcp_slow_start_after_idle net.ipv4.tcp_notsent_lowat
          net.ipv4.tcp_max_tw_buckets net.ipv4.ip_local_port_range net.ipv4.ip_local_reserved_ports
          net.ipv4.ip_nonlocal_bind net.ipv4.ip_unprivileged_port_start net.ipv4.ping_group_range
          net.ipv4.neigh.default.gc_stale_time net.ipv4.neigh.default.gc_thresh1 net.ipv4.neigh.default.gc_thresh2
          net.ipv4.neigh.default.gc_thresh3",
        // IPv6, netfilter and bridges
        @"net.ipv6.conf.all.forwarding net.ipv6.conf.default.forwarding net.ipv6.conf.all.accept_ra
          net.ipv6.conf.default.accept_ra net.ipv6.conf.all.disable_ipv6 net.ipv6.conf.default.disable_ipv6
          net.ipv6.conf.all.use_tempaddr net.ipv6.conf.default.use_tempaddr net.ipv6.bindv6only
          net.netfilter.nf_conntrack_max net.netfilter.nf_conntrack_tcp_timeout_established net.netfilter.nf_conntrack_buckets
          net.bridge.bridge-nf-call-iptables net.bridge.bridge-nf-call-ip6tables net.bridge.bridge-nf-call-arptables");

    private static readonly HashSet<string> SysctlValues = Words(StringComparer.Ordinal,
        // the usual string values of the tunables
        @"default all bbr cubic fq_codel fq pfifo_fast noqueue reno vegas westwood yeah illinois htcp
          never always");

    /// <summary>
    /// sysctl: the colours of Config with the kernel tunables of sysctl.conf and sysctl.d known; a tunable written
    /// without a value (or with one) is a keyword, and a typo stays an ordinary key. Comments are '#'.
    /// </summary>
    private static bool TokenizeSysctl(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(SysctlDirectives, startState, text, sink, out endState, valueKeywords: SysctlValues);
    }

    // -------------------------------------------------------------------- PAM

    private static readonly HashSet<string> PamDirectives = Words(StringComparer.Ordinal,
        // the management groups of a PAM rule
        @"
          auth account password session");

    private static readonly HashSet<string> PamValues = Words(StringComparer.Ordinal,
        // the management groups, the control flags and the key of a bracketed control
        @"auth account password session
          required requisite sufficient optional include substack die ok done bad ignore reset NONE
          success default ignore bad done die reset",
        // common arguments of the modules
        @"nullok nullok_secure try_first_pass use_first_pass use_authtok not_set_pass nodelay remember
          broken_shadow no_pass_expiry sha512 yescrypt md5 bigcrypt rounds minlen difok ucredit lcredit dcredit
          ocredit enforce_for_root retry authtok_type obscure debug quiet audit silent standard relaxed
          login tty user ruser rhost host service type",
        // the modules of the usual stacks
        @"pam_unix.so pam_env.so pam_systemd.so pam_pwquality.so pam_cracklib.so pam_limits.so pam_loginuid.so
          pam_securetty.so pam_nologin.so pam_motd.so pam_mail.so pam_lastlog.so pam_faillock.so pam_tally2.so
          pam_deny.so pam_permit.so pam_rootok.so pam_wheel.so pam_succeed_if.so pam_listfile.so pam_access.so
          pam_time.so pam_echo.so pam_exec.so pam_script.so pam_selinux.so pam_apparmor.so pam_tty_audit.so
          pam_umask.so pam_keyinit.so pam_namespace.so pam_pwhistory.so pam_krb5.so pam_ldap.so pam_sss.so
          pam_winbind.so pam_google_authenticator.so pam_u2f.so pam_oath.so pam_fprintd.so pam_gnome_keyring.so
          pam_kwallet.so pam_kwallet5.so pam_ecryptfs.so pam_mount.so pam_ccreds.so pam_localuser.so pam_shells.so
          pam_issue.so pam_debug.so pam_faildelay.so pam_filter.so pam_ftp.so pam_group.so pam_mkhomedir.so
          pam_pkcs11.so pam_rhosts.so pam_stress.so pam_timestamp.so pam_userdb.so pam_warn.so pam_xauth.so");

    /// <summary>
    /// PAM: the colours of Config with the management groups, control flags, module arguments and common modules
    /// known; the words of a bracketed control ([success=1 default=ignore]) are coloured too. Comments are '#'.
    /// </summary>
    private static bool TokenizePam(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(PamDirectives, startState, text, sink, out endState, valueKeywords: PamValues);
    }

    // ------------------------------------------------------------------ Samba

    private static readonly HashSet<string> SambaDirectives = Phrases(StringComparer.OrdinalIgnoreCase,
        // global: identity, security and name resolution
        @"workgroup
          netbios name
          server string
          server role
          security
          map to guest
          guest account
          log level
          max log size
          passdb backend
          obey pam restrictions
          pam password change
          unix password sync
          ldap ssl
          ldap suffix
          ldap admin dn
          ldap user suffix
          ldap group suffix
          ldap machine suffix
          idmap config
          idmap uid
          idmap gid
          winbind use default domain
          winbind enum users
          winbind enum groups
          winbind separator
          winbind offline logon
          winbind refresh tickets
          template shell
          template homedir
          realm
          password server
          dns proxy
          name resolve order
          wins support
          wins server
          domain master
          local master
          preferred master
          domain logons
          logon path
          logon drive
          logon script
          os level
          announce as
          announce version
          hosts allow
          hosts deny
          interfaces
          bind interfaces only
          smb ports
          socket options
          deadtime
          keepalive
          max smbd processes
          min receivefile size
          use sendfile
          aio read size
          aio write size
          read raw
          write raw
          strict sync
          sync always
          large readwrite
          server min protocol
          server max protocol
          client min protocol
          client max protocol
          server signing
          client signing
          smb encrypt
          ntlm auth
          client ntlmv2 auth
          lanman auth
          raw NTLMv2 auth
          restrict anonymous
          map archive
          map hidden
          map system
          map readonly
          store dos attributes
          ea support
          unix extensions
          wide links
          follow symlinks
          allow insecure wide links
          load printers
          printing
          printcap name
          disable spoolss
          cups options
          cups server
          delete readonly
          dos filemode
          dos filetimes
          dos filetime resolution
          fake directory create times
          change notify
          kernel change notify
          change notify timeout
          getwd cache
          host msdfs
          msdfs root
          msdfs proxy
          usershare allow guests
          usershare max shares
          usershare path
          usershare template share
          include
          config backend
          registry shares
          preload
          panic action
          lock directory
          state directory
          cache directory
          pid directory
          private dir
          ncalrpc dir
          winbindd socket directory
          server services
          rpc server dynamic port range
          rpc server port
          ctdbd socket
          clustering
          kernel oplocks
          oplocks
          level2 oplocks
          posix locking
          strict locking
          locking
          blocking locks
          share modes
          smbd profiling level
          comment
          path
          browseable
          read only
          writable
          writeable
          write ok
          guest ok
          public
          guest only
          only guest
          valid users
          invalid users
          admin users
          read list
          write list
          force user
          force group
          group
          create mask
          force create mode
          directory mask
          force directory mode
          inherit permissions
          inherit acls
          inherit owner
          inherit smbcacls
          acl allow execute always
          acl group control
          acl map full control
          acl check execute
          nt acl support
          printable
          print ok
          printer name
          printer
          print command
          lpq command
          lprm command
          lppause command
          lpresume command
          queuepause command
          queueresume command
          hide dot files
          hide files
          hide unreadable
          hide unwriteable files
          veto files
          delete veto files
          veto oplock files
          mangled names
          mangling method
          case sensitive
          default case
          preserve case
          short preserve case
          csc policy
          volume
          available
          dfree command
          dfree cache time
          map acl inherit
          fake oplocks
          directory name cache size
          vfs objects");

    private static readonly HashSet<string> SambaValues = Words(StringComparer.OrdinalIgnoreCase,
        // roles, backends, protocol levels and the usual choices of the parameters
        @"user share domain ads standalone member classic
          tdbsam smbpasswd ldapsam rid autorid ad nss rfc2307 sfu sfu20 hash bad plaintext
          NT1 SMB2 SMB3 SMB2_02 SMB2_10 SMB2_22 SMB2_24 SMB3_00 SMB3_02 SMB3_10 SMB3_11
          mandatory desired auto off none full disk printer printers cups bsd sysv lprng
          recycle full_audit acl_xattr streams_xattr fruit catia time_audit extd_audit shadow_copy2 crossrename");

    /// <summary>
    /// Samba: the colours of Config with the [global] and [share] parameters known; the names match in any case, as
    /// Samba reads them. The usual values (user, tdbsam, SMB3, recycle, ...) are keywords too. Comments are '#'.
    /// </summary>
    private static bool TokenizeSamba(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(SambaDirectives, startState, text, sink, out endState,
            valueKeywords: SambaValues, phraseDirectives: SambaDirectives, valueKeywordsInValue: true);
    }

    // ---------------------------------------------------------------- Postfix

    private static readonly HashSet<string> PostfixDirectives = Words(StringComparer.Ordinal,
        // main.cf: identity, interfaces and routing
        @"myhostname mydomain myorigin mydestination mynetworks mynetworks_style inet_interfaces inet_protocols
          relayhost relay_domains relay_recipient_maps relay_transport transport_maps
          smtpd_banner mail_name readme_directory compatibility_level
          biff append_dot_mydomain append_at_myorigin mail_spool_directory home_mailbox
          message_size_limit mailbox_size_limit recipient_delimiter duplicate_filter_limit
          unknown_local_recipient_reject_code strict_rfc821_envelopes disable_vrfy_command
          luser_relay masquerade_domains masquerade_exceptions owner_request_special
          soft_bounce import_environment export_environment syslog_facility syslog_name
          debug_peer_level debug_peer_list debugger_command",
        // main.cf: maps and aliases
        @"alias_maps alias_database local_recipient_maps local_transport canonical_maps
          sender_canonical_maps recipient_canonical_maps relocated_maps
          virtual_alias_maps virtual_alias_domains virtual_mailbox_domains virtual_mailbox_maps
          virtual_mailbox_base virtual_uid_maps virtual_gid_maps virtual_transport virtual_minimum_uid
          header_checks body_checks mime_header_checks nested_header_checks smtp_header_checks
          smtp_mime_header_checks smtp_body_checks content_filter receive_override_options
          transport_destination_concurrency_limit relay_destination_concurrency_limit
          local_destination_concurrency_limit smtp_destination_concurrency_limit
          smtps_destination_concurrency_limit lmtp_destination_concurrency_limit
          virtual_destination_concurrency_limit error_destination_concurrency_limit
          default_destination_concurrency_limit default_destination_recipient_limit
          default_destination_rate_delay default_process_limit
          qmgr_message_active_limit qmgr_message_recipient_limit qmgr_message_recipient_minimum
          maximal_queue_lifetime bounce_queue_lifetime queue_run_delay minimal_backoff_time maximal_backoff_time
          notify_classes delay_warning_time confirm_delay_cleared bounce_notice_recipient
          error_notice_recipient delay_notice_recipient postmaster_address
          mail_owner default_privs setgid_group",
        // main.cf: TLS, SASL and the SMTP server
        @"smtpd_tls_cert_file smtpd_tls_key_file smtpd_tls_CAfile smtpd_tls_CApath
          smtpd_tls_dcert_file smtpd_tls_dkey_file smtpd_tls_dh1024_param_file smtpd_tls_dh512_param_file
          smtpd_tls_eccert_file smtpd_tls_eckey_file smtpd_tls_security_level smtpd_tls_auth_only
          smtpd_tls_loglevel smtpd_tls_received_header smtpd_tls_session_cache_database
          smtpd_use_tls smtp_tls_security_level smtp_tls_CAfile smtp_tls_CApath smtp_tls_cert_file
          smtp_tls_key_file smtp_tls_dcert_file smtp_tls_dkey_file smtp_tls_eccert_file smtp_tls_eckey_file
          smtp_tls_loglevel smtp_tls_session_cache_database smtp_tls_wrappermode smtp_use_tls
          smtp_sasl_auth_enable smtp_sasl_password_maps smtp_sasl_security_options
          smtp_sasl_tls_security_options smtp_sasl_mechanism_filter
          smtpd_sasl_auth_enable smtpd_sasl_type smtpd_sasl_path smtpd_sasl_security_options
          smtpd_sasl_local_domain smtpd_sasl_tls_security_options smtpd_sasl_mechanism_filter
          smtpd_sasl_exceptions_networks smtpd_sasl_authenticated_header
          smtpd_relay_restrictions smtpd_recipient_restrictions smtpd_sender_restrictions
          smtpd_client_restrictions smtpd_helo_restrictions smtpd_etrn_restrictions
          smtpd_delay_reject smtpd_error_sleep_time smtpd_soft_error_limit smtpd_hard_error_limit
          smtpd_junk_command_limit smtpd_client_connection_count_limit smtpd_client_connection_rate_limit
          smtpd_client_message_rate_limit smtpd_client_recipient_rate_limit
          smtpd_client_new_tls_session_rate_limit smtpd_client_auth_rate_limit anvil_rate_time_unit
          smtpd_command_filter smtpd_forbidden_commands smtpd_proxy_filter smtpd_proxy_timeout
          smtpd_proxy_options smtpd_peername_lookup qmqpd_authorized_clients qmqpd_error_delay qmqpd_timeout",
        // main.cf: directories of the installation
        @"config_directory daemon_directory command_directory queue_directory data_directory
          meta_directory shlib_directory mailq_path sendmail_path newaliases_path
          alternate_config_directories multi_instance_name multi_instance_group
          multi_instance_directories multi_instance_wrapper process_id_directory
          default_dbtype berkeley_db_create_buffer_size berkeley_db_read_buffer_size smtputf8_enable
          allow_percent_hack allow_mail_to_commands allow_mail_to_files propagate_unmatched_extensions
          trace_service_name",
        // master.cf: the services of the Postfix system
        @"smtp smtps submission submissions pickup cleanup qmgr trivial-rewrite bounce defer trace verify
          flush proxymap proxywrite showq error retry discard local virtual lmtp anvil scache proxy relay");

    private static readonly HashSet<string> PostfixValues = Words(StringComparer.Ordinal,
        // protocol families, service types, map types and service commands
        @"all ipv4 ipv6 inet unix fifo hash lmdb btree dbm cdb static proxy
          smtpd qmgr pickup cleanup trivial-rewrite bounce defer trace verify flush showq error retry discard
          local virtual lmtp anvil scache relay
          y n",
        // TLS levels and the restriction expressions of smtpd
        @"may encrypt none dane fingerprint secure plaintext anonymous
          permit defer reject discard hold warning
          permit_mynetworks permit_sasl_authenticated permit_tls_clientcerts permit_tls_all_clientcerts
          reject_unauth_destination reject_unauth_pipelining reject_invalid_helo_hostname
          reject_non_fqdn_helo_hostname reject_non_fqdn_sender reject_non_fqdn_recipient
          reject_unknown_sender_domain reject_unknown_recipient_domain reject_unknown_helo_hostname
          reject_unknown_reverse_client_hostname reject_unknown_client_hostname
          reject_rbl_client reject_rhsbl_client reject_rhsbl_sender reject_rhsbl_helo reject_rhsbl_reverse_client
          check_client_access check_sender_access check_recipient_access check_helo_access
          check_policy_service check_sasl_access defer_if_permit defer_if_reject");

    /// <summary>
    /// Postfix: the colours of Config with the parameters of main.cf and the services of master.cf known; the service
    /// types (inet, unix), map types (hash, lmdb), TLS levels (may, encrypt) and restriction expressions are keywords
    /// too, so the columns of master.cf are readable. Comments are '#'.
    /// </summary>
    private static bool TokenizePostfix(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(PostfixDirectives, startState, text, sink, out endState, valueKeywords: PostfixValues);
    }

    // ---------------------------------------------------------------- OpenVPN

    private static readonly HashSet<string> OpenVpnDirectives = Words(StringComparer.OrdinalIgnoreCase,
        // the mode, the transport and the device
        @"
          client server mode proto dev dev-type topology port local remote remote-random remote-random-hostname
          nobind resolv-retry float multihome",
        // the keys and the TLS handshake
        @"
          ca cert key dh ecdh-curve pkcs12 secret tls-auth tls-crypt tls-crypt-v2 tls-server tls-client
          tls-version-min tls-version-max tls-group tls-cipher tls-ciphersuites tls-cert-profile tls-export-cert
          key-direction auth cipher data-ciphers data-ciphers-fallback ncp-ciphers auth-nocache auth-user-pass
          auth-retry auth-gen-token auth-token auth-token-user username-as-common-name verify-client-cert
          verify-x509-name verify-hash remote-cert-tls remote-cert-eku remote-cert-ku ns-cert-type crl-verify
          x509-username-field pkcs11-providers pkcs11-private-mode pkcs11-cert-private pkcs11-id pkcs11-id-management
          askpass engine providers provider keystore",
        // the scripts and the daemon
        @"
          plugin script-security up down up-restart up-delay down-pre ipchange route-up route-pre-down learn-address
          client-connect client-disconnect client-config-dir ccd-exclusive connection config cd chroot user group
          persist-key persist-tun persist-local-ip persist-remote-ip daemon syslog log log-append verb mute status
          status-version writepid tmp-dir mlock nice",
        // the timers, the link and the buffers
        @"
          keepalive ping ping-restart ping-exit ping-timer-rem inactive connect-retry connect-retry-max connect-timeout
          hand-window reneg-sec reneg-bytes reneg-pkts mute-replay-warnings replay-window tcp-nodelay tcp-queue-limit
          txqueuelen mtu mtu-disc tun-mtu tun-mtu-extra fragment mssfix sndbuf rcvbuf fast-io",
        // the addresses and the routes
        @"
          route route-ipv6 route-gateway route-metric route-delay route-method route-noexec route-nopull
          redirect-gateway redirect-private pull pull-filter push push-reset push-remove push-peer-info
          ifconfig ifconfig-ipv6 ifconfig-noexec ifconfig-nowarn server-bridge server-ipv6
          ifconfig-pool ifconfig-pool-persist ifconfig-pool-linear ifconfig-ipv6-pool
          client-to-client duplicate-cn max-clients max-routes-per-client",
        // compression, the proxies and the management interface
        @"
          comp-lzo compress allow-compression http-proxy http-proxy-option http-proxy-retry http-proxy-timeout
          http-proxy-user-pass socks-proxy
          management management-client management-query-passwords management-hold management-signal
          management-log-cache management-forget-disconnect management-up-down management-client-auth
          management-client-pf management-client-user management-client-group management-external-key
          management-external-cert management-client-connect",
        // names, options and the Windows client
        @"
          setenv setenv-safe setenv-opt opt-verify ignore-unknown-option compat-mode show-ciphers show-digests
          show-tls show-engines show-gateway show-net-up test-crypto disable-occ
          block-outside-dns block-ipv6 register-dns windows-driver cryptoapicert dhcp-renew dhcp-release
          ip-win32 tap-sleep net30 exit-event win-sys
          dhcp-option dhcp-option-dom dhcp-option-dns dns dns-search dns-domain dns-server dns-fallback
          explicit-exit-notify");

    private static readonly HashSet<string> OpenVpnValues = Words(StringComparer.OrdinalIgnoreCase,
        // the directives written without a value
        @"
          client nobind persist-key persist-tun persist-local-ip persist-remote-ip pull tls-client tls-server
          remote-random remote-random-hostname auth-nocache duplicate-cn float multihome up-restart down-pre mlock
          mute-replay-warnings client-to-client opt-verify disable-occ block-outside-dns block-ipv6 register-dns
          show-net-up dhcp-renew dhcp-release tap-sleep push-peer-info ccd-exclusive ifconfig-noexec ifconfig-nowarn
          route-noexec route-nopull management-hold management-query-passwords management-signal
          management-forget-disconnect management-up-down management-client auth-gen-token explicit-exit-notify",
        // the transports, the devices and the modes
        @"udp udp4 udp6 tcp tcp4 tcp6 tcp-server tcp-client tun tap null server client p2p subnet net30",
        // the words of the usual options
        @"yes no none default infinite adaptive asym require optional ignore accept nointeract interact stub v2
          or-highest tap-windows6 wintun",
        // the ciphers and the digests
        @"AES-256-GCM AES-192-GCM AES-128-GCM CHACHA20-POLY1305 AES-256-CBC AES-192-CBC AES-128-CBC
          BF-CBC DES-EDE3-CBC CAMELLIA-256-CBC CAMELLIA-192-CBC CAMELLIA-128-CBC SEED-CBC
          SHA1 SHA224 SHA256 SHA384 SHA512 SHA3-256 SHA3-512 MD5 BLAKE2b512 BLAKE2s256 whirlpool ripemd160");

    /// <summary>
    /// OpenVPN: the colours of Config with the options of a client or server profile known; the protocols, devices,
    /// modes, ciphers and digests are keywords too. The directives written without a value (client, nobind,
    /// persist-key) are kept in the value set, so they colour as well. Comments are '#' and ';'.
    /// </summary>
    private static bool TokenizeOpenVpn(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(OpenVpnDirectives, startState, text, sink, out endState, valueKeywords: OpenVpnValues);
    }

    // ---------------------------------------------------------------- Netplan

    private static readonly HashSet<string> NetplanKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope and the renderer
        @"
          network version renderer",
        // the kinds of the devices
        @"
          ethernets wifis bridges bonds vlans vrfs tunnels nm-devices dummy-devices modems",
        // the settings of every device
        @"
          dhcp4 dhcp6 dhcp4-overrides dhcp6-overrides addresses gateway4 gateway6 routes routing-policy
          nameservers search macaddress mtu optional set-name wakeonlan match accept-ra ipv6-address-generation
          ipv6-address-token ipv6-privacy",
        // the DHCP overrides
        @"
          use-dns use-ntp send-hostname use-hostname hostname use-routes route-metric use-domains",
        // the routes and the routing policy
        @"
          to via from on-link scope metric table type priority mark",
        // the bonds and the bridges
        @"
          interfaces parameters mode lacp-rate mii-monitor-interval up-delay down-delay transmit-hash-policy
          ad-select primary resend-igmp all-slaves-active arp-interval arp-ip-targets arp-validate learn-packet-interval
          stp forward-delay hello-time max-age ageing-time port-priority path-cost mac-learning",
        // the VLANs, the VRFs and the tunnels
        @"
          id link table local remote key input-key output-key",
        // the Wi-Fi access points and their authentication
        @"
          access-points auth password key-management ssid band channel bssid hidden network-name regulatory-domain
          method identity anonymous-identity phase2-auth ca-certificate client-certificate client-key
          private-key private-key-password eap",
        // the modems of NetworkManager
        @"
          apn auto-config device-id network-id number pin sim-id sim-operator-id username");

    private static readonly HashSet<string> NetplanValues = Words(StringComparer.OrdinalIgnoreCase,
        // the renderers
        @"networkd NetworkManager openvswitch",
        // the choices of the addresses, the bonds, the bridges and the access points
        @"static dhcp none default link-local eui64 stable-privacy
          active-backup balance-rr balance-xor broadcast 802.3ad balance-tlb balance-alb
          psk eap wpa-psk wpa-eap infrastructure adhoc ap");

    /// <summary>
    /// Netplan: the colours and the folds of YAML with the keys of the network configuration known; the renderers
    /// (networkd, NetworkManager, openvswitch), the address modes and the bond and Wi-Fi choices are values.
    /// </summary>
    private static bool TokenizeNetplan(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(NetplanKeywords, NetplanValues, startState, text, sink, out endState);
    }

    // -------------------------------------------------------------- cloud-init

    private static readonly HashSet<string> CloudInitKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope of a user-data file
        @"
          version hostname fqdn manage_etc_hosts preserve_hostname timezone locale locale_configfile keyboard",
        // the users and their keys
        @"
          users groups ssh_authorized_keys ssh_keys ssh_pwauth ssh_import_id ssh_redirect_user disable_root
          chpasswd password expire list lock_passwd sudo shell gecos homedir primary_group no_create_home system
          inactive selinux_user snapuser doas",
        // the packages and the commands
        @"
          package_update package_upgrade package_reboot_if_required packages apt yum_repos dnf zypper apk_repos
          snap snappy puppet chef salt_minion ansible runcmd bootcmd scripts_per_boot scripts_per_instance
          scripts_per_once scripts_user script",
        // the files, the mounts and the disks
        @"
          write_files path content owner permissions encoding append defer mkdir delete
          mounts device fstype options dump passno swap filename maxsize ephemeral create migrate
          disk_setup layout overwrite table_type partition size type fs label replace_fs extra_opts",
        // the datasources and the network of a seed
        @"
          datasource datasource_list datasource_pkg_list vendor_data user_data metadata network config search dns
          nameserver match mac name address netmask gateway routes id subnets broadcast dns_nameservers dns_search
          mtu wakeonlan dhcp4 dhcp6 addresses renderer",
        // the modules of /etc/cloud/cloud.cfg
        @"
          cloud_config_modules cloud_init_modules cloud_final_modules system_info default_user distro paths
          package_mirrors apt_mirror security ntpservers",
        // the power state, the phone home and the random seed
        @"
          power_state mode message timeout condition delay phone_home url post tries retries
          random_seed file data command",
        // the certificates, the merges and the system adjustments
        @"
          ca_certs remove_defaults trusted rsyslog configs remotes remote filter merge_how merge_type
          byobu byobu_by_default growpart devices ignore_growroot_disabled resize_rootfs update_etc_hosts hosts
          ubuntu_drivers install_drivers drivers");

    private static readonly HashSet<string> CloudInitValues = Words(StringComparer.OrdinalIgnoreCase,
        // the power states, the schedules and the file systems
        @"reboot poweroff halt shutdown
          always once per-boot per-instance per-once
          ext4 xfs btrfs vfat ntfs swap zfs
          auto none dhcp static
          cloud-init cloud-config");

    /// <summary>
    /// cloud-init: the colours and the folds of YAML with the modules of a user-data file and of /etc/cloud/cloud.cfg
    /// known; the power states, the schedules, the file systems and the modes are values. The "#cloud-config" line is
    /// the marker the detector recognizes even when the file has another name.
    /// </summary>
    private static bool TokenizeCloudInit(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(CloudInitKeywords, CloudInitValues, startState, text, sink, out endState);
    }

    // ------------------------------------------------------------- Prometheus

    private static readonly HashSet<string> PrometheusKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope and the global settings
        @"
          global rule_files scrape_configs scrape_config_files alerting remote_write remote_read storage tracing
          scrape_interval scrape_timeout evaluation_interval external_labels query_log_file
          body_size_limit sample_limit target_limit label_limit label_name_length_limit label_value_length_limit
          keep_dropped_targets",
        // the scrape configurations and the service discovery
        @"
          job_name metrics_path scheme params basic_auth authorization oauth2 bearer_token bearer_token_file
          tls_config proxy_url no_proxy follow_redirects enable_http2 static_configs targets labels
          file_sd_configs files refresh_interval http_sd_configs dns_sd_configs names type port
          kubernetes_sd_configs role api_server kubeconfig_file namespaces selectors
          consul_sd_configs ec2_sd_configs azure_sd_configs gce_sd_configs openstack_sd_configs docker_sd_configs
          dockerswarm_sd_configs marathon_sd_configs nerve_sd_configs serverset_sd_configs triton_sd_configs
          digitalocean_sd_configs hetzner_sd_configs ionos_sd_configs linode_sd_configs nomad_sd_configs
          ovhcloud_sd_configs puppetdb_sd_configs scaleway_sd_configs vultr_sd_configs eureka_sd_configs",
        // the relabeling of the targets and the metrics
        @"
          relabel_configs metric_relabel_configs source_labels separator target_label regex modulus replacement action",
        // the alert managers and the rules
        @"
          alertmanagers timeout api_version path_prefix http_headers groups name rules alert expr for record
          keep_firing_for limit annotations summary description runbook_url",
        // the remote storage and its queue
        @"
          url remote_timeout write_relabel_configs queue_config capacity max_shards min_shards max_samples_per_send
          batch_send_deadline min_backoff max_backoff retry_on_http_429 sample_age_limit metadata_config send
          send_exemplars send_native_histograms protobuf_message sigv4 azuread google_iam round_robin read_recent",
        // the TLS, the authentication and the headers
        @"
          ca_file cert_file key_file server_name insecure_skip_verify min_version max_version ca cert key
          username password credentials headers",
        // the local storage and the tracing
        @"
          tsdb out_of_order_time_window retention wal_compression block_ranges no_lockfile
          endpoint client_type sampling_fraction insecure compression otlp promote_resource_attributes
          translation_strategy");

    private static readonly HashSet<string> PrometheusValues = Words(StringComparer.OrdinalIgnoreCase,
        // the schemes and the authentication type
        @"http https Bearer",
        // the relabeling actions
        @"replace keep drop hashmod labelmap labeldrop labelkeep lowercase uppercase keepequal dropequal",
        // the roles of the service discovery
        @"pod service endpoints endpointslice ingress");

    /// <summary>
    /// Prometheus: the colours and the folds of YAML with the settings of prometheus.yml and of the rule files known;
    /// the schemes, the relabeling actions and the service discovery roles are values.
    /// </summary>
    private static bool TokenizePrometheus(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(PrometheusKeywords, PrometheusValues, startState, text, sink, out endState);
    }

    // ------------------------------------------------------------ Alertmanager

    private static readonly HashSet<string> AlertmanagerKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope and the global settings
        @"
          global route receivers inhibit_rules templates mute_time_intervals time_intervals resolve_timeout
          smtp_smarthost smtp_from smtp_hello smtp_auth_username smtp_auth_password smtp_auth_identity
          smtp_auth_secret smtp_require_tls smtp_tls_config slack_api_url slack_api_url_file victorops_api_key
          victorops_api_url pagerduty_url opsgenie_api_key opsgenie_api_key_file opsgenie_api_url wechat_api_url
          wechat_api_secret wechat_api_corp_id telegram_api_url webex_api_url http_config proxy_url",
        // the routing tree
        @"
          receiver group_by group_wait group_interval repeat_interval match match_re matchers continue routes
          mute_time_intervals active_time_intervals",
        // the receivers and the integrations
        @"
          name email_configs pagerduty_configs pushover_configs slack_configs opsgenie_configs webhook_configs
          victorops_configs wechat_configs webex_configs msteams_configs sns_configs telegram_configs
          discord_configs",
        // the fields of the integrations
        @"
          to from smarthost hello auth_username auth_password auth_identity auth_secret require_tls tls_config
          send_resolved headers html text title body url url_file service_key routing_key api_url api_key channel
          api_key_file corp_id agent_id secret chat_id message message_thread_id parse_mode entity_id user_key
          token room_id color fields short_fields monospace attachments max_alerts timeout insecure_skip_verify
          ca_file cert_file key_file server_name min_version max_version basic_auth username password
          password_file authorization type credentials credentials_file bearer_token bearer_token_file oauth2
          client_id client_secret client_secret_file scopes endpoint_params",
        // the inhibition and the time intervals
        @"
          source_match source_match_re source_matchers target_match target_match_re target_matchers equal
          weekdays days_of_month months years location start_time end_time");

    private static readonly HashSet<string> AlertmanagerValues = Words(StringComparer.OrdinalIgnoreCase,
        // the schemes, the authorization type and the message formats
        @"http https Bearer HTML Markdown MarkdownV2",
        // the weekdays of a time interval
        @"monday tuesday wednesday thursday friday saturday sunday");

    /// <summary>
    /// Alertmanager: the colours and the folds of YAML with the routing tree, the receivers and the inhibition rules
    /// known; the schemes, the authorization type, the message formats and the weekdays are values.
    /// </summary>
    private static bool TokenizeAlertmanager(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(AlertmanagerKeywords, AlertmanagerValues, startState, text, sink, out endState);
    }

    // ---------------------------------------------------------------- MongoDB

    private static readonly HashSet<string> MongoDbKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope, the logging and the process
        @"
          systemLog destination path logAppend logRotate verbosity quiet component timeStampFormat syslogFacility
          processManagement fork pidFilePath timeZoneInfo",
        // the network and the TLS connection
        @"
          net port bindIp bindIpAll ipv6 maxIncomingConnections unixDomainSocket compression serviceExecutor
          ssl tls mode PEMKeyFile PEMKeyPassword clusterFile clusterPassword CAFile CRLFile
          allowConnectionsWithoutCertificates allowInvalidCertificates allowInvalidHostnames disabledProtocols
          FIPSMode certificateSelector certificateKeyFile certificateKeyFilePassword",
        // the authentication and the encryption at rest
        @"
          security authorization keyFile clusterAuthMode javascriptEnabled redactClientLogData enableEncryption
          encryptionCipherMode kmip sasl authenticationRestrictions clientSource serverAddress
          host serviceName saslMechanisms saslSource queryUser queryPassword userCacheInvalidationIntervalSecs",
        // the storage engine and the journal
        @"
          storage dbPath journal enabled commitIntervalMs directoryPerDB syncPeriodSecs engine wiredTiger
          collectionConfig blockCompressor indexConfig prefixCompression journalCompressor engineConfig cacheSizeGB
          statisticsLogDelaySecs directoryForIndexes configString mmapv1 nsSize smallFiles inMemory inMemorySizeGB
          ephemeralForTest repairPath oplogMinRetentionHours usePowerOf2Sizes",
        // the profiler, the replication and the sharding
        @"
          operationProfiling slowOpThresholdMs slowOpSampleRate filter
          replication replSetName oplogSizeMB enableMajorityReadConcern chainingAllowed catchUpTimeoutMillis
          electionTimeoutMillis heartbeatIntervalMillis heartbeatTimeoutSecs rollbackTimeLimitSecs priority
          arbiterOnly buildIndexes hidden secondaryDelaySecs votes tags getLastErrorModes getLastErrorDefaults
          useHostName useIPv6 localPingThresholdMs waitForSecondaryBeforeNoopWriteMS
          sharding clusterRole shardingState configDB archiveMovedChunks chunkSize balancer autoSplit
          proposedSplitVectorThresholdMB migrateChunkSizeBytes",
        // the audit log, the parameters and the search index
        @"
          auditLog format compressionMode snmp subagent
          setParameter enableLocalhostAuthBypass authenticationMechanisms notablescan logLevel
          diagnosticDataCollectionEnabled textSearch searchScore indexRoller");

    private static readonly HashSet<string> MongoDbValues = Words(StringComparer.OrdinalIgnoreCase,
        // the log destinations, the TLS modes and the states
        @"file syslog console JSON BSON enabled disabled
          requireTLS allowTLS preferTLS
          keyFile sendKeyFile sendKeyAndCertificate x509",
        // the storage engines and the roles of a node
        @"wiredTiger inMemory ephemeralForTest mmapv1 configsvr shardsvr",
        // the profiling modes and the choices of the options
        @"off slowOp all none reopen
          SCRAM-SHA-1 SCRAM-SHA-256 MONGODB-CR snappy zlib zstd noop");

    /// <summary>
    /// MongoDB: the colours and the folds of YAML with the settings of mongod.conf and mongos.conf known; the log
    /// destinations, the TLS modes, the storage engines, the node roles and the profiling modes are values.
    /// </summary>
    private static bool TokenizeMongoDb(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(MongoDbKeywords, MongoDbValues, startState, text, sink, out endState);
    }

    // ----------------------------------------------------------- Elasticsearch

    private static readonly HashSet<string> ElasticsearchKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the cluster and the node
        @"
          cluster.name cluster.uuid cluster.initial_master_nodes cluster.initial_cluster_manager_nodes
          cluster.max_shards_per_node cluster.info.update.interval
          node.name node.roles node.attr node.data node.master node.ingest node.ml node.max_local_storage_nodes",
        // the paths of the data and the logs
        @"
          path.data path.logs path.repo path.scripts path.home",
        // the network and the HTTP layer
        @"
          network.host network.bind_host network.publish_host network.tcp.keep_alive network.tcp.no_delay
          network.tcp.send_buffer_size network.tcp.receive_buffer_size
          http.port http.host http.bind_host http.publish_host http.enabled http.compression
          http.cors.enabled http.cors.allow-origin http.cors.allow-headers http.cors.allow-credentials
          http.cors.allow-methods http.max_content_length http.max_initial_line_length http.max_header_size
          transport.port transport.host transport.bind_host transport.publish_host transport.tcp.compress
          transport.compress transport.type",
        // the discovery, the gateway and the bootstrap checks
        @"
          discovery.type discovery.seed_hosts discovery.seed_providers discovery.cluster_formation_warning_timeout
          discovery.find_peers_interval discovery.probe.connect_timeout discovery.probe.handshake_timeout
          discovery.request_peers_timeout
          gateway.recover_after_nodes gateway.recover_after_data_nodes gateway.expected_nodes
          gateway.expected_data_nodes gateway.recover_after_time
          bootstrap.memory_lock bootstrap.system_call_filter bootstrap.systems",
        // the security and the TLS certificates
        @"
          xpack.security.enabled xpack.security.transport.ssl.enabled
          xpack.security.transport.ssl.verification_mode xpack.security.transport.ssl.keystore.path
          xpack.security.transport.ssl.keystore.password xpack.security.transport.ssl.keystore.secure_password
          xpack.security.transport.ssl.truststore.path xpack.security.transport.ssl.truststore.password
          xpack.security.transport.ssl.truststore.secure_password xpack.security.transport.ssl.certificate
          xpack.security.transport.ssl.certificate_authorities xpack.security.transport.ssl.key
          xpack.security.http.ssl.enabled xpack.security.http.ssl.keystore.path
          xpack.security.http.ssl.keystore.password xpack.security.http.ssl.keystore.secure_password
          xpack.security.http.ssl.truststore.path xpack.security.http.ssl.truststore.password
          xpack.security.http.ssl.certificate xpack.security.http.ssl.certificate_authorities
          xpack.security.http.ssl.key xpack.security.http.ssl.client_authentication
          xpack.security.authc.realms xpack.security.authc.api_key.enabled xpack.security.authc.token.enabled
          xpack.security.audit.enabled xpack.security.audit.outputs order",
        // the features of the subscription
        @"
          xpack.ml.enabled xpack.ml.max_machine_memory_percent xpack.monitoring.enabled
          xpack.monitoring.collection.enabled xpack.watcher.enabled xpack.ilm.enabled xpack.slm.enabled
          xpack.transform.enabled xpack.rollup.enabled xpack.ccr.enabled xpack.graph.enabled xpack.profiling.enabled",
        // the indices, the thread pools and the snapshots
        @"
          action.destructive_requires_name action.auto_create_index indices.query.bool.max_clause_count
          indices.fielddata.cache.size indices.memory.index_buffer_size indices.recovery.max_bytes_per_sec
          thread_pool.search.size thread_pool.search.queue_size thread_pool.write.size thread_pool.write.queue_size
          ingest.geoip.downloader.enabled reindex.remote.whitelist repositories.url.allowed_urls
          snapshot.max_concurrent_snapshots script.allowed_types script.allowed_contexts search.max_buckets
          cluster.routing.allocation.disk.threshold_enabled cluster.routing.allocation.disk.watermark.low
          cluster.routing.allocation.disk.watermark.high cluster.routing.allocation.disk.watermark.flood_stage
          cluster.routing.allocation.awareness.attributes cluster.routing.allocation.enable
          cluster.routing.rebalance.enable");

    private static readonly HashSet<string> ElasticsearchValues = Words(StringComparer.OrdinalIgnoreCase,
        // the discovery modes and the roles of the nodes
        @"single-node multi-node
          master data data_content data_hot data_warm data_cold data_frozen ingest ml remote_cluster_client
          transform voting_only coordinating_only index search",
        // the TLS checks, the client authentication and the realm types
        @"full certificate none optional required
          native ldap active_directory pki file saml oidc kerberos",
        // the allocation and rebalance choices, the audit outputs and the licenses
        @"all primaries newprimaries replicas
          deprecation index access
          basic trial gold platinum enterprise");

    /// <summary>
    /// Elasticsearch: the colours and the folds of YAML with the settings of elasticsearch.yml known; the discovery
    /// modes, the node roles, the TLS checks, the realm types and the allocation choices are values.
    /// </summary>
    private static bool TokenizeElasticsearch(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(ElasticsearchKeywords, ElasticsearchValues, startState, text, sink, out endState);
    }

    // -------------------------------------------------------------- Kubernetes

    private static readonly HashSet<string> KubernetesKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope and the metadata
        @"apiVersion kind metadata spec status name generateName namespace labels annotations ownerReferences finalizers
          uid resourceVersion generation creationTimestamp deletionTimestamp deletionGracePeriodSeconds managedFields
          // the pods and their containers
          containers initContainers ephemeralContainers image imagePullPolicy command args workingDir ports containerPort
          hostPort hostIP protocol env envFrom prefix configMapRef secretRef value valueFrom fieldRef resourceFieldRef
          configMapKeyRef secretKeyRef resources requests limits cpu memory hugepages volumeMounts mountPath
          mountPropagation subPath subPathExpr readOnly volumeDevices devicePath
          livenessProbe readinessProbe startupProbe httpGet tcpSocket grpc exec scheme host httpHeaders initialDelaySeconds
          periodSeconds timeoutSeconds successThreshold failureThreshold terminationGracePeriodSeconds lifecycle postStart
          preStop securityContext runAsUser runAsGroup runAsNonRoot allowPrivilegeEscalation readOnlyRootFilesystem
          privileged capabilities add drop seccompProfile appArmorProfile fsGroup fsGroupChangePolicy supplementalGroups
          sysctls serviceAccountName serviceAccount automountServiceAccountToken dnsPolicy dnsConfig nameservers searches
          options nodeName nodeSelector restartPolicy terminationMessagePath terminationMessagePolicy hostNetwork hostPID
          hostIPC shareProcessNamespace schedulerName priorityClassName priority preemptionPolicy
          // scheduling
          tolerations affinity nodeAffinity podAffinity podAntiAffinity requiredDuringSchedulingIgnoredDuringExecution
          preferredDuringSchedulingIgnoredDuringExecution matchExpressions matchFields matchLabels operator values weight topologyKey
          topologySpreadConstraints maxSkew whenUnsatisfiable labelSelector nodeSelectorTerms
          // the volumes
          volumes configMap secret emptyDir hostPath persistentVolumeClaim claimName storageClassName accessModes nfs server
          iscsi targetPortal iqn lun rbd cephfs glusterfs awsElasticBlockStore gcePersistentDisk azureDisk azureFile csi
          driver volumeAttributes projected downwardAPI serviceAccountToken expirationSeconds audience path medium sizeLimit
          defaultMode optional items key mode
          // the services and the ingress
          service type clusterIP clusterIPs externalIPs loadBalancerIP loadBalancerSourceRanges externalName
          externalTrafficPolicy internalTrafficPolicy sessionAffinity sessionAffinityConfig selector port targetPort nodePort
          appProtocol publishNotReadyAddresses healthCheckNodePort ipFamilyPolicy ipFamilies allocateLoadBalancerNodePorts
          ingressClassName tls hosts secretName rules http paths pathType backend serviceName servicePort resource
          defaultBackend
          // the workloads
          replicas revisionHistoryLimit paused strategy rollingUpdate maxSurge maxUnavailable minReadySeconds
          progressDeadlineSeconds template updateStrategy podManagementPolicy volumeClaimTemplates
          persistentVolumeReclaimPolicy allowedTopologies schedule concurrencyPolicy suspend successfulJobsHistoryLimit
          failedJobsHistoryLimit startingDeadlineSeconds completions parallelism backoffLimit activeDeadlineSeconds
          ttlSecondsAfterFinished
          // RBAC, autoscaling and the policies
          apiGroups resources resourceNames verbs nonResourceURLs subjects roleRef apiGroup aggregationRule
          clusterRoleSelectors minReplicas maxReplicas metrics scaleTargetRef behavior scaleUp scaleDown
          stabilizationWindowSeconds policies policy podSelector policyTypes from to namespaceSelector ipBlock cidr except
          hard minAvailable disruptionBudget
          // custom resources
          group scope names plural singular shortNames categories versions served storage schema openAPIV3Schema
          properties required additionalProperties description format enum conversion webhook admissionReviewVersions
          clientConfig caBundle
          // Helm
          chart apiVersion appVersion kubeVersion keywords home sources dependencies maintainers icon condition tags
          repository alias deprecated");

    private static readonly HashSet<string> KubernetesValues = Words(StringComparer.OrdinalIgnoreCase,
        // the kinds
        @"Pod Deployment Service StatefulSet DaemonSet ReplicaSet Job CronJob ConfigMap Secret Ingress Namespace
          PersistentVolume PersistentVolumeClaim ServiceAccount Role RoleBinding ClusterRole ClusterRoleBinding NetworkPolicy
          PodDisruptionBudget HorizontalPodAutoscaler StorageClass CustomResourceDefinition ResourceQuota LimitRange
          Endpoints EndpointSlice PriorityClass RuntimeClass CSIDriver CSINode VolumeAttachment APIService
          // the api versions
          v1 v1beta1 v1beta2 v2 v2beta1 v2beta2
          // the choices of the fields
          Always IfNotPresent Never OnFailure TCP UDP SCTP ClusterIP NodePort LoadBalancer ExternalName RollingUpdate Recreate
          ReadWriteOnce ReadOnlyMany ReadWriteMany ReadWriteOncePod NoSchedule PreferNoSchedule NoExecute In NotIn Exists
          DoesNotExist Gt Lt DoNotSchedule ScheduleAnyway Allow Forbid Replace ClusterFirst Default ClusterFirstWithHostNet
          application library Delete Retain Recycle RuntimeDefault Unconfined Localhost
          // Helm template functions
          include template tpl required default quote squote toYaml fromYaml toJson fromJson nindent indent trim upper lower
          title trunc replace printf range with if else end define block fail lookup semverCompare
          b64enc sha256sum merge coalesce ternary dig toPrettyJson genCA");

    /// <summary>
    /// Kubernetes and Helm: the colours and the folds of YAML with the keys of the manifests and the chart metadata
    /// known, and the kinds, api versions and Helm template functions coloured as values.
    /// </summary>
    private static bool TokenizeKubernetes(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(KubernetesKeywords, KubernetesValues, startState, text, sink, out endState);
    }

    // ----------------------------------------------------------- Docker Compose

    private static readonly HashSet<string> DockerComposeKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the envelope
        @"version name services networks volumes secrets configs
          include develop models provider gpus
          // the services
          image build context dockerfile dockerfile_inline additional_contexts args target cache_from cache_to labels
          pull network container_name command entrypoint environment env_file expose ports tmpfs network_mode hostname
          domainname mac_address depends_on restart deploy profiles scale
          healthcheck test interval timeout retries start_period start_interval disable
          privileged user working_dir stdin_open tty stop_grace_period stop_signal sysctls ulimits cap_add cap_drop
          devices dns dns_search extra_hosts group_add init isolation links logging driver options pid platform
          read_only security_opt shm_size userns_mode extends file service
          annotations blkio_config cgroup cgroup_parent cpu_count cpu_percent cpu_shares cpu_quota cpu_period cpuset
          credential_spec device_cgroup_rules oom_kill_disable oom_score_adj uts
          // the networks, the volumes and the deploy settings
          external external_links ipam subnet gateway ip_range aux_addresses enable_ipv4 enable_ipv6 internal attachable
          resources reservations limits cpus memory pids restart_policy condition delay max_attempts window
          update_config parallelism failure_action monitor order rollback_config placement constraints preferences
          endpoint_mode mode replicas node role");

    private static readonly HashSet<string> DockerComposeValues = Words(StringComparer.OrdinalIgnoreCase,
        @"bridge host none service always unless-stopped on-failure local global replicated dnsrr vip
          tcp udp sctp json-file syslog journald gelf fluentd awslogs splunk
          asc desc spread stop-first start-first pause continue manager worker ingress latest");

    /// <summary>
    /// Docker Compose: the colours and the folds of YAML with the keys of the services, networks, volumes and deploy
    /// settings known, and the choices of the drivers, policies and modes coloured as values.
    /// </summary>
    private static bool TokenizeDockerCompose(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(DockerComposeKeywords, DockerComposeValues, startState, text, sink, out endState);
    }

    // ------------------------------------------------------------------ Ansible

    private static readonly HashSet<string> AnsibleKeywords = Words(StringComparer.OrdinalIgnoreCase,
        // the play
        @"name hosts remote_user port connection become become_user become_method become_exe become_flags become_pass
          gather_facts gather_subset gather_timeout fact_path vars vars_files vars_prompt roles tasks handlers pre_tasks
          post_tasks force_handlers any_errors_fatal max_fail_percentage serial strategy order throttle run_once
          delegate_to delegate_facts module_defaults environment collections check_mode diff no_log ignore_errors
          ignore_unreachable tags
          // the task
          when loop loop_control with_items with_dict with_list with_fileglob with_together with_nested with_sequence
          with_indexed_items with_first_found with_random_choice with_cartesian with_flattened with_subelements with_ini
          with_inventory_hostnames with_password with_lines until retries delay register failed_when changed_when notify
          listen block rescue always async poll
          include_tasks import_tasks include_role import_role include_vars include import_playbook
          // the modules
          apt apt_key apt_repository yum yum_repository dnf rpm_key package copy template file lineinfile blockinfile
          replace assemble fetch synchronize unarchive archive command shell script raw expect service systemd sysvinit
          runit supervisorctl user group authorized_key git subversion hg get_url uri slurp cron at timezone mount parted
          lvg lvol filesystem stat debug assert fail pause wait_for wait_for_connection meta set_fact local_action
          state src dest path owner group mode content backup validate force recurse follow create regexp line
          insertafter insertbefore marker remote_src attributes seuser serole setype selevel unsafe_writes enabled
          docker_container docker_image docker_network docker_volume docker_compose_v2 k8s helm
          mysql_db mysql_user mysql_query postgresql_db postgresql_user postgresql_query redis redis_info
          pip composer npm yarn gem");

    private static readonly HashSet<string> AnsibleValues = Words(StringComparer.OrdinalIgnoreCase,
        // the states and the choices of the play
        @"present absent started stopped restarted reloaded latest enabled disabled mounted unmounted link file directory
          touch yes no true false always never any all first last on_success on_failure
          ssh local docker paramiko_ssh winrm sudo su doas pbrun pfexec runas
          linear free host_pinned debug");

    /// <summary>
    /// Ansible: the colours and the folds of YAML with the keys of the plays, the tasks and the roles known, and the
    /// modules (apt, copy, service, command, ...) and the common states (present, started, enabled) coloured too.
    /// </summary>
    private static bool TokenizeAnsible(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeYamlCore(AnsibleKeywords, AnsibleValues, startState, text, sink, out endState);
    }

    // ------------------------------------------------------------- PostgreSQL

    private static readonly HashSet<string> PostgreSqlDirectives = Words(StringComparer.OrdinalIgnoreCase,
        // postgresql.conf: the settings a server is tuned with
        @"listen_addresses port max_connections superuser_reserved_connections reserved_connections
          shared_buffers work_mem maintenance_work_mem effective_cache_size huge_pages huge_page_size
          temp_buffers max_prepared_transactions wal_level wal_buffers wal_keep_size max_wal_size min_wal_size
          wal_compression wal_log_hints full_page_writes fsync synchronous_commit checkpoint_timeout
          checkpoint_completion_target max_wal_senders max_replication_slots hot_standby
          primary_conninfo primary_slot_name restore_command archive_mode archive_command archive_timeout
          recovery_target recovery_target_action recovery_target_time recovery_target_name
          password_encryption scram_iterations authentication_timeout ssl ssl_cert_file ssl_key_file ssl_ca_file
          ssl_ciphers ssl_min_protocol_version ssl_max_protocol_version ssl_prefer_server_ciphers
          unix_socket_directories unix_socket_permissions listen_addresses hba_file ident_file config_file
          data_directory external_pid_file timezone log_timezone log_destination logging_collector
          log_directory log_filename log_rotation_age log_rotation_size log_truncate_on_rotation log_line_prefix
          log_min_messages log_min_duration_statement log_statement log_checkpoints log_connections
          log_disconnections log_duration log_lock_waits log_temp_files log_autovacuum_min_duration
          logging_collector syslog_facility syslog_ident syslog_sequence_numbers syslog_split_messages
          autovacuum autovacuum_max_workers autovacuum_naptime autovacuum_vacuum_threshold
          autovacuum_vacuum_scale_factor autovacuum_analyze_threshold autovacuum_analyze_scale_factor
          autovacuum_freeze_max_age autovacuum_multixact_freeze_max_age autovacuum_vacuum_cost_delay
          autovacuum_vacuum_cost_limit vacuum_cost_delay vacuum_cost_limit vacuum_cost_page_hit
          vacuum_cost_page_miss vacuum_cost_page_dirty vacuum_freeze_min_age vacuum_freeze_table_age
          bgwriter_delay bgwriter_lru_maxpages bgwriter_lru_multiplier bgwriter_flush_after
          backend_flush_after effective_io_concurrency maintenance_io_concurrency random_page_cost
          seq_page_cost cpu_tuple_cost cpu_index_tuple_cost cpu_operator_cost parallel_setup_cost
          parallel_tuple_cost min_parallel_table_scan_size min_parallel_index_scan_size
          max_parallel_workers max_parallel_workers_per_gather max_worker_processes
          max_parallel_maintenance_workers default_statistics_target default_tablespace
          default_transaction_isolation default_transaction_read_only statement_timeout lock_timeout
          idle_in_transaction_session_timeout idle_session_timeout client_encoding client_min_messages
          search_path datestyle intervalstyle extra_float_digits standard_conforming_strings
          bytea_output xmlbinary xmloption escape_string_warning backslash_quote standard_conforming_strings
          lc_messages lc_monetary lc_numeric lc_time default_text_search_config
          shared_preload_libraries session_preload_libraries local_preload_libraries
          dynamic_library_path jit jit_provider jit_above_cost jit_inline_above_cost jit_optimize_above_cost
          max_files_per_process max_locks_per_transaction max_pred_locks_per_transaction
          max_function_args max_identifier_length max_index_keys max_stack_depth
          track_activities track_counts track_io_timing track_functions track_commit_timestamp
          update_process_title cluster_name application_name bonjour bonjour_name
          allow_system_table_mods data_checksums ignore_checksum_failure zero_damaged_pages
          row_security constraint_exclusion from_collapse_limit join_collapse_limit
          enable_seqscan enable_indexscan enable_indexonlyscan enable_bitmapscan enable_sort
          enable_hashagg enable_hashjoin enable_mergejoin enable_nestloop enable_material
          enable_partitionwise_join enable_partitionwise_aggregate enable_parallel_append
          enable_parallel_hash enable_incremental_sort enable_memoize enable_presorted_aggregate
          enable_partition_pruning enable_async_append enable_gathermerge
          quote_all_identifiers transform_null_equals array_nulls remove_temp_files_after_crash",
        // pg_hba.conf: the connection types
        @"local host hostssl hostnossl include include_if_exists include_dir");

    private static readonly HashSet<string> PostgreSqlValues = Words(StringComparer.OrdinalIgnoreCase,
        // pg_hba.conf: databases, users and authentication methods
        @"all sameuser samerole replication
          trust reject md5 scram-sha-256 password gss sspi krb5 ident peer pam ldap radius cert
          map clientcert clientname ldapserver ldapport ldapprefix ldapsuffix ldapbasedn ldapbinddn
          ldapbindpasswd ldapsearchattribute ldapsearchfilter ldaptls
          on off true false yes no minimal replica logical archive
          read committed repeatable serializable
          local0 local1 local2 local3 local4 local5 local6 local7
          stderr csvlog jsonlog syslog eventlog");

    /// <summary>
    /// PostgreSQL: the colours of Config with the settings of postgresql.conf known, and the connection types,
    /// authentication methods and option names of pg_hba.conf. Keys stay case insensitive like the server's.
    /// </summary>
    private static bool TokenizePostgreSql(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(PostgreSqlDirectives, startState, text, sink, out endState, valueKeywords: PostgreSqlValues);
    }

    // -------------------------------------------------------------------- TOML

    private static readonly HashSet<string> TomlKeywords = Words(StringComparer.Ordinal,
        @"true false");

    // Line state: a multi-line basic ("") or literal ('') string continues on the next line; the rest of
    // the state is the depth of the [ ] arrays, so a "[" at the start of a line is a table only at depth 0.
    private const int StTomlBasicString = 0x01;
    private const int StTomlLiteralString = 0x02;
    private const int TomlDepthShift = 2;
    private const int TomlDepthMax = 31;

    private static bool IsTomlBareKeyChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '-';
    }

    /// <summary>Index after the closing "]" of a [table] or [[array of tables]] header, or -1.</summary>
    private static int TomlSectionEnd(string text, int start)
    {
        bool array = start + 1 < text.Length && text[start + 1] == '[';
        int i = start + (array ? 2 : 1);
        char quote = '\0';
        while (i < text.Length)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i += 2;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                }
                i++;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                quote = c;
                i++;
                continue;
            }
            if (c == ']')
            {
                if (!array)
                {
                    return i + 1;
                }
                if (i + 1 < text.Length && text[i + 1] == ']')
                {
                    return i + 2;
                }
            }
            i++;
        }
        return -1;
    }

    /// <summary>Index after the closing ' of a literal string that starts at start, or -1.</summary>
    private static int FindTomlLiteralEnd(string text, int start)
    {
        int close = text.IndexOf('\'', start + 1);
        return (close < 0) ? -1 : (close + 1);
    }

    /// <summary>
    /// Index after the closing delimiter of a multi-line string that starts at <paramref name="start"/>, or -1
    /// when the string continues on the next line. In a basic string a backslash escapes the next character;
    /// up to two quotes before the delimiter belong to the text.
    /// </summary>
    private static int TomlMultilineEnd(string text, int start, bool basic)
    {
        char quote = basic ? '"' : '\'';
        int i = start;
        while (i < text.Length)
        {
            if (basic && text[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (i + 2 < text.Length && text[i] == quote && text[i + 1] == quote && text[i + 2] == quote)
            {
                int end = i + 3;
                for (int extra = 0; extra < 2 && end < text.Length && text[end] == quote; extra++)
                {
                    end++;
                }
                return end;
            }
            i++;
        }
        return -1;
    }

    /// <summary>Index after a number or a date/time that starts at <paramref name="start"/>.</summary>
    private static int TomlNumberEnd(string text, int start)
    {
        int len = text.Length;
        int i = start;
        if (i < len && (text[i] == '+' || text[i] == '-'))
        {
            i++;
        }
        // A signed inf or nan.
        if (i + 2 < len && (string.Compare(text, i, "inf", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(text, i, "nan", 0, 3, StringComparison.OrdinalIgnoreCase) == 0))
        {
            return i + 3;
        }
        bool date = i + 9 < len && IsDigit(text[i]) && IsDigit(text[i + 1]) && IsDigit(text[i + 2]) &&
            IsDigit(text[i + 3]) && text[i + 4] == '-' && IsDigit(text[i + 5]) && IsDigit(text[i + 6]) &&
            text[i + 7] == '-' && IsDigit(text[i + 8]) && IsDigit(text[i + 9]);
        while (i < len)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '+' || c == '-')
            {
                i++;
                continue;
            }
            // The space in "1979-05-27 07:32:00".
            if (c == ' ' && date && i + 3 < len && IsDigit(text[i + 1]) && IsDigit(text[i + 2]) && text[i + 3] == ':')
            {
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>
    /// TOML: # comments, [tables] and [[arrays of tables]], bare, quoted and dotted keys, basic and literal
    /// strings (single-line and multi-line), numbers, dates, true/false, and the foldable [ ] and { } of arrays
    /// and inline tables.
    /// </summary>
    private static bool TokenizeToml(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        int len = text.Length;
        int last = 0;
        int i = 0;
        int depth = (startState >> TomlDepthShift) & TomlDepthMax;
        int strings = startState & (StTomlBasicString | StTomlLiteralString);
        endState = 0;

        if (strings != 0)
        {
            bool basic = (strings & StTomlBasicString) != 0;
            int end = TomlMultilineEnd(text, 0, basic);
            if (end < 0)
            {
                if (len > 0)
                {
                    sink.AddToken(TypeString, 0, len);
                }
                endState = strings | (depth << TomlDepthShift);
                return true;
            }
            sink.AddToken(TypeString, 0, end);
            i = end;
            last = end;
        }

        i = SkipBlanks(text, i);
        if (i < len && text[i] == '#')
        {
            Normal(ref last, last, i, sink);
            sink.AddToken(TypeComment, i, len);
            endState = depth << TomlDepthShift;
            return true;
        }
        if (i < len && text[i] == '[' && depth == 0)
        {
            int end = TomlSectionEnd(text, i);
            Normal(ref last, last, i, sink);
            sink.AddToken(TypeSection, i, (end < 0) ? len : end);
            if (end < 0)
            {
                endState = 0;
                return true;
            }
            i = end;
            last = end;
        }

        while (i < len)
        {
            char c = text[i];

            if (c == '#')
            {
                Normal(ref last, last, i, sink);
                sink.AddToken(TypeComment, i, len);
                endState = depth << TomlDepthShift;
                return true;
            }

            if (c == '"' || c == '\'')
            {
                bool basic = c == '"';
                if (i + 2 < len && text[i + 1] == c && text[i + 2] == c)
                {
                    Normal(ref last, last, i, sink);
                    int end = TomlMultilineEnd(text, i + 3, basic);
                    if (end < 0)
                    {
                        sink.AddToken(TypeString, i, len);
                        endState = (basic ? StTomlBasicString : StTomlLiteralString) | (depth << TomlDepthShift);
                        return true;
                    }
                    sink.AddToken(TypeString, i, end);
                    i = end;
                    last = end;
                    continue;
                }
                int close = basic ? FindQuoteEnd(text, i, c) : FindTomlLiteralEnd(text, i);
                int stop = (close < 0) ? len : close;
                int probe = SkipBlanks(text, stop);
                bool isKey = probe < len && (text[probe] == '=' || text[probe] == '.');
                Normal(ref last, last, i, sink);
                sink.AddToken(isKey ? TypeVariable : TypeString, i, stop);
                i = stop;
                last = i;
                continue;
            }

            if (c == '[')
            {
                TryAddBracket(text, i, ref last, sink, foldSquareBrackets: true);
                depth = Math.Min(depth + 1, TomlDepthMax);
                i++;
                continue;
            }
            if (c == ']')
            {
                TryAddBracket(text, i, ref last, sink, foldSquareBrackets: true);
                if (depth > 0)
                {
                    depth--;
                }
                i++;
                continue;
            }
            if (c == '{' || c == '}')
            {
                TryAddBracket(text, i, ref last, sink, foldCurlyBraces: true);
                i++;
                continue;
            }

            if (IsDigit(c) || ((c == '+' || c == '-') && i + 1 < len &&
                (IsDigit(text[i + 1]) || text[i + 1] == 'i' || text[i + 1] == 'n')))
            {
                int end = TomlNumberEnd(text, i);
                int probe = SkipBlanks(text, end);
                // A bare key may start with a digit: 1234 = "x", 1979.05 = 1.
                bool isKey = probe < len && text[probe] == '=';
                Normal(ref last, last, i, sink);
                sink.AddToken(isKey ? TypeVariable : TypeNumber, i, end);
                i = end;
                last = i;
                continue;
            }

            if (IsTomlBareKeyChar(c))
            {
                int s = i;
                while (i < len && IsTomlBareKeyChar(text[i]))
                {
                    i++;
                }
                string word = text.Substring(s, i - s);
                int probe = SkipBlanks(text, i);
                bool isKey = probe < len && (text[probe] == '=' || text[probe] == '.');
                Normal(ref last, last, s, sink);
                if (isKey)
                {
                    sink.AddToken(TypeVariable, s, i);
                    last = i;
                }
                else if (TomlKeywords.Contains(word))
                {
                    sink.AddToken(TypeKeyword, s, i);
                    last = i;
                }
                else if (string.Equals(word, "inf", StringComparison.Ordinal) ||
                    string.Equals(word, "nan", StringComparison.Ordinal))
                {
                    sink.AddToken(TypeNumber, s, i);
                    last = i;
                }
                continue;
            }

            i++;
        }

        Normal(ref last, last, len, sink);
        endState = depth << TomlDepthShift;
        return true;
    }

    // -------------------------------------------------------------------- logs

    private static readonly HashSet<string> LogLevelWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "FATAL", "CRITICAL", "CRIT", "SEVERE", "EMERG", "EMERGENCY", "ALERT", "ERROR", "ERR", "WARNING", "WARN", "NOTICE", "INFO", "DEBUG", "TRACE", "VERBOSE"
    };

    // ------------------------------------------------------------ tokenizers

    private static readonly TokenCache.LineTokenizer SystemdTokenizer =
        (int startState, string text, TokenCache.ITokenSink sink, out int endState) => TokenizeConfigCore(SystemdDirectives, startState, text, sink, out endState);

    private static readonly TokenCache.LineTokenizer NginxTokenizer =
        (int startState, string text, TokenCache.ITokenSink sink, out int endState) => TokenizeConfigCore(NginxDirectives, startState, text, sink, out endState, foldBraces: true);

    private static readonly TokenCache.LineTokenizer SshConfigTokenizer =
        (int startState, string text, TokenCache.ITokenSink sink, out int endState) => TokenizeConfigCore(SshDirectives, startState, text, sink, out endState);

    private static readonly string[] MonthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    /// <summary>The colour of a log level written as a word (any case), or -1 when the word is none.</summary>
    private static int LogLevelType(string word)
    {
        switch (word.ToUpperInvariant())
        {
            case "FATAL":
            case "CRITICAL":
            case "CRIT":
            case "SEVERE":
            case "EMERG":
            case "EMERGENCY":
            case "ALERT":
            case "ERROR":
            case "ERR":
            case "PANIC":
                return TypeString;
            case "WARNING":
            case "WARN":
                return TypeNumber;
            case "NOTICE":
            case "INFO":
                return TypeKeyword;
            case "DEBUG":
            case "TRACE":
            case "VERBOSE":
                return TypeComment;
            default:
                return -1;
        }
    }

    private static bool DigitsAt(string text, int index, int count)
    {
        if (index < 0 || index + count > text.Length)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!IsDigit(text[index + i]))
            {
                return false;
            }
        }
        return true;
    }

    // hh:mm:ss, with a fraction of a second (.123 or ,123) when there is one; the end of the time, or start when there is none.
    private static int MatchClock(string text, int start)
    {
        if (!DigitsAt(text, start, 2) || start + 8 > text.Length || text[start + 2] != ':' || !DigitsAt(text, start + 3, 2)
            || text[start + 5] != ':' || !DigitsAt(text, start + 6, 2))
        {
            return start;
        }
        int end = start + 8;
        if (end + 1 < text.Length && (text[end] == '.' || text[end] == ',') && IsDigit(text[end + 1]))
        {
            end++;
            while (end < text.Length && IsDigit(text[end]))
            {
                end++;
            }
        }
        return end;
    }

    // 2026-10-31, 2026-10-31 07:00:01, 2026-10-31T07:00:01.123Z, 2026-10-31T07:00:01+03:00, 2026/10/31 07:00:01 (nginx's error log)
    private static int MatchIsoTimestamp(string text, int start)
    {
        if (!DigitsAt(text, start, 4) || start + 10 > text.Length || (text[start + 4] != '-' && text[start + 4] != '/') || !DigitsAt(text, start + 5, 2)
            || text[start + 7] != text[start + 4] || !DigitsAt(text, start + 8, 2))
        {
            return start;
        }
        int end = start + 10;
        if (end < text.Length && (text[end] == 'T' || text[end] == ' '))
        {
            int clockEnd = MatchClock(text, end + 1);
            if (clockEnd > end + 1)
            {
                end = clockEnd;
                if (end < text.Length && text[end] == 'Z')
                {
                    end++;
                }
                else if (end + 2 < text.Length && (text[end] == '+' || text[end] == '-') && DigitsAt(text, end + 1, 2))
                {
                    end += 3;
                    if (end < text.Length && text[end] == ':' && DigitsAt(text, end + 1, 2))
                    {
                        end += 3;
                    }
                    else if (DigitsAt(text, end, 2))
                    {
                        end += 2;
                    }
                }
            }
        }
        return end;
    }

    // Oct 31 07:00:01, Oct  1 07:00:01 (syslog)
    private static int MatchSyslogTimestamp(string text, int start)
    {
        if (start + 3 > text.Length || Array.IndexOf(MonthNames, text.Substring(start, 3)) < 0 || start + 3 >= text.Length || text[start + 3] != ' ')
        {
            return start;
        }
        int day = start + 4;
        if (day < text.Length && text[day] == ' ')
        {
            day++;
        }
        int dayEnd = day;
        while (dayEnd < text.Length && dayEnd - day < 2 && IsDigit(text[dayEnd]))
        {
            dayEnd++;
        }
        if (dayEnd == day || dayEnd >= text.Length || text[dayEnd] != ' ')
        {
            return start;
        }
        int clockEnd = MatchClock(text, dayEnd + 1);
        return clockEnd > dayEnd + 1 ? clockEnd : start;
    }

    // 31/Oct/2026:07:00:01 +0000 (the time of a web server's access log, between brackets)
    private static int MatchCommonLogTimestamp(string text, int start)
    {
        if (!DigitsAt(text, start, 2) || start + 12 > text.Length || text[start + 2] != '/' || Array.IndexOf(MonthNames, text.Substring(start + 3, 3)) < 0
            || text[start + 6] != '/' || !DigitsAt(text, start + 7, 4) || text[start + 11] != ':')
        {
            return start;
        }
        int clockEnd = MatchClock(text, start + 12);
        if (clockEnd == start + 12)
        {
            return start;
        }
        if (clockEnd + 5 < text.Length && text[clockEnd] == ' ' && (text[clockEnd + 1] == '+' || text[clockEnd + 1] == '-') && DigitsAt(text, clockEnd + 2, 4))
        {
            clockEnd += 6;
        }
        return clockEnd;
    }

    private static int MatchLogTimestamp(string text, int start)
    {
        bool bracket = start < text.Length && text[start] == '[';
        int from = bracket ? start + 1 : start;
        int end = MatchIsoTimestamp(text, from);
        if (end == from)
        {
            end = MatchSyslogTimestamp(text, from);
        }
        if (end == from)
        {
            end = MatchCommonLogTimestamp(text, from);
        }
        if (end == from && !bracket)
        {
            end = MatchClock(text, from);
        }
        if (end == from)
        {
            return start;
        }
        if (bracket)
        {
            return end < text.Length && text[end] == ']' ? end + 1 : start;
        }
        return end;
    }

    /// <summary>
    /// A log line: the timestamp at its start, and the level of the message (ERROR, WARN, INFO, DEBUG in capitals, [error] or
    /// level=warn in the forms logs use). The rest of the line is left as it is. Nothing spans lines.
    /// </summary>
    private static bool TokenizeLog(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        endState = 0;
        int len = text.Length;
        int last = 0;
        int i = SkipBlanks(text, 0);
        int stampEnd = MatchLogTimestamp(text, i);
        if (stampEnd > i)
        {
            Normal(ref last, last, i, sink);
            sink.AddToken(TypeSection, i, stampEnd);
            i = stampEnd;
            last = i;
        }
        while (i < len)
        {
            char c = text[i];
            if (c == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > i + 1 && close - i <= 40)
                {
                    int stamp = MatchCommonLogTimestamp(text, i + 1);
                    if (stamp > i + 1 && stamp == close)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(TypeSection, i, close + 1);
                        i = close + 1;
                        last = i;
                        continue;
                    }
                    int type = LogLevelType(text.Substring(i + 1, close - i - 1));
                    if (type >= 0)
                    {
                        Normal(ref last, last, i, sink);
                        sink.AddToken(type, i, close + 1);
                        i = close + 1;
                        last = i;
                        continue;
                    }
                }
                i++;
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
                // Capitals only: a level in a sentence ("error", "Info") is a word.
                if (LogLevelWords.Contains(word))
                {
                    int type = LogLevelType(word);
                    Normal(ref last, last, s, sink);
                    sink.AddToken(type, s, i);
                    last = i;
                }
                else if (word == "level" && i + 1 < len && text[i] == '=')
                {
                    int valueStart = i + 1;
                    int valueEnd = valueStart;
                    while (valueEnd < len && IsIdChar(text[valueEnd]))
                    {
                        valueEnd++;
                    }
                    int type = valueEnd > valueStart ? LogLevelType(text.Substring(valueStart, valueEnd - valueStart)) : -1;
                    if (type >= 0)
                    {
                        Normal(ref last, last, valueStart, sink);
                        sink.AddToken(type, valueStart, valueEnd);
                        last = valueEnd;
                        i = valueEnd;
                    }
                }
                continue;
            }
            i++;
        }
        Normal(ref last, last, len, sink);
        return true;
    }
}
