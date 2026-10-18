import sys
p=sys.argv[1]; t=open(p,encoding='utf-8').read()
old='''            if (h.HasPerk(Sk.Toughness)) u.BaseHealth += 2;
'''
new=old+'''            // Ring of Life (artifact 21): +2 HP per equipped copy (exe FUN_004bc470: 2 x CountArtifact(21),
            // FUN_00ca6680 counts the hero's equipped artifacts of that id).
            u.BaseHealth += 2 * h.Artifacts.Count(a => a == "RING_OF_LIFE");
'''
assert t.count(old)==1 and 'RING_OF_LIFE' not in t
open(p,'w',encoding='utf-8',newline='').write(t.replace(old,new))
