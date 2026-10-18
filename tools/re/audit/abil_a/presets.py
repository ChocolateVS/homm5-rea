import sys,glob,re,os
X=sys.argv[1]
# creature ID -> xdb path via RefTables/Creatures.xdb
t=open(X+'/GameMechanics/RefTables/Creatures.xdb',encoding='utf-8').read()
idpath=dict(re.findall(r'<ID>(CREATURE_\w+)</ID>\s*<Obj href="([^"#]+)',t))
used=set()
for f in glob.glob(X+'/Maps/DuelMode/Heroes/*.xdb'):
    used|=set(re.findall(r'<Creature>(CREATURE_\w+)</Creature>',open(f,encoding='utf-8').read()))
mine=set(l.strip() for l in open('tools/re/audit/out/abil_a/mine.txt'))
hits={}
for c in used:
    p=idpath.get(c)
    if not p: continue
    tt=open(X+p,encoding='utf-8',errors='replace').read()
    for a in re.findall(r'<Item>(ABILITY_\w+)</Item>',tt):
        if a in mine: hits.setdefault(a,set()).add(c)
for a in sorted(hits): print(a, sorted(hits[a]))
print(len(hits),'of',len(mine))
