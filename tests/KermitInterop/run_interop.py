#!/usr/bin/env python3
"""Interoperability check of SmarTTY's Kermit code against real Kermit programs.

A real Kermit (C-Kermit `kermit`, G-Kermit `gkermit`) runs on a pty - as on the serial console of a device - and the pty is
bridged to a TCP socket, which tests/KermitInterop connects to. Files are compared byte for byte afterwards.

    dotnet build tests/KermitInterop -c Release
    python3 tests/KermitInterop/run_interop.py

Needs the `kermit` (package ckermit) and/or `gkermit` programs; cases for a missing one are skipped.
"""
import filecmp, os, pty, random, select, shutil, socket, subprocess, sys, tempfile, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net8.0", "KermitInterop.dll")


def make_files(directory):
    os.makedirs(directory, exist_ok=True)
    import random
    rnd = random.Random(5)
    files = {
        "random.bin": bytes(rnd.randrange(256) for _ in range(150000)),
        "allbytes.bin": bytes(range(256)) * 40 + b"\x00" * 3000 + b"#&~" * 500 + b"\xff" * 500,
        "text.txt": b"Kermit test line with spaces and # & ~ characters\r\n" * 800,
        "small.dat": b"x",
    }
    paths = []
    for name, data in files.items():
        path = os.path.join(directory, name)
        with open(path, "wb") as f:
            f.write(data)
        paths.append(path)
    return paths


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


def run_case(title, kermit_argv, our_args, check, noise_every=0, env=None):
    """kermit_argv: the real program (run on a pty); our_args: arguments for the C# client after host and port."""
    started = time.time()
    to_us = Noise(noise_every, 1)
    to_program = Noise(noise_every, 2)
    child_env = dict(os.environ)
    child_env.update(env or {})
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    pid, master = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "dumb"
        os.execvp(kermit_argv[0], kermit_argv)
    client = subprocess.Popen(["dotnet", DLL, our_args[0], "127.0.0.1", str(port)] + our_args[1:],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=child_env)
    # The client's output is collected all the time: a full pipe would stop it in the middle of a transfer.
    lines = []
    threading.Thread(target=lambda: lines.extend(client.stdout), daemon=True).start()
    listener.settimeout(20)
    conn, _ = listener.accept()
    deadline = time.time() + 240
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
    try:
        os.kill(pid, 9)
    except OSError:
        pass
    try:
        os.waitpid(pid, 0)
    except OSError:
        pass
    conn.close()
    listener.close()
    ok = client.returncode == 0 and check()
    extra = " [%d bytes damaged]" % (to_us.damaged + to_program.damaged) if noise_every else ""
    print(("PASS: " if ok else "FAIL: ") + title + " (%.1f s)" % (time.time() - started) + extra)
    sys.stdout.flush()
    if not ok:
        print(out)
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
        print("build first: dotnet build tests/KermitInterop -c Release")
        return 2
    work = tempfile.mkdtemp(prefix="kermit-interop-")
    src = os.path.join(work, "src")
    sources = make_files(src)
    failures = 0
    ckermit = shutil.which("kermit")
    gkermit = shutil.which("gkermit")

    def same(dest, names):
        # G-Kermit sends upper-case names, so look the files up ignoring case.
        present = {n.lower(): n for n in os.listdir(dest)} if os.path.isdir(dest) else {}
        return all(n.lower() in present and filecmp.cmp(os.path.join(src, n), os.path.join(dest, present[n.lower()]), shallow=False) for n in names)

    names = [os.path.basename(p) for p in sources]

    if ckermit:
        dest = os.path.join(work, "to-ckermit-automatic")
        os.makedirs(dest)
        cmd = [ckermit, "-Y", "-C", "set transfer display none,cd " + dest + ",receive,exit"]
        if not run_case_retry(dest, "send to C-Kermit (its own file type settings: the sender must say the files are binary)", cmd, ["send"] + sources, lambda d=dest: same(d, names)):
            failures += 1

        for label, settings in [
            ("defaults", ""),
            ("block check 1", "set block-check 1,"),
            ("block check 2", "set block-check 2,"),
            ("short packets (94)", "set receive packet-length 94,set send packet-length 94,"),
            ("prefix all (7-bit line)", "set prefixing all,set parity space,"),
            ("no repeat compression", "set repeat prefix 0,"),
        ]:
            dest = os.path.join(work, "to-ckermit-" + label.replace(" ", "_"))
            os.makedirs(dest)
            cmd = [ckermit, "-Y", "-C", settings + "set transfer display none,set file patterns off,set file type binary,cd " + dest + ",receive,exit"]
            if "7-bit" in label:
                # A 7-bit line: C-Kermit asks for 8-bit prefixing, which our side has to accept.
                pass
            if not run_case_retry(dest, "send to C-Kermit (" + label + ")", cmd, ["send"] + sources, lambda d=dest: same(d, names)):
                failures += 1

        for label, settings, noise in [
            ("sliding window 31", "set window 31,", 0),
            ("sliding window 4, short packets", "set window 4,set receive packet-length 90,", 0),
            ("sliding window 31, noisy line", "set window 31,", 2000),
            ("one packet at a time, noisy line", "set window 1,", 2000),
        ]:
            dest = os.path.join(work, "to-ckermit-" + label.replace(" ", "_").replace(",", ""))
            os.makedirs(dest)
            cmd = [ckermit, "-Y", "-C", settings + "set transfer display none,set file patterns off,set file type binary,cd " + dest + ",receive,exit"]
            if not run_case_retry(dest, "send to C-Kermit (" + label + ")", cmd, ["send"] + sources, lambda d=dest: same(d, names), noise):
                failures += 1

        for label, settings, noise in [
            ("sliding window 31", "set window 31,", 0),
            ("sliding window 8, long packets", "set window 8,set send packet-length 2000,", 0),
            ("sliding window 31, noisy line", "set window 31,", 2000),
            ("one packet at a time, noisy line", "set window 1,", 2000),
        ]:
            dest = os.path.join(work, "from-ckermit-" + label.replace(" ", "_").replace(",", ""))
            cmd = [ckermit, "-Y", "-C", settings + "set transfer display none,set transfer mode manual,set file type binary,cd " + src + ",send *,exit"]
            if not run_case_retry(dest, "receive from C-Kermit (" + label + ")", cmd, ["receive", dest], lambda d=dest: same(d, names), noise):
                failures += 1

        for label, settings in [("defaults", ""), ("block check 1", "set block-check 1,"), ("short packets (94)", "set send packet-length 94,"),
                                ("prefix all (7-bit line)", "set prefixing all,set parity space,")]:
            dest = os.path.join(work, "from-ckermit-" + label.replace(" ", "_"))
            cmd = [ckermit, "-Y", "-C", settings + "set transfer display none,set transfer mode manual,set file type binary,cd " + src + ",send *,exit"]
            if not run_case_retry(dest, "receive from C-Kermit (" + label + ")", cmd, ["receive", dest], lambda d=dest: same(d, names)):
                failures += 1

    if gkermit:
        dest = os.path.join(work, "to-gkermit")
        os.makedirs(dest)
        cmd = ["sh", "-c", "cd " + dest + " && exec " + gkermit + " -r"]
        if not run_case_retry(dest, "send to G-Kermit", cmd, ["send"] + sources, lambda: same(dest, names)):
            failures += 1
        dest = os.path.join(work, "from-gkermit")
        cmd = [gkermit, "-s"] + sources
        if not run_case_retry(dest, "receive from G-Kermit", cmd, ["receive", dest], lambda: same(dest, names)):
            failures += 1

    if not ckermit and not gkermit:
        print("neither kermit nor gkermit is installed")
        return 2
    shutil.rmtree(work, ignore_errors=True)
    print("\n%d case(s) failed" % failures if failures else "\nAll interoperability cases passed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
