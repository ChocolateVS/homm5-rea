"""Stage only the hunks of a file's working-tree diff that contain one of the given markers.

Usage: python tools/audiore/stage_mine.py <path> <marker>...   (run from the repo root)
Other agents edit the same files concurrently; this commits our one-line hooks only.
"""
import re, subprocess, sys, tempfile

path, markers = sys.argv[1], sys.argv[2:]
diff = subprocess.run(["git", "diff", "-U1", "--", path], capture_output=True, text=True, encoding="utf-8").stdout
parts = re.split(r"(?m)^(?=@@ )", diff)
head, hunks = parts[0], parts[1:]
keep = [h for h in hunks if any(m in h for m in markers)]
print(f"{path}: {len(keep)}/{len(hunks)} hunks staged")
if keep:
    with tempfile.NamedTemporaryFile("w", suffix=".diff", delete=False, encoding="utf-8", newline="\n") as f:
        f.write(head + "".join(keep))
    subprocess.run(["git", "apply", "--cached", "--recount", f.name], check=True)
