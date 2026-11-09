# Ranks the natives with a residual GAP in docs/mmh55/SCRIPTS.md section 6 by MMH55 use: H55 functions calling them
# directly and H55-Settings globals whose script path (transitive H55 function closure) reaches them.
# Usage (scratch dir with lua55/Index, lua55/Settings extracted and out/ledger.tsv from the SCRIPTS.md table):
#   python -I mmh55_native_deps.py [-v]
import re,sys,collections
base='lua55/Index/scripts/'
files=['H55-Core.lua','H55-AdvMap.lua','H55-Library.lua','advmap-startup.lua','advmap-common.lua']
funcs={}
def strip(t):
    t=re.sub(r"--\[\[.*?\]\]","",t,flags=re.S); t=re.sub(r"--[^\n]*","",t); return t
for f in files:
    t=strip(open(base+f,encoding='latin-1').read())
    parts=re.split(r"\n\s*function\s+([A-Za-z_][\w.:]*)\s*\(",t)
    for i in range(1,len(parts),2): funcs[parts[i]]=parts[i+1]
calls={k:set(re.findall(r"(?<![\w.:])([A-Za-z_]\w*)\s*\(",v)) | set(re.findall(r"[\"']([A-Za-z_]\w*)\(",v)) | set(re.findall(r"Trigger\(\s*\w+\s*,\s*[^,]*?,?\s*\"(\w+)\"",v)) for k,v in funcs.items()}
# string callbacks like "H55_TCQuestionBox('"..hero
for k,v in funcs.items():
    calls[k] |= set(re.findall(r"\"(H55_\w+)\(",v))
led={}
for l in open('out/ledger.tsv'):
    n,st,note=l.rstrip('\n').split('\t'); led[n]=(st,note)
gap={n for n,(st,note) in led.items() if st=='GAP' or 'GAP' in note}
sets=[l.split('=')[0].strip() for l in open('lua55/Settings/scripts/H55-Settings.lua',encoding='latin-1') if re.match(r'\s*H55_\w+\s*=',l)]
dep=collections.defaultdict(set)
for s in sets:
    roots=[k for k,v in funcs.items() if re.search(r"\b"+s+r"\b",v)]
    seen=set(); st=list(roots)
    while st:
        x=st.pop()
        if x in seen: continue
        seen.add(x)
        for c in calls.get(x,()):
            if c in funcs: st.append(c)
    nat=set()
    for x in seen: nat|= {c for c in calls[x] if c not in funcs}
    g=sorted(nat & gap)
    for n in g: dep[n].add(s)
    if len(sys.argv)>1: print(s, len(seen), g)
for n,ss in sorted(dep.items(), key=lambda kv:-len(kv[1])):
    print(f"{n}\t{len(ss)}\t{led[n][0]}")
print("== direct callers")
cnt=collections.Counter()
for k,v in calls.items():
    for c in v:
        if c in gap and c not in funcs: cnt[c]+=1
for n,c in cnt.most_common(): print(f"{n}\t{c}\t{len(dep.get(n,()))}")
