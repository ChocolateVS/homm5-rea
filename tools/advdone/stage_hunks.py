"""Stage only the hunks of a file's working-tree diff that don't mention any excluded marker.

Several agents edit the same files; this commits our hunks without sweeping up theirs.
Usage: python stage_hunks.py <path> <exclude-marker>...   (run from the repo root)
"""
import re
import subprocess
import sys
import tempfile

path, markers = sys.argv[1], sys.argv[2:]
diff = subprocess.run(["git", "diff", "--", path], capture_output=True, text=True, encoding="utf-8").stdout
parts = re.split(r"(?m)^(?=@@ )", diff)
head, hunks = parts[0], parts[1:]
keep = [h for h in hunks if not any(m in h for m in markers)]
print(f"{path}: {len(keep)}/{len(hunks)} hunks staged")
if keep:
    with tempfile.NamedTemporaryFile("w", suffix=".diff", delete=False, encoding="utf-8", newline="\n") as f:
        f.write(head + "".join(keep))
    subprocess.run(["git", "apply", "--cached", "--recount", f.name], check=True)
