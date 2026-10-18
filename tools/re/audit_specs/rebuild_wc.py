"""Rebuild working-copy files as HEAD + other agents' hunks (those without our markers) + our final patches.

Usage: rebuild_wc.py <path> <patch.py[:arg]> [...]. Our hunks are recognised by marker strings unique to our
patches; diffs use wide context so each of our edits stays inside one hunk."""
import subprocess, sys, re, os, tempfile

MARKERS = ['008a4330 passes each effect', 'exe 0085f0f0', 'GatingPortion', 'exe FUN_0085fe60', 'NecromancersDebuff',
           'exe 008a6c40', 'exe 008a8140', 'DeathScreamMoralePenalty', 'exe 00989fd0 case 0x3b', 'LionsDemoralized',
           'LionsMark', 'exe 008cf7ff', 'exe FUN_00bc1eb0', 'exe (e.g. Blooddrinker', 'exe 009844e0', 'PoisonerChance',
           'exe 00bbebd0', 'SacredHammerChance', 'MindCleaner', 'BombardierPower', 'Caster(haste)', 'Caster(scream)',
           'moralePenalty', 'basePortion', 'Mastery.Expert, power']
path = sys.argv[1]
os.chdir('G:/H5Reborn')
diff = subprocess.run(['git', 'diff', '-U8', 'HEAD', '--', path], capture_output=True, text=True, encoding='utf-8').stdout
head = subprocess.run(['git', 'show', f'HEAD:{path}'], capture_output=True, text=True, encoding='utf-8').stdout
lines = head.replace('\r\n', '\n').split('\n')
hunks = re.split(r'(?m)^(?=@@ )', diff)
keep = []
for h in hunks[1:]:
    changed = '\n'.join(l[1:] for l in h.split('\n')[1:] if l[:1] in ('+', '-'))
    if any(m in changed for m in MARKERS): continue
    keep.append(h)
for h in reversed(keep):
    body = h.rstrip('\n').split('\n')
    m = re.match(r'@@ -(\d+)(?:,(\d+))? \+\d+(?:,\d+)? @@', body[0])
    a = int(m.group(1))
    old = [l[1:] for l in body[1:] if l[:1] in (' ', '-')]
    new = [l[1:] for l in body[1:] if l[:1] in (' ', '+')]
    start = a - 1
    if lines[start:start + len(old)] != old: sys.exit(f'{path}: foreign hunk mismatch at {a}')
    lines[start:start + len(old)] = new
text = '\n'.join(lines)
crlf = '\r\n' in open(path, encoding='utf-8', newline='').read()
tmp = tempfile.mktemp(suffix='.cs')
open(tmp, 'w', encoding='utf-8', newline='').write(text)
for spec in sys.argv[2:]:
    script, *arg = spec.split(':')
    r = subprocess.run([sys.executable, '-I', f'tools/re/audit_specs/{script}', tmp, *arg], capture_output=True, text=True)
    if r.returncode: sys.exit(f'{path} {script}: {r.stdout}{r.stderr}')
t = open(tmp, encoding='utf-8').read(); os.remove(tmp)
if crlf: t = t.replace('\n', '\r\n')
open(path, 'w', encoding='utf-8', newline='').write(t)
print(path, 'kept', len(keep), 'foreign hunks')
