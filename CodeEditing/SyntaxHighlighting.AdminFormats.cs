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
