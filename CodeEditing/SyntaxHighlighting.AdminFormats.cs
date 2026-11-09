using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

// The formats that administrators edit and read on a server: systemd units, nginx and ssh configuration, logs. The first three are
// the "Config" colours with the directives of the program known (a known directive is a keyword, so a misspelt one stands out, and the
// editor offers them as completions); logs get the colours of their timestamps and levels.
public static partial class SyntaxHighlighting
{
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
        @"SHELL PATH MAILTO HOME LOGNAME CRON_TZ");

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
          x-systemd.required-by x-systemd.wanted-by x-systemd.before x-systemd.after
          soft hard intr nointr timeo retrans rsize wsize nolock lock
          vers proto tcp udp port mountproto mountport
          credentials guest sec iocharset codepage
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

    private static readonly HashSet<string> MySqlDirectives = Words(StringComparer.OrdinalIgnoreCase,
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
          group-concat-max-len group_concat_max_len",
        // the [groups] of the file
        @"mysqld mysqld_safe mysqldump mysql client server mariadb mariadbd mariadb-client mariadb-server
          embedded client-server client-mariadb galera mariadb-galera");

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
          rdb-save-incremental-fsync jemalloc-bg-thread activedefrag");

    private static readonly HashSet<string> RedisValues = Words(StringComparer.OrdinalIgnoreCase,
        @"yes no always everysec debug verbose notice warning
          noeviction allkeys-lru allkeys-lfu allkeys-random volatile-lru volatile-lfu volatile-random volatile-ttl
          on off nopass resetpass allkeys allchannels all
          normal slave replica master");

    /// <summary>
    /// Redis: the colours of Config with the directives of redis.conf and sentinel.conf known, and the values of the
    /// eviction policies, appendfsync and loglevel modes coloured as keywords.
    /// </summary>
    private static bool TokenizeRedis(int startState, string text, TokenCache.ITokenSink sink, out int endState)
    {
        return TokenizeConfigCore(RedisDirectives, startState, text, sink, out endState, valueKeywords: RedisValues);
    }

    // ---------------------------------------------------------------- HAProxy

    private static readonly HashSet<string> HAProxyDirectives = Words(StringComparer.OrdinalIgnoreCase,
        // sections
        @"global defaults frontend backend listen peers resolvers userlist program cache http-errors ring log-forward mailers",
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
          RouterLifetime");

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
          title trunc replace printf range with if else end define block fail lookup semverCompare");

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
