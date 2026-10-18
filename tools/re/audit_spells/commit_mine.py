"""Commit only our changes without touching the shared index: builds a temporary index from HEAD,
adds our new files from the working tree and applies our textual edits to HEAD's version of shared
files. Usage: python commit_mine.py <edits.json> <message-file>
edits.json: {"files": [paths copied verbatim], "edits": [[path, old, new], ...]}"""
import json, os, subprocess, sys, tempfile
spec = json.load(open(sys.argv[1], encoding="utf-8"))
msg = sys.argv[2]
env = dict(os.environ)
idx = tempfile.mktemp(suffix=".idx")
env["GIT_INDEX_FILE"] = idx
def git(*a, inp=None):
    r = subprocess.run(["git", *a], env=env, input=inp, capture_output=True)
    if r.returncode: raise SystemExit(f"git {a}: {r.stderr.decode(errors='replace')}")
    return r.stdout
git("read-tree", "HEAD")
for p in spec.get("files", []):
    git("add", "--", p)
by = {}
for path, old, new in spec.get("edits", []):
    by.setdefault(path, []).append((old, new))
for path, eds in by.items():
    try: txt = subprocess.run(["git", "show", f"HEAD:{path}"], capture_output=True, check=True).stdout.decode("utf-8")
    except subprocess.CalledProcessError: raise SystemExit(f"not in HEAD: {path}")
    crlf = "\r\n" in txt
    t = txt.replace("\r\n", "\n")
    for old, new in eds:
        old = old.replace("\r\n", "\n"); new = new.replace("\r\n", "\n")
        if t.count(old) != 1: raise SystemExit(f"edit anchor not unique/present in HEAD:{path}: {old[:60]!r}")
        t = t.replace(old, new)
    if crlf: t = t.replace("\n", "\r\n")
    blob = git("hash-object", "-w", "--stdin", inp=t.encode("utf-8")).decode().strip()
    mode = "100644"
    git("update-index", "--add", "--cacheinfo", f"{mode},{blob},{path}")
tree = git("write-tree").decode().strip()
head = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, check=True).stdout.decode().strip()
c = subprocess.run(["git", "-c", "user.email=office@rennacs.com", "-c", "user.name=H5Reborn", "commit-tree", tree, "-p", head, "-F", msg], capture_output=True, check=True).stdout.decode().strip()
# The caller builds/tests this commit, then moves HEAD with: git update-ref HEAD <commit> <parent>
print(c, head)
os.remove(idx)
