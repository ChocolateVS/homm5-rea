"""Cross-references every combat-relevant ID with (a) H5_Game.exe call sites that test it and
(b) references in our engine. Output: tools/re/audit/out/inventory.tsv (one row per ID)."""
import re, os, glob, collections, sys
ROOT = 'G:/H5Reborn'
OUT = ROOT + '/tools/re/audit/out'

def enum(name):
    m = {}
    for line in open(f'{ROOT}/tools/re/out/enum_{name}.txt'):
        p = line.split()
        if len(p) == 3 and p[1].startswith('0x'): m[int(p[1], 16)] = p[2]
    return m

def calls(slot):
    d = collections.defaultdict(set)
    for line in open(f'{OUT}/vc_{slot}.txt'):
        p = line.split()
        if len(p) >= 3: d[int(p[0], 16)].add(p[2].split('@')[-1] if '@' in p[2] else p[1])
    return d

def is_combat_fn(addr):
    a = int(addr, 16)
    # Battle code lives roughly in 0x008a0000-0x008e0000, 0x00960000-0x009a0000 (spells), 0x00a20000-0x00a80000, 0x00b30000-0x00b40000, 0x00bf0000-0x00c00000
    return any(lo <= a < hi for lo, hi in [(0x0089_0000, 0x008f_0000), (0x0096_0000, 0x009b_0000), (0x00a2_0000, 0x00a8_0000), (0x00b3_0000, 0x00b5_0000), (0x00bf_0000, 0x00c0_0000)])

src = ''
for f in glob.glob(f'{ROOT}/src/H5.Combat/**/*.cs', recursive=True): src += open(f, encoding='utf-8').read()
data_src = ''
for f in glob.glob(f'{ROOT}/src/H5.Core/Data/**/*.cs', recursive=True): data_src += open(f, encoding='utf-8').read()

# Constants in Ids.cs map short names to IDs (e.g. Ab.DoubleShot = "ABILITY_DOUBLE_SHOT"): count those uses too.
const = {}
for m in re.finditer(r'(\w+)\s*=\s*"((?:ABILITY|SPELL|HERO_SKILL|HERO_SPEC|ARTIFACT)_[A-Z0-9_]+)"', src):
    const[m.group(2)] = m.group(1)

def our_refs(ident):
    n = src.count('"' + ident + '"')
    c = const.get(ident)
    if c: n += len(re.findall(r'\.' + re.escape(c) + r'\b', src))
    return n

rows = []
for kind, slot, ename in [('ability', '28c', 'ability'), ('skill', '290', 'skill'), ('spec', '294', 'spec'), ('spell', '28', 'spell')]:
    e = enum(ename); c = calls(slot)
    for v, name in sorted(e.items()):
        fns = sorted(c.get(v, ()))
        cfns = [f for f in fns if is_combat_fn(f)]
        rows.append((kind, name, len(cfns), ' '.join(cfns[:8]), len(fns), our_refs(name)))
with open(f'{OUT}/inventory.tsv', 'w') as o:
    o.write('kind\tid\texe_combat_sites\texe_combat_fns\texe_all_sites\tour_refs\n')
    for r in rows: o.write('\t'.join(map(str, r)) + '\n')
# Summary of likely gaps: tested by exe combat code but never referenced by our engine.
gaps = [r for r in rows if r[2] > 0 and r[5] == 0]
print('rows', len(rows), 'gaps(exe combat uses, we never reference):', len(gaps))
for r in gaps: print(r[0], r[1], r[3])
