#!/usr/bin/env bash
# Provisions a throwaway OpenSSH server for tests/SshNetIntegrationTests.
#
# The script creates a dedicated unprivileged user, a host key and a client key pair
# under a temporary directory, starts sshd on a loopback port and prints the connection
# details as environment variables. It is used by CI and can be run locally:
#
#   source <(tests/SshNetIntegrationTests/setup-sshd.sh)
#   dotnet run --project tests/SshNetIntegrationTests
#
# It needs root (directly or through sudo) because it creates a user and starts sshd.

set -euo pipefail

port="${SMARTTY_SSH_TEST_PORT:-2222}"
# A second port of the same server with X11 forwarding disabled, for the refusal checks of X11ForwardingTests
# (2223 is the default port of the Dropbear server from setup-dropbear.sh).
no_x11_port="${SMARTTY_SSH_TEST_NO_X11_PORT:-2224}"
user="${SMARTTY_SSH_TEST_USER:-smartty-test}"
password="${SMARTTY_SSH_TEST_PASSWORD:-smartty-test-password}"
key_passphrase="${SMARTTY_SSH_TEST_KEY_PASSPHRASE:-smartty-key-passphrase}"
# Always a fresh directory: reusing one would make ssh-keygen prompt to overwrite.
directory="$(mktemp -d)"

as_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        sudo -n "$@"
    fi
}

# sshd lives in /usr/sbin, which is not always on PATH for a non-root user.
sshd_binary="$(command -v sshd || true)"
if [ -z "$sshd_binary" ] && [ -x /usr/sbin/sshd ]; then
    sshd_binary=/usr/sbin/sshd
fi
if [ -z "$sshd_binary" ]; then
    echo "sshd was not found; install openssh-server first." >&2
    exit 1
fi

# The user may already exist from an earlier run.
if ! id "$user" >/dev/null 2>&1; then
    as_root useradd --create-home --shell /bin/bash "$user"
fi
echo "$user:$password" | as_root chpasswd

host_key="$directory/ssh_host_ed25519_key"
as_root ssh-keygen -q -N "" -t ed25519 -f "$host_key"

client_key="$directory/id_ed25519"
ssh-keygen -q -N "" -t ed25519 -f "$client_key"
encrypted_key="$directory/id_ed25519_encrypted"
ssh-keygen -q -N "$key_passphrase" -t ed25519 -f "$encrypted_key"

home="/home/$user"
as_root install -d -m 700 -o "$user" -g "$user" "$home/.ssh"
cat "$client_key.pub" "$encrypted_key.pub" > "$directory/authorized_keys"
as_root install -m 600 -o "$user" -g "$user" "$directory/authorized_keys" "$home/.ssh/authorized_keys"

cat > "$directory/sshd_config" <<CONF
Port $port
Port $no_x11_port
ListenAddress 127.0.0.1
HostKey $host_key
PidFile $directory/sshd.pid
PasswordAuthentication yes
PubkeyAuthentication yes
KbdInteractiveAuthentication yes
UsePAM yes
PermitRootLogin no
AllowUsers $user
# Tools > Port forwarding (ssh -L / -R) is checked on the main port; the second port refuses it.
AllowTcpForwarding yes
# X11 forwarding also needs xauth on the server; without it sshd refuses x11-req and the X11 checks are skipped.
X11Forwarding yes
X11UseLocalhost yes
StrictModes no
LogLevel VERBOSE
Subsystem sftp internal-sftp
Match LocalPort $no_x11_port
    X11Forwarding no
    AllowTcpForwarding no
CONF

# sshd needs its privilege separation directory even for a config test; it can be missing on a fresh runner.
as_root mkdir -p /run/sshd
as_root "$sshd_binary" -t -f "$directory/sshd_config"
as_root "$sshd_binary" -f "$directory/sshd_config"

# sshd needs a moment before it accepts connections.
started=false
for _ in $(seq 1 100); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
        exec 3<&- 3>&-
        started=true
        break
    fi
    sleep 0.1
done
if [ "$started" != true ]; then
    echo "sshd did not start listening on port $port." >&2
    exit 1
fi

sshd_pid="$(cat "$directory/sshd.pid" 2>/dev/null || true)"

# Printed as plain NAME=value lines: that is the format GITHUB_ENV expects, and a caller can
# also read them with "set -a; eval \"$(...)\"; set +a".
cat <<ENV
SMARTTY_SSH_TEST_HOST=127.0.0.1
SMARTTY_SSH_TEST_PORT=$port
SMARTTY_SSH_TEST_NO_X11_PORT=$no_x11_port
SMARTTY_SSH_TEST_USER=$user
SMARTTY_SSH_TEST_PASSWORD=$password
SMARTTY_SSH_TEST_KEY=$client_key
SMARTTY_SSH_TEST_ENCRYPTED_KEY=$encrypted_key
SMARTTY_SSH_TEST_KEY_PASSPHRASE=$key_passphrase
SMARTTY_SSH_TEST_HOST_KEY_TYPE=ssh-ed25519
SMARTTY_SSH_TEST_DIR=$directory
ENV
