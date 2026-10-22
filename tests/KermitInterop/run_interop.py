#!/usr/bin/env python3
"""Interoperability check of SmarTTY's Kermit code against real Kermit programs.

A real Kermit (C-Kermit `kermit`, G-Kermit `gkermit`) runs on a pty - as on the serial console of a device - and the pty is
bridged to a TCP socket, which tests/KermitInterop connects to. Files are compared byte for byte afterwards.

    dotnet build tests/KermitInterop -c Release
    python3 tests/KermitInterop/run_interop.py

Needs the `kermit` (package ckermit) and/or `gkermit` programs; cases for a missing one are skipped.
"""
import filecmp, os, pty, select, shutil, socket, subprocess, sys, tempfile, time

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


def run_case(title, kermit_argv, our_args, check):
    """kermit_argv: the real program (run on a pty); our_args: arguments for the C# client after host and port."""
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    pid, master = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "dumb"
        os.execvp(kermit_argv[0], kermit_argv)
    client = subprocess.Popen(["dotnet", DLL, our_args[0], "127.0.0.1", str(port)] + our_args[1:],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    listener.settimeout(20)
    conn, _ = listener.accept()
    deadline = time.time() + 120
    conn.setblocking(False)
    while time.time() < deadline and client.poll() is None:
        r, _, _ = select.select([master, conn], [], [], 0.2)
        if master in r:
            try:
                data = os.read(master, 65536)
            except OSError:
                data = b""
            if data:
                conn.sendall(data)
        if conn in r:
            try:
                data = conn.recv(65536)
            except BlockingIOError:
                data = b""
            if data:
                os.write(master, data)
    out = client.communicate(timeout=30)[0] if client.poll() is None else client.stdout.read()
    if client.poll() is None:
        client.kill()
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
    print(("PASS: " if ok else "FAIL: ") + title)
    if not ok:
        print(out)
    return ok


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
        if not run_case("send to C-Kermit (its own file type settings: the sender must say the files are binary)", cmd, ["send"] + sources, lambda d=dest: same(d, names)):
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
            if not run_case("send to C-Kermit (" + label + ")", cmd, ["send"] + sources, lambda d=dest: same(d, names)):
                failures += 1

        for label, settings in [("defaults", ""), ("block check 1", "set block-check 1,"), ("short packets (94)", "set send packet-length 94,"),
                                ("prefix all (7-bit line)", "set prefixing all,set parity space,")]:
            dest = os.path.join(work, "from-ckermit-" + label.replace(" ", "_"))
            cmd = [ckermit, "-Y", "-C", settings + "set transfer display none,set transfer mode manual,set file type binary,cd " + src + ",send *,exit"]
            if not run_case("receive from C-Kermit (" + label + ")", cmd, ["receive", dest], lambda d=dest: same(d, names)):
                failures += 1

    if gkermit:
        dest = os.path.join(work, "to-gkermit")
        os.makedirs(dest)
        cmd = ["sh", "-c", "cd " + dest + " && exec " + gkermit + " -r"]
        if not run_case("send to G-Kermit", cmd, ["send"] + sources, lambda: same(dest, names)):
            failures += 1
        dest = os.path.join(work, "from-gkermit")
        cmd = [gkermit, "-s"] + sources
        if not run_case("receive from G-Kermit", cmd, ["receive", dest], lambda: same(dest, names)):
            failures += 1

    if not ckermit and not gkermit:
        print("neither kermit nor gkermit is installed")
        return 2
    shutil.rmtree(work, ignore_errors=True)
    print("\n%d case(s) failed" % failures if failures else "\nAll interoperability cases passed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
