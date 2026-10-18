"""SKILLS audit patch for src/H5.Combat/Battle.Attack.cs: Last Stand without a once-per-combat limit
(exe FUN_00a59490) and Chilling Steel / Chilling Bones amounts rounded like FUN_0097d740."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')

def rep(old, new):
    global t
    if old not in t:
        raise SystemExit(f'pattern not found: {old[:80]!r}')
    t = t.replace(old, new, 1)

rep('''        // Last Stand: a stack of 2+ survives a lethal blow with 1 HP, once (p.111).
        if (total <= 0 && before >= 2 && HeroOf(t).HasPerk(Sk.LastStand) && !t.AbilityUses.ContainsKey("LAST_STAND") && t.Kind == UnitKind.Creature)
        {
            t.AbilityUses["LAST_STAND"] = 1;
            total = 1;''',
'''        // Last Stand (exe FUN_00a59490, applied by the damage pipeline FUN_00a61600 → FUN_00a594f0): a stack of
        // 2+ creatures hit for at least its total HP keeps 1 HP. There is no once-per-combat flag — it triggers
        // again whenever the stack is back to 2+ creatures (fan manual p.111 "for each stack ... before the last blow").
        if (total <= 0 && before >= 2 && HeroOf(t).HasPerk(Sk.LastStand) && t.Kind == UnitKind.Creature)
        {
            total = 1;''')

rep('''                DealElementalDamage(t, (int)(dmg * 0.1), Elements.Water, a.Id, "COLD_STEEL");''',
'''                DealElementalDamage(t, SpellLikeAmount(dmg * (double)0.1f), Elements.Water, a.Id, "COLD_STEEL");   // exe FUN_00a5c000''')

rep('''                if (th.HasPerk(Sk.ChillingBones) && IsUndeadLike(t) && a.Alive) DealElementalDamage(a, (int)(dmg * 0.05), Elements.Water, t.Id, "CHILLING_BONES");''',
'''                if (th.HasPerk(Sk.ChillingBones) && IsUndeadLike(t) && a.Alive) DealElementalDamage(a, SpellLikeAmount(dmg * (double)0.05f), Elements.Water, t.Id, "CHILLING_BONES");   // exe FUN_00a5bc50''')

open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
