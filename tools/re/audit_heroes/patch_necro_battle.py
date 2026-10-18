import sys
p=sys.argv[1]; t=open(p,encoding='utf-8').read()
def rep(old,new):
    global t
    assert t.count(old)==1, old[:80]
    t=t.replace(old,new)
rep('''        long expAtt = ExpFor(pb.DefenderArmy, c.DefenderSurvivors);''','''        long expAtt = ExpFor(pb.DefenderArmy, c.DefenderSurvivors);
        // Captured before the survivors replace the attacker's army (the defender's experience and necromancy
        // need the attacker's pre-battle stacks).
        var attackerBefore = att.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
        static IEnumerable<(string, int)> Killed(List<Stack> before, List<ArmySlot> after) =>
            before.GroupBy(s => s.Creature).Select(g => (g.Key, g.Sum(s => s.Count) - after.Where(a => a.Creature == g.Key).Sum(a => a.Count)));''')
rep('''            GainExperience(att, expAtt);
            if (att.Army.StackCount() == 0) RemoveHero(att, "army destroyed");''','''            GainExperience(att, expAtt);
            NecromancyRaise(att, Killed(pb.DefenderArmy, c.DefenderSurvivors));   // exe FUN_00c26be0
            if (att.Army.StackCount() == 0) RemoveHero(att, "army destroyed");''')
rep('''            if (defHero != null) GainExperience(defHero, ExpFor(att.Army.Live().ToList(), c.AttackerSurvivors) + 500);''','''            if (defHero != null)
            {
                GainExperience(defHero, ExpFor(attackerBefore, c.AttackerSurvivors) + 500);
                NecromancyRaise(defHero, Killed(attackerBefore, c.AttackerSurvivors));
            }''')
open(p,'w',encoding='utf-8',newline='').write(t)
