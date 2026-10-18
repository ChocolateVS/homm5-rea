import sys
p = sys.argv[1]; t = open(p, encoding='utf-8').read()
line = 'tools/re/audit_heroes/out/'
if line not in t.split('\n'): t = t.rstrip('\n') + '\n' + line + '\n'
open(p, 'w', encoding='utf-8').write(t)
