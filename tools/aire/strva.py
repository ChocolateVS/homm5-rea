"""Prints VA and text of NUL-terminated strings in H5_Game.exe matching a regex.
Usage: python strva.py <regex>"""
import re, struct, sys
d = open(r"G:\HoMM 5 - Tribes of the East\bin\H5_Game.exe", 'rb').read()
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]
optsz = struct.unpack_from('<H', d, pe + 20)[0]
base = struct.unpack_from('<I', d, pe + 24 + 28)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + optsz + i * 40
    va, vsz = struct.unpack_from('<II', d, o + 12)[0], struct.unpack_from('<I', d, o + 8)[0]
    rsz, raw = struct.unpack_from("<II", d, o + 16)
    secs.append((va + base, raw, rsz))
def va(off):
    for sva, raw, rsz in secs:
        if raw <= off < raw + rsz: return sva + off - raw
pat = re.compile(sys.argv[1].encode())
for m in re.finditer(rb'[\x09\x0a\x0d\x20-\x7e]{6,}\x00', d):
    if pat.search(m.group()):
        print(hex(va(m.start())), repr(m.group()[:-1].decode()))
