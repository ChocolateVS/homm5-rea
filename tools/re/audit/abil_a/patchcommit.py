"""Hunk-level verify/commit for files other auditors also edit (abilities part A).

edits.json: [{"file": path, "old": text, "new": text}, ... , {"file": path, "whole": true}]
  - old/new pairs are applied to the HEAD version of the file (each `old` must occur exactly once there);
  - "whole" entries take the working-tree file as is (files only this workstream touches).

  python patchcommit.py edits.json --tree DIR      write HEAD+my edits into DIR (a clean worktree of HEAD)
  python patchcommit.py edits.json --commit MSG    commit HEAD+my edits through a private index
"""
import json, os, subprocess, sys

ROOT = 'G:/H5Reborn'
IDX = ROOT + '/tools/re/audit/out/abil_a/index.tmp'

def git(*args, env=None, inp=None):
    return subprocess.run(['git', *args], cwd=ROOT, env=env, input=inp, capture_output=True, check=True).stdout

def build(edits):
    files = {}
    for e in edits:
        f = e['file']
        if e.get('whole'):
            files[f] = open(os.path.join(ROOT, f), 'rb').read()
            continue
        if f not in files:
            files[f] = git('show', 'HEAD:' + f)
        text = files[f].decode('utf-8')
        old, new = e['old'], e['new']
        crlf = '\r\n' in text
        if crlf: old, new = old.replace('\n', '\r\n'), new.replace('\n', '\r\n')
        n = text.count(old)
        if n != 1: sys.exit(f'{f}: expected one occurrence, found {n}: {old[:80]!r}')
        files[f] = text.replace(old, new).encode('utf-8')
    return files

def main():
    edits = json.load(open(sys.argv[1], encoding='utf-8'))
    files = build(edits)
    if sys.argv[2] == '--tree':
        d = sys.argv[3]
        for f, data in files.items():
            p = os.path.join(d, f); os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, 'wb').write(data)
        print('wrote', len(files), 'files')
    elif sys.argv[2] == '--commit':
        env = dict(os.environ, GIT_INDEX_FILE=IDX)
        if os.path.exists(IDX): os.remove(IDX)
        git('read-tree', 'HEAD', env=env)
        for f, data in files.items():
            sha = git('hash-object', '-w', '--stdin', inp=data).decode().strip()
            git('update-index', '--add', '--cacheinfo', f'100644,{sha},{f}', env=env)
        msg = sys.argv[3] + '\n\nCo-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>'
        git('-c', 'user.email=office@rennacs.com', '-c', 'user.name=H5Reborn', 'commit', '-q', '-m', msg, env=env)
        os.remove(IDX)
        print(git('log', '--oneline', '-1').decode().strip())

main()
