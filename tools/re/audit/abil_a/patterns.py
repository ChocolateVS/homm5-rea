import sys,glob,re,os
X=sys.argv[1]
def pat(t):
    items=re.findall(r'<Item>\s*<cells>(.*?)</cells>\s*<AngleRotateTo>([-\d.]+)',t,re.S)
    out=[]
    for cells,ang in items:
        out.append(([(int(a),int(b)) for a,b in re.findall(r'<x>(-?\d+)</x>\s*<y>(-?\d+)</y>',cells)],ang))
    hdr={k:(re.search(f'<{k}>([^<]*)</{k}>',t) or [None,None])[1] for k in ['RotateToMainTarget','DamageToMainTargetCoefficient','DamageToOtherTargetsCoefficient','DamageAll']}
    return out,hdr
for name in sys.argv[2:]:
    for f in glob.glob(X+'/GameMechanics/Creature/Creatures/**/'+name+'.xdb',recursive=True):
        t=open(f,encoding='utf-8',errors='replace').read()
        m=re.search(r'<PatternAttack(?: ObjectRecordID="[0-9]+")?>(.*?)</PatternAttack>',t,re.S)
        if not m: print(name,'no pattern'); continue
        body=m.group(1)
        href=re.search(r'href="([^"]+)"',m.group(0))
        p,h=pat(body)
        print('==',os.path.basename(f),h,'npatterns',len(p))
        for cells,ang in p[:3]: print('   ',ang,cells)
