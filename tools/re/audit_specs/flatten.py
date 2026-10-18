"""Flattens a ReaderChain dump into absolute RPGStats offsets.

Explicit offsets ("param_1 + 0x24") are taken as given; implicit ones (pfVar1/piVar2: the reader walks
fields in declaration order) are previous field + 4 (first field +4). Usage: flatten.py chain.txt base_hex
"""
import re, sys

lines = open(sys.argv[1], encoding='utf-8').read().splitlines()[1:]
base = int(sys.argv[2], 16)
stack = [(-1, base, base)]  # (indent, struct base, last field offset)
for ln in lines:
    m = re.match(r'(\s*)(\S+)\s+off=(.*?)\s+via', ln)
    if not m: continue
    ind = len(m.group(1)) // 2
    name, off = m.group(2), m.group(3)
    while stack and stack[-1][0] >= ind: stack.pop()
    _, sbase, last = stack[-1]
    e = re.search(r'\+ (0x[0-9a-f]+|\d+)', off)
    rel = int(e.group(1), 0) if e else None
    abs_off = sbase + rel if rel is not None else (last + 4 if last != sbase else sbase + 4)
    stack[-1] = (stack[-1][0], sbase, abs_off)
    print(f'{abs_off:#06x}  {"  " * ind}{name}')
    stack.append((ind, abs_off, abs_off))
