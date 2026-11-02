#!/usr/bin/env python3
# A stand-in for BusyBox devmem and for devmem2, for tests/DevMemTests: the "physical memory" is the file in FAKE_MEM, starting at
# the address in FAKE_BASE. It prints exactly what the real tools print. FAKE_BUS is an address that raises a bus error (SIGBUS),
# FAKE_DENY makes /dev/mem unreadable, as for a user other than root.
import os
import signal
import sys

name = os.path.basename(sys.argv[0])
args = sys.argv[1:]
if name == "busybox":
    if args[:1] == ["--list"]:
        print("sh\ndevmem\ncat")
        sys.exit(0)
    if args[:1] != ["devmem"]:
        sys.exit(127)
    name, args = "devmem", args[1:]

path = os.environ["FAKE_MEM"]
base = int(os.environ["FAKE_BASE"], 16)
devmem2 = name == "devmem2"

if os.environ.get("FAKE_DENY"):
    if devmem2:
        print("Error at line 75, file devmem2.c (13) [Permission denied]")
    else:
        print("devmem: can't open '/dev/mem': Permission denied", file=sys.stderr)
    sys.exit(1)

if not args:
    print("Usage: devmem ADDRESS [WIDTH [VALUE]]", file=sys.stderr)
    sys.exit(1)

address = int(args[0], 0)
width_arg = args[1] if len(args) > 1 else ("w" if devmem2 else "32")
if devmem2:
    width = {"b": 8, "h": 16, "w": 32}[width_arg]
else:
    width = int(width_arg)
size = width // 8

if os.environ.get("FAKE_BUS") and int(os.environ["FAKE_BUS"], 16) == address:
    sys.stdout.flush()
    os.kill(os.getpid(), signal.SIGBUS)

with open(path, "r+b") as memory:
    memory.seek(address - base)
    old = int.from_bytes(memory.read(size), "little")
    if len(args) > 2:
        value = int(args[2], 0)
        stored = value
        if os.environ.get("FAKE_READONLY_MASK"):
            # Bits that the "hardware" keeps as they are.
            mask = int(os.environ["FAKE_READONLY_MASK"], 16)
            stored = (value & ~mask) | (old & mask)
        memory.seek(address - base)
        memory.write((stored & ((1 << width) - 1)).to_bytes(size, "little"))
        if devmem2:
            print("/dev/mem opened.")
            print("Memory mapped at address 0xb6f5e000.")
            print("Value at address 0x%X (0xb6f5e000): 0x%X" % (address, old))
            print("Written 0x%X; readback 0x%X" % (value, stored))
        sys.exit(0)

if devmem2:
    print("/dev/mem opened.")
    print("Memory mapped at address 0xb6f5e000.")
    print("Read at address  0x%08X (0xb6f5e000): 0x%0*X" % (address, size * 2, old))
else:
    print("0x%0*X" % (size * 2, old))
