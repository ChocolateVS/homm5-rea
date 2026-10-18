"""Builds a patch containing only selected hunks of working-tree changes against HEAD (other agents edit the same
files, and the shared index may be stale, so every file is diffed directly against its HEAD blob).
Usage: python mypatch.py <out.patch> <spec>...   spec = path (all hunks) | path:1,3 (1-based hunk numbers)
"""
import os, subprocess, sys, tempfile

ROOT = 'G:/H5Reborn'
out = sys.argv[1]
chunks = []
for spec in sys.argv[2:]:
    path, _, sel = spec.partition(':')
    keep = {int(x) for x in sel.split(',')} if sel else None
    head = subprocess.run(['git', 'show', 'HEAD:' + path], cwd=ROOT, capture_output=True)
    tracked = head.returncode == 0
    with tempfile.NamedTemporaryFile(delete=False, suffix='.orig') as tmp:
        tmp.write(head.stdout if tracked else b'')
        tmp_path = tmp.name
    try:
        diff = subprocess.run(['git', 'diff', '--no-color', '--no-index', '--', tmp_path if tracked else '/dev/null', path],
                              cwd=ROOT, capture_output=True, text=True, encoding='utf-8').stdout
    finally:
        os.unlink(tmp_path)
    if not diff:
        continue
    lines = diff.splitlines(keepends=True)
    header, hunks, cur = [], [], None
    for ln in lines:
        if ln.startswith('@@'):
            cur = [ln]; hunks.append(cur)
        elif cur is None:
            header.append(ln)
        else:
            cur.append(ln)
    # Normalise the header to a/<path> b/<path> (the temp file name must not leak into the patch).
    fixed = [f'diff --git a/{path} b/{path}\n']
    if not tracked:
        fixed.append('new file mode 100644\n')
    fixed.append(f'--- {"a/" + path if tracked else "/dev/null"}\n')
    fixed.append(f'+++ b/{path}\n')
    sel_hunks = [h for i, h in enumerate(hunks, 1) if keep is None or i in keep]
    if sel_hunks:
        chunks.append(''.join(fixed) + ''.join(''.join(h) for h in sel_hunks))
open(out, 'w', encoding='utf-8', newline='').write(''.join(chunks))
print(f'{out}: {len(chunks)} file(s)')
