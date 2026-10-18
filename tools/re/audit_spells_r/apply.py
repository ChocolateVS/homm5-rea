"""Applies textual edits to the working tree and records them (anchored on HEAD text) for commit_mine.py.
Usage: python apply.py <edits.json>   (edits.json: {"files": [...], "edits": [[path, old, new], ...]})"""
import json, subprocess, sys
spec = json.load(open(sys.argv[1], encoding="utf-8"))
for path, old, new in spec["edits"]:
    head = subprocess.run(["git", "show", f"HEAD:{path}"], capture_output=True, check=True).stdout.decode("utf-8").replace("\r\n", "\n")
    if head.count(old) != 1: raise SystemExit(f"anchor not unique in HEAD:{path}: {old[:70]!r}")
    raw = open(path, encoding="utf-8", newline="").read()
    crlf = "\r\n" in raw
    t = raw.replace("\r\n", "\n")
    if t.count(old) == 0 and (new == "" or t.count(new) >= 1): continue   # already applied
    if t.count(old) != 1: raise SystemExit(f"anchor not unique in worktree:{path}: {old[:70]!r}")
    t = t.replace(old, new)
    open(path, "w", encoding="utf-8", newline="").write(t.replace("\n", "\r\n") if crlf else t)
print("ok")
