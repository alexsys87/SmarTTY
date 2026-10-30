# SSH.NET integration tests

These checks run the production SSH layer (`Ssh/`) against a real OpenSSH server. They exist
because `SmarTTY.csproj` depends on `SSH.NET`, and a large part of that library's API is
internal: `Ssh/SshNetChannel.cs` reaches it through reflection, so a version update breaks at
runtime instead of at compile time.

Run them whenever `SSH.NET` is updated.

## What is checked

Two groups run from the same command.

The API contract checks always run and need no server. They cover the exact SSH.NET surface the
SSH layer relies on: the internal types, methods, events, constructors and enum values behind
the reflection lookups in `Ssh/SshNetChannel.cs`, plus everything `SshSession` and `RsaSshKey`
use directly. A rename or removal in SSH.NET fails here with a readable message instead of as a
`MissingMemberException` in the running application.

The live-server checks need the server described below. They drive the real protocol:
connect and key exchange, the host key callback, password, keyboard-interactive and public key
authentication (including passphrase handling), exec channels with and without a PTY, the
separate stderr stream, exit statuses and signals, shell channels, PTY resize, EOF, polling
delivery, the extended data modes, the SCP upload/download exchange and connection loss.

X11 forwarding (`Ssh/X11Forwarding.cs`) has a group of its own. The parsing of the X11 connection setup with
the fake cookie always runs. The live part needs `xauth` and `python3` on the server (CI installs them): a remote
Python client connects to the forwarded display through the real OpenSSH server, and its connection is relayed to an
in-process fake X server, with more data in both directions than either side's channel window. A wrong cookie must
be rejected, and a second port of the same server (`SMARTTY_SSH_TEST_NO_X11_PORT`, X11 forwarding disabled with
`Match LocalPort`) checks the refusal and the remote X11 check.

Two more groups cover Dropbear, the SSH server of OpenWRT. `PtySetupGateTests` always run: they check
`Ssh/PtySetupGate.cs`, which keeps helper commands without a PTY from overlapping with setting up a PTY
channel (Dropbear silently drops the PTY channel's request when such a helper exits meanwhile, which hung
Smart Terminal commands on OpenWRT). `DropbearTests` run PTY commands together with helper commands against
a Dropbear server with a BusyBox ash login shell; they need the server from `setup-dropbear.sh`.

`tests/SmartTerminalIntegrationTests` uses the same two servers to run the real Smart Terminal control (a
WinForms program built for .NET Framework 4.8) under Mono with a virtual display: commands, `cd`, `export`, exit
codes, the Ctrl+R history search and Tab completion of variables, hosts, aliases and functions. After the servers are
up:

```sh
dotnet build tests/SmartTerminalIntegrationTests -c Release
xvfb-run -a mono tests/SmartTerminalIntegrationTests/bin/Release/net48/SmartTerminalIntegrationTests.exe
```

Windows hosts (`Ssh/SftpFileSystem.cs`, `Ssh/WindowsSftpPath.cs`, `Ssh/WindowsAuthorizedKeys.cs`) have two groups. The checks of
the SFTP form of Windows paths, of the key file that sshd of Windows uses (read from `whoami /groups` and `sshd_config`) and of
the `icacls` command always run. The live ones run the operations behind the file list of a Windows host (listing a directory of
1500 files in many READDIR requests, a recursive delete that must not follow links, a copy of a 3 MB file on one channel, Cyrillic
names) and the installation and removal of a key in the key files (CRLF kept, nothing added twice, the administrators' file) over
SFTP against the Unix test server: the protocol is the same one Win32-OpenSSH speaks. What only a Windows host can answer
(`whoami`, `icacls`) is a callback in these tests, and a directory of the test stands for `%ProgramData%`.

## Running

The suite needs the .NET 8 SDK and an OpenSSH server. `setup-sshd.sh` provisions a throwaway
one: a dedicated unprivileged user, a host key and a client key pair under a temporary
directory, and `sshd` on a loopback port. It needs root, directly or through `sudo`.

```sh
set -a; eval "$(tests/SshNetIntegrationTests/setup-sshd.sh)"; set +a
dotnet run --project tests/SshNetIntegrationTests
```

For the Dropbear checks also install `dropbear-bin` and `busybox` and run
`set -a; eval "$(tests/SshNetIntegrationTests/setup-dropbear.sh)"; set +a` (CI sets
`SMARTTY_DROPBEAR_TEST_REQUIRED=1` so that a missing server fails instead of skipping these checks).

The script prints the connection details as `NAME=value` lines; the test reads them from the
environment. Without them the live-server checks are skipped with a notice and only the API
contract is verified. Set `SMARTTY_SSH_TEST_REQUIRED=1` to turn that skip into a failure, which
is what CI does. In a workflow the output can go straight to `$GITHUB_ENV`.

CI runs the suite in the `sshnet` job of `.github/workflows/build.yml`.

## Notes

The test project links the production sources directly, as `tests/CommandCompletionDatabaseTests`
does, and takes the `SSH.NET` version from `SmarTTY.csproj`, so it always tests the same library
the application ships with. A failed check exits unsuccessfully; a watchdog aborts the run if a
connection hangs.

These checks need Linux and an OpenSSH server. They are not a Windows/WPF application build or a
UI test, and `RsaSshKey` (the Windows CSP key store) is compiled but not exercised.

`SmarTTYKeyTests` cover the Ed25519 and ECDSA keys of SmarTTY (`Ssh/OpenSshKeyPair.cs`) with an in-memory key
store instead of the DPAPI file of the application: generation, Reset, export and import (keys made by `ssh-keygen` in
the OpenSSH, PEM and PKCS#8 formats, and a PuTTY `.ppk` when `puttygen` is installed), and, against the live server,
installing each key type in `authorized_keys` and logging in with it. A PuTTY key with a passphrase is also used to log
in through the key file path when `puttygen` is installed.

`ProxyTests` connect to the live server through a small proxy server inside the test: SOCKS5 (as the inbound of xray
or v2ray), SOCKS4/4a and HTTPS (HTTP CONNECT over TLS with a self-signed certificate), with and without credentials.
The server name must reach the proxy as a name, a refused or missing proxy login must be recognized so that SmarTTY
asks for the proxy credentials, an untrusted HTTPS proxy certificate must stop the connection, and bulk data must pass
`Ssh/ProxyRelay.cs`, which carries the SOCKS4 and HTTPS connections for SSH.NET.

`PortForwardTests` cover Tools > Port forwarding: a local forwarding (ssh -L) on a port chosen by the system, checked
by logging in to the server again through it with bulk data; a remote forwarding (ssh -R) that a bash on the server uses
to reach a listener of the test; the refusal of the second port (AllowTcpForwarding no); and the list of a session in
`Ssh/PortForwardHost.cs`: "start on connect", start and stop, restore after a reconnect, editing and a taken port.
