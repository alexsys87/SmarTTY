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

Two more groups cover Dropbear, the SSH server of OpenWRT. `PtySetupGateTests` always run: they check
`Ssh/PtySetupGate.cs`, which keeps helper commands without a PTY from overlapping with setting up a PTY
channel (Dropbear silently drops the PTY channel's request when such a helper exits meanwhile, which hung
Smart Terminal commands on OpenWRT). `DropbearTests` run PTY commands together with helper commands against
a Dropbear server with a BusyBox ash login shell; they need the server from `setup-dropbear.sh`.

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
