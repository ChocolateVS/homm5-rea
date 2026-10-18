"""Builds a patch containing only selected hunks of working-tree changes (other agents edit the same files).
Usage: python mypatch.py <out.patch> <spec>...   spec = path (all hunks) | path:1,3 (1-based hunk numbers, git diff -U3 order)
"""
import subprocess, sys

ROOT = 'G:/H5Reborn'
out = sys.argv[1]
chunks = []
for spec in sys.argv[2:]:
    path, _, sel = spec.partition(':')
    keep = {int(x) for x in sel.split(',')} if sel else None
    diff = subprocess.run(['git', 'diff', '--no-color', '--', path], cwd=ROOT, capture_output=True, text=True,
                          encoding='utf-8').stdout
    if not diff:
        # New untracked file: whole file as an addition.
        diff = subprocess.run(['git', 'diff', '--no-color', '--no-index', '--', '/dev/null', path], cwd=ROOT,
                              capture_output=True, text=True, encoding='utf-8').stdout
        diff = diff.replace('/dev/null', '/dev/null')
    lines = diff.splitlines(keepends=True)
    head, hunks, cur = [], [], None
    for ln in lines:
        if ln.startswith('@@'):
            cur = [ln]; hunks.append(cur)
        elif cur is None:
            head.append(ln)
        else:
            cur.append(ln)
    sel_hunks = [h for i, h in enumerate(hunks, 1) if keep is None or i in keep]
    if sel_hunks:
        chunks.append(''.join(head) + ''.join(''.join(h) for h in sel_hunks))
open(out, 'w', encoding='utf-8', newline='').write(''.join(chunks))
print(f'{out}: {len(chunks)} file(s)')
