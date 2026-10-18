import sys,glob,re,os
X=sys.argv[1]
order=['Haven','Inferno','Necropolis','Preserve','Academy','Dungeon','Dwarf','Fortress','Orcs','Neutrals']
fac={}
for f in glob.glob(X+'/GameMechanics/Creature/Creatures/**/*.xdb',recursive=True):
    f=f.replace(os.sep,'/')
    folder=f.split('/GameMechanics/Creature/Creatures/')[1].split('/')[0]
    t=open(f,encoding='utf-8',errors='replace').read()
    ab=re.search(r'<Abilities>(.*?)</Abilities>',t,re.S)
    if not ab: continue
    for a in re.findall(r'<Item>(ABILITY_\w+)</Item>',ab.group(1)):
        fac.setdefault(a,{}).setdefault(folder,[]).append(os.path.basename(f)[:-4])
unver=[l.strip() for l in open('tools/re/audit/out/abil_a/unver.txt')]
A={'Haven','Inferno','Necropolis','Preserve'}
mine=[];other=[];none=[]
for a in unver:
    fs=fac.get(a)
    if not fs: none.append(a); continue
    first=min(fs,key=lambda k: order.index(k) if k in order else 99)
    (mine if first in A else other).append((a,first,fs[first][:3]))
print('MINE',len(mine))
for m in mine: print(' ',m)
print('OTHER',len(other)); print('NONE',len(none),none)
open('tools/re/audit/out/abil_a/mine.txt','w').write('\n'.join(m[0] for m in mine))
