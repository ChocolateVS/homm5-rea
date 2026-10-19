"""For a VA inside a vtable, find the vtable start, its RTTI class and slot index. Usage: vtof.py hexVA..."""
import sys, struct
src = open(r'G:\H5Reborn\tools\re\peread.py').read().split('kind = sys.argv[1]')[0]
g = {}; exec(src, g); d = g['d']; off = g['off']
def u32(va): return struct.unpack_from('<I', d, off(va))[0]
def name_of_col(c):
    try:
        if u32(c) != 0: return None
        td = u32(c + 12); s = d[off(td + 8):off(td + 8) + 200]
        s = s[:s.index(b'\0')].decode('latin1')
        return s if s.startswith('.?A') else None
    except Exception: return None
for a in sys.argv[1:]:
    va = int(a, 16); p = va
    while p > va - 0x1000:
        n = name_of_col(u32(p - 4)) if True else None
        try: n = name_of_col(u32(p - 4))
        except Exception: n = None
        if n: print(a, 'vtable', hex(p), n, 'slot', (va - p) // 4, 'off', hex(va - p)); break
        p -= 4
