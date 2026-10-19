"""Commits only this workstream's changes, without touching the shared index or other people's work in progress.

Usage: python wtcommit.py <spec.json> <message-file>
spec.json: {"files": [paths copied verbatim from the main working tree],
            "edits": [[path, old, new], ...]   (applied to HEAD's version of shared files),
            "ledger": true                    (regenerate docs/rules/AUDIT_COMBAT.md inside the checkout)}
A clean worktree at HEAD is made under the scratchpad, the files / edits are applied there, the ledger regenerated,
`dotnet test H5Reborn.sln` run; only if it passes is the commit made there and main moved to it (refused if main moved
meanwhile). Afterwards the worktree is removed and the committed paths are reset in the main index.
"""
import json, os, shutil, subprocess, sys

REPO = 'G:/H5Reborn'
SP = 'C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/spells'
WT = SP + '/wt'
EXTRACT = 'C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/extract'


def run(*a, cwd=REPO, check=True, env=None):
    r = subprocess.run(list(a), cwd=cwd, capture_output=True, text=True, encoding='utf-8', errors='replace', env=env)
    if check and r.returncode:
        raise SystemExit(f'{a}: {r.stdout[-2000:]}{r.stderr[-2000:]}')
    return r


def remove_wt():
    run('git', 'worktree', 'remove', '--force', WT, check=False)
    shutil.rmtree(WT, ignore_errors=True)
    run('git', 'worktree', 'prune', check=False)


spec = json.load(open(sys.argv[1], encoding='utf-8'))
msg = os.path.abspath(sys.argv[2])
head = run('git', 'rev-parse', 'HEAD').stdout.strip()
remove_wt()
run('git', 'worktree', 'add', '--detach', WT, head)
try:
    paths = list(spec.get('files', []))
    for p in paths:
        os.makedirs(os.path.dirname(f'{WT}/{p}') or '.', exist_ok=True)
        shutil.copyfile(f'{REPO}/{p}', f'{WT}/{p}')
    for path, old, new in spec.get('edits', []):
        raw = open(f'{WT}/{path}', encoding='utf-8', newline='').read()
        crlf = '\r\n' in raw
        t = raw.replace('\r\n', '\n')
        old, new = old.replace('\r\n', '\n'), new.replace('\r\n', '\n')
        if t.count(old) != 1:
            raise SystemExit(f'anchor not unique in HEAD:{path}: {old[:80]!r}')
        t = t.replace(old, new)
        open(f'{WT}/{path}', 'w', encoding='utf-8', newline='').write(t.replace('\n', '\r\n') if crlf else t)
        if path not in paths: paths.append(path)
    if spec.get('ledger'):
        env = dict(os.environ, H5_TREE=WT)
        print(run(sys.executable, f'{WT}/tools/re/audit/ledger.py', EXTRACT, env=env).stdout.strip())
        paths.append('docs/rules/AUDIT_COMBAT.md')
    r = run('dotnet', 'test', 'H5Reborn.sln', '-nodeReuse:false', cwd=WT, check=False)
    lines = sorted({l.strip() for l in r.stdout.splitlines() if 'Passed!' in l or 'Failed!' in l or ' error ' in l or '[FAIL]' in l})
    print('\n'.join(lines))
    if r.returncode or not lines or any('Failed!' in l or ' error ' in l for l in lines):
        raise SystemExit('TESTS FAILED - nothing committed')
    run('git', 'add', '--', *paths, cwd=WT)
    run('git', '-c', 'user.email=office@rennacs.com', '-c', 'user.name=H5Reborn', 'commit', '-q', '-F', msg, cwd=WT)
    new = run('git', 'rev-parse', 'HEAD', cwd=WT).stdout.strip()
    run('git', 'update-ref', 'refs/heads/main', new, head)
    if spec.get('ledger'):
        shutil.copyfile(f'{WT}/docs/rules/AUDIT_COMBAT.md', f'{REPO}/docs/rules/AUDIT_COMBAT.md')
    run('git', 'reset', '-q', '--', *paths, check=False)
    print(run('git', 'log', '--oneline', '-1', new).stdout.strip())
finally:
    remove_wt()
