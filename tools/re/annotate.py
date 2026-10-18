"""Annotates Ghidra decompiler output for H5_Game.exe with enum and RPGStats field names.

Usage: python annotate.py <in.c> [out.c]
Replaces virtual calls with known meanings (HasAbility, GetEffect, HasPerk...) and
offsets on the RPGStats singleton (FUN_00bcb3f0()) with field paths from out/rpg_*.txt.
"""
import re, sys, os, glob
here = os.path.dirname(os.path.abspath(__file__))

def load_enum(name):
    m = {}
    for line in open(os.path.join(here, 'out', f'enum_{name}.txt')):
        p = line.split()
        if len(p) >= 3 and p[0].isdigit(): m[int(p[0])] = p[2]
    return m

AB, SP, SK, SPEC = load_enum('ability'), load_enum('spell'), load_enum('skill'), load_enum('spec')
STATS = {}
for f in glob.glob(os.path.join(here, 'out', 'rpg_*.txt')):
    for line in open(f):
        p = line.split()
        if len(p) >= 3 and p[0].startswith('0x'): STATS[int(p[0], 16)] = p[2]

# vtable slot -> (label, enum table) ; extended as slots are identified
VCALLS = {
    0x28c: ('HasAbility', AB),
    0x28:  ('GetSpellEffect', SP),
    0x290: ('HeroHasSkill', SK),
    0x294: ('HeroHasSpec', SPEC),
}

def ann(src):
    def vcall(m):
        slot, arg = int(m.group(2), 16), int(m.group(3), 16)
        if slot in VCALLS:
            label, table = VCALLS[slot]
            return f'{m.group(0)} /*{label}({table.get(arg, hex(arg))})*/'
        return m.group(0)
    src = re.sub(r'\(\*\*\(code \*\*\)\((.*?) \+ (0x[0-9a-f]+)\)\)\((0x[0-9a-f]+|\d+)\)', vcall, src)
    def stat(m):
        off = int(m.group(1), 16)
        return f'{m.group(0)} /*{STATS[off]}*/' if off in STATS else m.group(0)
    src = re.sub(r'\*\((?:float|int|undefined4|char|byte) \*\)\(\w+ \+ (0x[0-9a-f]{3})\)', stat, src)
    return src

if __name__ == '__main__':
    s = ann(open(sys.argv[1]).read())
    if len(sys.argv) > 2: open(sys.argv[2], 'w').write(s)
    else: print(s)
