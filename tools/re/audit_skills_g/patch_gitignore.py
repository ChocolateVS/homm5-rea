"""Skills audit G: ignore this workstream's Ghidra output folder."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
line = 'tools/re/audit_skills_g/out/'
if line not in t.splitlines():
    if not t.endswith(nl): t += nl
    t += line + nl
open(p, 'w', encoding='utf-8', newline='').write(t)
