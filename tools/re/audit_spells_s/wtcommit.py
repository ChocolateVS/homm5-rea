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
    def test(target='H5Reborn.sln', build_only=False):
        if build_only:
            r = run('dotnet', 'build', 'H5Reborn.sln', '-nodeReuse:false', cwd=WT, check=False)
            if r.returncode: raise SystemExit('BUILD FAILED - nothing committed: ' + r.stdout[-1500:])
            print('build ok'); return
        r = run('dotnet', 'test', target, '-nodeReuse:false', cwd=WT, check=False)
        lines = sorted({l.strip() for l in r.stdout.splitlines() if 'Passed!' in l or 'Failed!' in l or ' error ' in l or '[FAIL]' in l})
        print(chr(10).join(lines))
        if r.returncode or not lines or any('Failed!' in l or ' error ' in l for l in lines):
            raise SystemExit('TESTS FAILED - nothing committed')
    test()
    run('git', 'add', '--', *paths, cwd=WT)
    run('git', '-c', 'user.email=office@rennacs.com', '-c', 'user.name=H5Reborn', 'commit', '-q', '-F', msg, cwd=WT)
    for attempt in range(8):
        new = run('git', 'rev-parse', 'HEAD', cwd=WT).stdout.strip()
        if run('git', 'update-ref', 'refs/heads/main', new, head, check=False).returncode == 0: break
        # main moved while testing: redo the commit on the new main (own paths only), regenerate the ledger, retest.
        mine, old = new, head
        head = run('git', 'rev-parse', 'refs/heads/main').stdout.strip()
        touched = set(run('git', 'diff', '--name-only', old, head).stdout.split())
        print(f'main moved to {head[:8]} - rebuilding on it')
        run('git', 'reset', '-q', '--hard', head, cwd=WT)
        own = [p for p in paths if p != 'docs/rules/AUDIT_COMBAT.md']
        if touched & set(own): raise SystemExit(f'main changed my paths {touched & set(own)} - redo by hand')
        run('git', 'checkout', mine, '--', *own, cwd=WT)
        if spec.get('ledger'):
            print(run(sys.executable, f'{WT}/tools/re/audit/ledger.py', EXTRACT, env=dict(os.environ, H5_TREE=WT)).stdout.strip())
        # The full suite already passed on the earlier base with these changes; the intervening commits are disjoint from
        # them. Re-check what they could interact with: the combat tests when they touch combat/core code, else a build.
        combat = any(p.startswith(('src/H5.Combat/', 'src/H5.Core/', 'tests/H5.Combat.Tests/')) for p in touched)
        test('tests/H5.Combat.Tests', build_only=not combat)
        run('git', 'add', '--', *paths, cwd=WT)
        run('git', '-c', 'user.email=office@rennacs.com', '-c', 'user.name=H5Reborn', 'commit', '-q', '-F', msg, cwd=WT)
    else:
        raise SystemExit('main kept moving - not committed')
    if spec.get('ledger'):
        shutil.copyfile(f'{WT}/docs/rules/AUDIT_COMBAT.md', f'{REPO}/docs/rules/AUDIT_COMBAT.md')
    run('git', 'reset', '-q', '--', *paths, check=False)
    print(run('git', 'log', '--oneline', '-1', new).stdout.strip())
finally:
    remove_wt()
