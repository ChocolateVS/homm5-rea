"""SKILLS audit: commit only our changes without touching the shared index or other agents' work.

Builds a temporary index from HEAD, adds our new/owned files verbatim from the working tree, and runs our
patch scripts on HEAD's version of files other agents also edit. Then builds + tests the resulting tree in a
scratch worktree and, only if green, moves HEAD (compare-and-swap) and refreshes the shared index entries of
the touched paths (only where the shared entry still equals the old HEAD blob).

Usage: python commit_mine.py <spec.json> <message-file> [--no-test]
spec.json: {"files": [paths copied verbatim], "patches": [[script.py, path], ...]}
"""
import json, os, shutil, subprocess, sys, tempfile

ROOT = 'G:/H5Reborn'
WT = 'C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/audit-skills-commit'
spec = json.load(open(sys.argv[1], encoding='utf-8'))
msg = sys.argv[2]
run_tests = '--no-test' not in sys.argv
os.chdir(ROOT)

def git(*a, env=None, inp=None, check=True):
    r = subprocess.run(['git', *a], env=env, input=inp, capture_output=True)
    if check and r.returncode: raise SystemExit(f'git {a}: {r.stderr.decode(errors="replace")}')
    return r.stdout.decode(errors='replace').strip()

head = git('rev-parse', 'HEAD')
env = dict(os.environ); idx = tempfile.mktemp(suffix='.idx'); env['GIT_INDEX_FILE'] = idx
git('read-tree', head, env=env)
for p in spec.get('files', []):
    git('add', '--', p, env=env)
touched = list(spec.get('files', []))
for script, path in spec.get('patches', []):
    txt = subprocess.run(['git', 'show', f'{head}:{path}'], capture_output=True, check=True).stdout.decode('utf-8')
    crlf = '\r\n' in txt
    tmp = tempfile.mktemp(suffix='.cs')
    open(tmp, 'w', encoding='utf-8', newline='').write(txt.replace('\r\n', '\n'))
    r = subprocess.run([sys.executable, '-I', script, tmp], capture_output=True, text=True)
    if r.returncode: raise SystemExit(f'patch {script} on HEAD:{path} failed: {r.stdout}{r.stderr}')
    t = open(tmp, encoding='utf-8').read(); os.remove(tmp)
    if crlf: t = t.replace('\n', '\r\n')
    blob = git('hash-object', '-w', '--stdin', inp=t.encode('utf-8'))
    git('update-index', '--add', '--cacheinfo', f'100644,{blob},{path}', env=env)
    touched.append(path)
tree = git('write-tree', env=env); os.remove(idx)
commit = subprocess.run(['git', '-c', 'user.email=office@rennacs.com', '-c', 'user.name=H5Reborn', 'commit-tree', tree,
                         '-p', head, '-F', msg], capture_output=True, check=True).stdout.decode().strip()
print('commit', commit, 'parent', head)

# Build (+ test) the exact commit in a scratch worktree.
subprocess.run(['git', 'worktree', 'remove', '--force', WT], capture_output=True)
shutil.rmtree(WT, ignore_errors=True)
git('worktree', 'add', '-f', '--detach', WT, commit)
ok = True
try:
    b = subprocess.run(['dotnet', 'build', 'game/H5Reborn.csproj', '-nodeReuse:false'], cwd=WT, capture_output=True, text=True)
    errs = sorted({l for l in b.stdout.splitlines() if ' error ' in l})
    print('build:', 'FAILED' if b.returncode or errs else 'ok'); print('\n'.join(errs[:10]))
    ok = b.returncode == 0 and not errs
    if ok and run_tests:
        t = subprocess.run(['dotnet', 'test', '-nodeReuse:false'], cwd=WT, capture_output=True, text=True)
        lines = [l for l in t.stdout.splitlines() if 'Passed!' in l or 'Failed!' in l or '[FAIL]' in l or ' error ' in l]
        print('\n'.join(lines))
        ok = t.returncode == 0 and not any('Failed!' in l or ' error ' in l for l in lines)
finally:
    subprocess.run(['git', 'worktree', 'remove', '--force', WT], capture_output=True)
if not ok: raise SystemExit('NOT committed (build/test failed)')

# Move HEAD only if nobody else committed meanwhile.
r = subprocess.run(['git', 'update-ref', 'HEAD', commit, head], capture_output=True, text=True)
if r.returncode: raise SystemExit('HEAD moved meanwhile; re-run: ' + r.stderr)
for p in touched:
    old = git('ls-tree', head, '--', p).split()
    new = git('ls-tree', commit, '--', p).split()
    cur = git('ls-files', '-s', '--', p).split()
    if new and (not cur or (old and cur[1] == old[2])):
        git('update-index', '--add', '--cacheinfo', f'{new[0]},{new[2]},{p}')
print('HEAD ->', commit)
