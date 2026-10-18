import sys
p=sys.argv[1]; t=open(p,encoding='utf-8').read()
old='''    string? DoSurrender(SurrenderCmd c)
    {
        S.Heroes[c.Side].Fled = true;'''
new='''    string? DoSurrender(SurrenderCmd c)
    {
        // H5_Game.exe CCombatArmy::CanRetreat (FUN_004d9e20) gates both flee and surrender.
        if (RetreatRefusal(c.Side, c.PayRansom) is var refusal && refusal != 0) return $"cannot retreat (reason {refusal})";
        S.Heroes[c.Side].Fled = true;'''
assert t.count(old)==1
open(p,'w',encoding='utf-8',newline='').write(t.replace(old,new))
