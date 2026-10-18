import sys
p=sys.argv[1]; t=open(p,encoding='utf-8').read()
old='    public bool[] TacticsDone = { false, false };\n'
new=old+'    /// <summary>Retreat (flee and surrender) forbidden for a side in this battle (exe CCombatArmy reason 7), e.g. by a map script.</summary>\n    public bool[] RetreatDenied = { false, false };\n'
assert t.count(old)==1 and 'RetreatDenied' not in t
open(p,'w',encoding='utf-8',newline='').write(t.replace(old,new))
