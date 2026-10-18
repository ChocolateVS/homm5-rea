"""Reads typed values at virtual addresses from H5_Game.exe (read-only).
Usage: python peread.py <f|d|i|s> <hexVA> [hexVA...]"""
import struct, sys
EXE = r"G:\HoMM 5 - Tribes of the East\bin\H5_Game.exe"
d = open(EXE, 'rb').read()
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]
optsz = struct.unpack_from('<H', d, pe + 20)[0]
base = struct.unpack_from('<I', d, pe + 24 + 28)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + optsz + i * 40
    va, vsz, raw, rsz = struct.unpack_from('<IIII', d, o + 8)[1], *struct.unpack_from('<I', d, o + 8), *struct.unpack_from('<II', d, o + 16)
    secs.append((struct.unpack_from('<I', d, o + 12)[0] + base, struct.unpack_from('<I', d, o + 8)[0], struct.unpack_from('<I', d, o + 20)[0]))
def off(va):
    for sva, vsz, raw in secs:
        if sva <= va < sva + vsz: return raw + va - sva
    raise ValueError(hex(va))
kind = sys.argv[1]
for a in sys.argv[2:]:
    va = int(a, 16); o = off(va)
    if kind == 'f': v = struct.unpack_from('<f', d, o)[0]
    elif kind == 'd': v = struct.unpack_from('<d', d, o)[0]
    elif kind == 'i': v = struct.unpack_from('<i', d, o)[0]
    else: v = d[o:d.index(b'\0', o)].decode('latin1')
    print(a, v)
