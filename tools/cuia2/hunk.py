"""Write HEAD's version of a file with exact text edits applied (our hunks only), for commit.sh path::file.
Usage: python hunk.py <path> <out> <old1> <new1> [<old2> <new2> ...]   (old/new may be @file to read text)"""
import subprocess, sys
path, out = sys.argv[1], sys.argv[2]
txt = subprocess.run(["git", "show", f"HEAD:{path}"], capture_output=True, check=True).stdout.decode("utf-8")
crlf = "\r\n" in txt
t = txt.replace("\r\n", "\n")
args = sys.argv[3:]
def rd(s): return open(s[1:], encoding="utf-8").read() if s.startswith("@") else s
for i in range(0, len(args), 2):
    old, new = rd(args[i]).replace("\r\n", "\n"), rd(args[i + 1]).replace("\r\n", "\n")
    if t.count(old) != 1: raise SystemExit(f"anchor not unique in HEAD:{path}: {old[:70]!r}")
    t = t.replace(old, new)
if crlf: t = t.replace("\n", "\r\n")
open(out, "w", encoding="utf-8", newline="").write(t)
print("wrote", out)
