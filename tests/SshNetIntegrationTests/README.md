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

## Running

The suite needs the .NET 8 SDK and an OpenSSH server. `setup-sshd.sh` provisions a throwaway
one: a dedicated unprivileged user, a host key and a client key pair under a temporary
directory, and `sshd` on a loopback port. It needs root, directly or through `sudo`.

```sh
eval "$(tests/SshNetIntegrationTests/setup-sshd.sh)"
dotnet run --project tests/SshNetIntegrationTests
```

The script prints the connection details as environment variables; the test reads them from
there. Without them the live-server checks are skipped with a notice and only the API contract
is verified. Set `SMARTTY_SSH_TEST_REQUIRED=1` to turn that skip into a failure, which is what
CI does.

CI runs the suite in the `sshnet` job of `.github/workflows/build.yml`.

## Notes

The test project links the production sources directly, as `tests/CommandCompletionDatabaseTests`
does, and takes the `SSH.NET` version from `SmarTTY.csproj`, so it always tests the same library
the application ships with. A failed check exits unsuccessfully; a watchdog aborts the run if a
connection hangs.

These checks need Linux and an OpenSSH server. They are not a Windows/WPF application build or a
UI test, and `RsaSshKey` (the Windows CSP key store) is compiled but not exercised.
