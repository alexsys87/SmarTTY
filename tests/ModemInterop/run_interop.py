#!/usr/bin/env python3
"""Interoperability check of SmarTTY's XMODEM, YMODEM and ZMODEM code against lrzsz (sx/rx, sb/rb, sz/rz).

The lrzsz program runs on a pty - as on the serial console of a device - and the pty is bridged to a TCP socket, which
tests/ModemInterop connects to. The bridge can damage bytes on the way (both directions), like a noisy serial line.
Files are compared byte for byte afterwards.

    dotnet build tests/ModemInterop -c Release
    python3 tests/ModemInterop/run_interop.py

Needs lrzsz (package lrzsz); without it every case is skipped.
"""
import filecmp, os, re, pty, random, select, shutil, socket, subprocess, sys, tempfile, threading, time, tty

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net8.0", "ModemInterop.dll")


def make_files(directory):
    os.makedirs(directory, exist_ok=True)
    rnd = random.Random(7)
    files = {
        "random.bin": bytes(rnd.randrange(256) for _ in range(200000)),
        # Every byte value, runs of the ZMODEM and XMODEM control bytes, CAN and '@' CR (ZMODEM escapes it).
        "allbytes.bin": bytes(range(256)) * 40 + b"\x18" * 700 + b"\x11\x13\x91\x93\x10\x90" * 300 + b"@\r" * 400 + b"\x1a" * 100,
        "text.txt": b"Line with CR LF\r\n" * 3000,
        "small.dat": b"x",
        "exact1k.bin": bytes(rnd.randrange(256) for _ in range(1024)),
    }
    for name, data in files.items():
        with open(os.path.join(directory, name), "wb") as f:
            f.write(data)
    return [os.path.join(directory, n) for n in files]


class Noise:
    """Damages one byte in roughly every `every` bytes that pass the bridge (0 = a clean line)."""

    def __init__(self, every, seed):
        self.every = every
        self.rnd = random.Random(seed)
        self.damaged = 0

    def apply(self, data):
        if not self.every or not data:
            return data
        out = bytearray(data)
        for i in range(len(out)):
            if self.rnd.randrange(self.every) == 0:
                out[i] ^= 1 << self.rnd.randrange(8)
                self.damaged += 1
        return bytes(out)


def run_case(title, program_argv, cwd, our_args, check, noise_every=0, timeout=240, env=None):
    """program_argv: the lrzsz program (run on a pty in `cwd`); our_args: mode and paths for the C# client."""
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    pid, master = pty.fork()
    if pid == 0:
        os.chdir(cwd)
        os.environ["TERM"] = "dumb"
        tty.setraw(0)
        os.execvp(program_argv[0], program_argv)
    child_env = dict(os.environ)
    child_env.update(env or {})
    client = subprocess.Popen(["dotnet", DLL, our_args[0], "127.0.0.1", str(port)] + our_args[1:],
                              stdout=None if os.environ.get("VERBOSE") else subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=child_env)
    # The client's output is collected all the time: a full pipe would stop it in the middle of a transfer.
    lines = []
    if client.stdout is not None:
        threading.Thread(target=lambda: lines.extend(client.stdout), daemon=True).start()
    listener.settimeout(30)
    conn, _ = listener.accept()
    to_us = Noise(noise_every, 1)
    to_program = Noise(noise_every, 2)
    started = time.time()
    deadline = started + timeout
    conn.setblocking(False)
    # Both directions are buffered and written without blocking: a program that has ended (or stopped reading) must
    # not hang the bridge, and the bridge must keep reading while a write waits.
    os.set_blocking(master, False)
    to_client = bytearray()
    to_pty = bytearray()
    program_done = False
    client_done = False  # the client closed its end; it is still given the time to exit by itself
    while time.time() < deadline and client.poll() is None:
        readers = [conn] if len(to_pty) < (1 << 20) and not client_done else []
        if not program_done and len(to_client) < (1 << 20):
            readers.append(master)
        writers = ([conn] if to_client and not client_done else []) + ([master] if to_pty and not program_done else [])
        r, w, _ = select.select(readers, writers, [], 0.2)
        if master in r:
            try:
                data = os.read(master, 65536)
            except BlockingIOError:
                data = None
            except OSError:
                data = None
                program_done = True
            if data:
                to_client += to_us.apply(data)
            elif data is not None:
                program_done = True
        if conn in r:
            try:
                data = conn.recv(65536)
            except BlockingIOError:
                data = None
            except OSError:
                data = b""
            if data:
                if not program_done:
                    to_pty += to_program.apply(data)
            elif data is not None:
                client_done = True  # the client has finished and closed the connection
        if conn in w:
            try:
                del to_client[:conn.send(bytes(to_client[:65536]))]
            except BlockingIOError:
                pass
            except OSError:
                client_done = True
        if master in w:
            try:
                del to_pty[:os.write(master, bytes(to_pty[:4096]))]
            except BlockingIOError:
                pass
            except OSError:
                program_done = True
    if client.poll() is None:
        client.kill()
    client.wait(timeout=30)
    time.sleep(0.2)
    out = "".join(lines)
    # Give the program a moment to write its file and leave.
    for _ in range(100):
        try:
            done, _ = os.waitpid(pid, os.WNOHANG)
        except ChildProcessError:
            break
        if done:
            break
        # Keep reading what it prints, or it may block on a full terminal before it has closed its files.
        try:
            while os.read(master, 65536):
                pass
        except OSError:
            pass
        time.sleep(0.1)
    else:
        try:
            os.kill(pid, 9)
            os.waitpid(pid, 0)
        except OSError:
            pass
    os.close(master)
    conn.close()
    listener.close()
    elapsed = time.time() - started
    ok = client.returncode == 0 and check()
    extra = " [%d bytes damaged]" % (to_us.damaged + to_program.damaged) if noise_every else ""
    print(("PASS: " if ok else "FAIL: ") + title + " (%.1f s)" % elapsed + extra)
    if not ok or os.environ.get("VERBOSE"):
        print(out)
    sys.stdout.flush()
    return ok



def clear(path):
    """Removes what a failed attempt left at the destination (a directory's files, or a file)."""
    if os.path.isdir(path):
        for name in os.listdir(path):
            full = os.path.join(path, name)
            if os.path.isfile(full):
                os.remove(full)
    elif os.path.isfile(path):
        os.remove(path)


def run_case_retry(dest, title, *args, **kwargs):
    """
    A case runs against a real program on a real pty, where timing is not ours to control; a failed case is run once
    more on a clean destination, and a pass on the second attempt is reported as FLAKY. A second failure is a failure.
    """
    if run_case(title, *args, **kwargs):
        return True
    clear(dest)
    print("  ... running it once more")
    if run_case(title, *args, **kwargs):
        print("FLAKY: " + title + " (passed on the second attempt)")
        return True
    return False

def main():
    if not os.path.exists(DLL):
        print("build first: dotnet build tests/ModemInterop -c Release")
        return 2
    if not shutil.which("sz"):
        print("lrzsz is not installed")
        return 2
    only = sys.argv[1] if len(sys.argv) > 1 else ""
    work = tempfile.mkdtemp(prefix="modem-interop-")
    src = os.path.join(work, "src")
    sources = make_files(src)
    names = [os.path.basename(p) for p in sources]
    failures = 0
    counter = [0]

    def fresh(label):
        counter[0] += 1
        d = os.path.join(work, "%02d-%s" % (counter[0], label))
        os.makedirs(d)
        return d

    def same_dir(dest, wanted):
        return all(os.path.exists(os.path.join(dest, n)) and filecmp.cmp(os.path.join(src, n), os.path.join(dest, n), shallow=False) for n in wanted)

    def padded_equal(original, received, block):
        # XMODEM pads the last block with SUB (0x1A): the received file is the original plus padding.
        a = open(original, "rb").read()
        b = open(received, "rb").read()
        return b[:len(a)] == a and len(b) - len(a) < block and set(b[len(a):]) <= {0x1A}

    def case(title, argv, cwd, ours, check, noise=0, env=None):
        nonlocal failures
        if only and not re.search(only, title):
            return
        dest = cwd if cwd != src else ours[-1]
        if not run_case_retry(dest, title, argv, cwd, ours, check, noise, env=env):
            failures += 1

    big = os.path.join(src, "random.bin")
    allbytes = os.path.join(src, "allbytes.bin")

    # ---------- XMODEM ----------
    for label, mode, argv_extra in [("XMODEM", "xsend", ["-c"]), ("XMODEM-1K", "x1ksend", ["-c"]), ("XMODEM checksum", "xsend", [])]:
        for noise in (0, 3000):
            d = fresh("x")
            target = os.path.join(d, "out.bin")
            case("%s: send to rx%s%s" % (label, " " + " ".join(argv_extra) if argv_extra else "", ", noisy line" if noise else ""),
                 ["rx"] + argv_extra + [target], d, [mode, allbytes], lambda t=target: padded_equal(allbytes, t, 1024), noise)
    for label, argv in [("XMODEM", ["sx", allbytes]), ("XMODEM-1K", ["sx", "-k", allbytes])]:
        for noise in (0, 3000):
            d = fresh("x")
            target = os.path.join(d, "out.bin")
            case("%s: receive from %s%s" % (label, " ".join(argv[:-1]), ", noisy line" if noise else ""), argv, d, ["xreceive", target],
                 lambda t=target: padded_equal(allbytes, t, 1024), noise)

    # ---------- YMODEM ----------
    for noise in (0, 3000):
        d = fresh("y")
        case("YMODEM: send a batch to rb" + (", noisy line" if noise else ""), ["rb"], d, ["ysend"] + sources, lambda d=d: same_dir(d, names), noise)
    for noise in (0, 3000):
        d = fresh("y")
        case("YMODEM: receive a batch from sb" + (", noisy line" if noise else ""), ["sb"] + sources, src, ["yreceive", d], lambda d=d: same_dir(d, names), noise)
    d = fresh("y")
    case("YMODEM: receive a batch from sb -k", ["sb", "-k"] + sources, src, ["yreceive", d], lambda d=d: same_dir(d, names))
    d = fresh("yg")
    case("YMODEM-G: receive a batch from sb", ["sb"] + sources, src, ["ygreceive", d], lambda d=d: same_dir(d, names))

    # ---------- ZMODEM ----------
    for title, argv, env, noise in [
        ("ZMODEM: send a batch to rz", ["rz"], None, 0),
        ("ZMODEM: send a batch to rz, escape control characters", ["rz"], {"ZMODEM_OPTIONS": "escape"}, 0),
        ("ZMODEM: send a batch to rz -e (receiver asks for escaping)", ["rz", "-e"], None, 0),
        ("ZMODEM: send a batch to rz, CRC-16", ["rz"], {"ZMODEM_OPTIONS": "crc16"}, 0),
        ("ZMODEM: send a batch to rz, 8 KB subpackets", ["rz"], {"ZMODEM_OPTIONS": "sub=8192"}, 0),
        ("ZMODEM: send a batch to rz, noisy line", ["rz"], None, 3000),
        ("ZMODEM: send a batch to rz, very noisy line", ["rz"], None, 800),
    ]:
        d = fresh("z")
        case(title, argv, d, ["zsend"] + sources, lambda d=d: same_dir(d, names), noise, env)
    for title, extra, env, noise in [
        ("ZMODEM: receive a batch from sz", [], None, 0),
        ("ZMODEM: receive a batch from sz -e (escape control characters)", ["-e"], None, 0),
        ("ZMODEM: receive a batch from sz -8 (8 KB blocks)", ["-8"], None, 0),
        ("ZMODEM: receive a batch from sz -w 4096 (window)", ["-w", "4096"], None, 0),
        ("ZMODEM: receive a batch from sz -l 512 (ZCRCW every 512 bytes)", ["-l", "512"], None, 0),
        ("ZMODEM: receive a batch from sz, CRC-16 only", [], {"ZMODEM_OPTIONS": "crc16"}, 0),
        ("ZMODEM: receive a batch from sz, we ask for escaping", [], {"ZMODEM_OPTIONS": "escape"}, 0),
        ("ZMODEM: receive a batch from sz, noisy line", [], None, 3000),
        ("ZMODEM: receive a batch from sz, very noisy line", [], None, 800),
    ]:
        d = fresh("z")
        case(title, ["sz"] + extra + sources, src, ["zreceive", d], lambda d=d: same_dir(d, names), noise, env)

    shutil.rmtree(work, ignore_errors=True)
    print("\n%d case(s) failed" % failures if failures else "\nAll interoperability cases passed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
