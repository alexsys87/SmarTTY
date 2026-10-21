#!/usr/bin/env bash
# Provisions a throwaway Dropbear server with a BusyBox ash login shell, the combination OpenWRT routers use,
# for the Dropbear checks of tests/SshNetIntegrationTests (DropbearTests.cs):
#
#   set -a; eval "$(tests/SshNetIntegrationTests/setup-dropbear.sh)"; set +a
#   dotnet run --project tests/SshNetIntegrationTests
#
# It needs root (directly or through sudo) and the dropbear and busybox packages
# (Debian/Ubuntu: dropbear-bin busybox).

set -euo pipefail

port="${SMARTTY_DROPBEAR_TEST_PORT:-2223}"
user="${SMARTTY_DROPBEAR_TEST_USER:-smartty-ash}"
password="${SMARTTY_DROPBEAR_TEST_PASSWORD:-smartty-ash-password}"
directory="$(mktemp -d)"

as_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        sudo -n "$@"
    fi
}

dropbear_binary="$(command -v dropbear || true)"
if [ -z "$dropbear_binary" ] && [ -x /usr/sbin/dropbear ]; then
    dropbear_binary=/usr/sbin/dropbear
fi
busybox_binary="$(command -v busybox || true)"
if [ -z "$dropbear_binary" ] || [ -z "$busybox_binary" ]; then
    echo "dropbear or busybox was not found; install dropbear-bin and busybox first." >&2
    exit 1
fi

# The login shell is BusyBox ash, as on OpenWRT; it has to be listed in /etc/shells. BusyBox picks the applet
# from the name it is started with, so the link must be called "ash".
shell=/usr/local/lib/smartty-busybox/ash
as_root mkdir -p "$(dirname "$shell")"
as_root ln -sf "$busybox_binary" "$shell"
if ! grep -qx "$shell" /etc/shells; then
    echo "$shell" | as_root tee -a /etc/shells >/dev/null
fi
if ! id "$user" >/dev/null 2>&1; then
    as_root useradd --create-home --shell "$shell" "$user"
else
    as_root usermod --shell "$shell" "$user"
fi
echo "$user:$password" | as_root chpasswd

host_key="$directory/dropbear_ed25519_host_key"
dropbearkey_binary="$(command -v dropbearkey || echo /usr/bin/dropbearkey)"
"$dropbearkey_binary" -t ed25519 -f "$host_key" >/dev/null 2>&1
as_root chown root "$host_key"

# -E logs to stderr, -P keeps the pid file next to the key; dropbear forks into the background by itself.
# Its stdout must not stay connected to ours: a caller reading this script's output through $(...) would wait
# for the daemon to exit.
as_root "$dropbear_binary" -r "$host_key" -p "127.0.0.1:$port" -P "$directory/dropbear.pid" -E </dev/null >/dev/null 2>"$directory/dropbear.log"

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
    echo "dropbear did not start listening on port $port." >&2
    cat "$directory/dropbear.log" >&2 || true
    exit 1
fi

cat <<ENV
SMARTTY_DROPBEAR_TEST_HOST=127.0.0.1
SMARTTY_DROPBEAR_TEST_PORT=$port
SMARTTY_DROPBEAR_TEST_USER=$user
SMARTTY_DROPBEAR_TEST_PASSWORD=$password
SMARTTY_DROPBEAR_TEST_DIR=$directory
ENV
