"""Find probable function starts (after 0xCC/0x90 padding) preceding each VA. Usage: fstart.py hexVA..."""
import sys, importlib.util
sys.argv = [sys.argv[0], 'i', '401000'] + sys.argv[1:]
spec = importlib.util.spec_from_file_location('pe', r'G:\H5Reborn\tools\re\peread.py')
src = open(r'G:\H5Reborn\tools\re\peread.py').read().split('kind = sys.argv[1]')[0]
g = {}; exec(src, g)
for a in sys.argv[3:]:
    va = int(a, 16); o = g['off'](va); d = g['d']
    res = []
    p = o
    while len(res) < 3 and p > o - 0x4000:
        if d[p-1] in (0xCC,) and d[p] != 0xCC and (va - (o - p)) % 16 == 0:
            res.append(hex(va - (o - p)))
        p -= 1
    print(a, res)
