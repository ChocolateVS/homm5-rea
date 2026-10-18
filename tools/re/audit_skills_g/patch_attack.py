"""Skills audit G patch for Battle.Attack.cs: perk extra elemental damage goes through the spell protection of its
stand-in spell (exe FUN_0097d740), and gets no Mark of Fire multiplier."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import apply
apply(sys.argv[1], [
 ('DealElementalDamage(t, SpellLikeAmount(dmg * (double)0.1f), Elements.Fire, a.Id, "FIERY_WRATH");   // exe FUN_00a5ed90',
  'DealPerkElementalDamage(t, dmg * (double)0.1f, "SPELL_FIREBALL", a.Id, "FIERY_WRATH");   // exe FUN_00a5ed90 → 0097d740, ECX=5'),
 ('DealElementalDamage(t, SpellLikeAmount(dmg * (double)0.1f), Elements.Water, a.Id, "COLD_STEEL");   // exe FUN_00a5c000',
  'DealPerkElementalDamage(t, dmg * (double)0.1f, "SPELL_ICE_BOLT", a.Id, "COLD_STEEL");   // exe FUN_00a5c000 → 0097d740, ECX=4'),
 ('DealElementalDamage(a, SpellLikeAmount(dmg * (double)0.05f), Elements.Water, t.Id, "CHILLING_BONES");   // exe FUN_00a5bc50',
  'DealPerkElementalDamage(a, dmg * (double)0.05f, "SPELL_SKILL_CHILLING_BONES", t.Id, "CHILLING_BONES");   // exe FUN_00a5bc50 → 0097d740, ECX=0xcf'),
 ('DealElementalDamage(t, 50, Elements.Fire, u.Id, "FLAMING_ARROWS");',
  'DealPerkElementalDamage(t, 50, "SPELL_FIREBALL", u.Id, "FLAMING_ARROWS");   // exe FUN_00a5ed90 (Wildfire 0x82) → 0097d740, ECX=5'),
])
