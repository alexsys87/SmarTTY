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

    // -------------------------------------------------------------------- logs

    private static readonly HashSet<string> LogLevelWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "FATAL", "CRITICAL", "CRIT", "SEVERE", "EMERG", "EMERGENCY", "ALERT", "ERROR", "ERR", "WARNING", "WARN", "NOTICE", "INFO", "DEBUG", "TRACE", "VERBOSE"
    };

    // ------------------------------------------------------------ tokenizers

    private static readonly TokenCache.LineTokenizer SystemdTokenizer =
        (int startState, string text, TokenCache.ITokenSink sink, out int endState) => TokenizeConfigCore(SystemdDirectives, startState, text, sink, out endState);

    private static readonly TokenCache.LineTokenizer NginxTokenizer =
        (int startState, string text, TokenCache.ITokenSink sink, out int endState) => TokenizeConfigCore(NginxDirectives, startState, text, sink, out endState);

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
