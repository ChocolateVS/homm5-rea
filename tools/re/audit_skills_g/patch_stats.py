"""Skills audit G patch for Battle.Stats.cs: Boneward / Sap Magic / Luck of the Barbarian per exe FUN_0097af50."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import apply
apply(sys.argv[1], [
 ('    double SpellDamageMultiplier(Unit target, SpellDef spell, int casterSide)\n',
  '    /// <remarks>\n'
  '    /// Hero perks per exe FUN_0097af50 (target side): Boneward (HERO_SKILL_SPELLPROOF_BONES) and Sap Magic\n'
  '    /// (HERO_SKILL_MAGIC_CUSHION) ×0.8 only for Destructive spells (spell school field +0x88 == 0), Sap Magic only when the\n'
  '    /// caster is a hero (caster vfunc 0x78); Luck of the Barbarian ×clamp(1 − MagicProofPerLuck, 0, 1) when the stack\'s\n'
  '    /// luck is positive. <paramref name="protectionOnly"/> leaves out the damage boosts (vulnerability auras, Mark of\n'
  '    /// Fire), which the exe applies to raw spell damage in FUN_00977a80 rather than to the protection share.\n'
  '    /// </remarks>\n'
  '    internal double SpellDamageMultiplier(Unit target, SpellDef spell, int casterSide, bool casterIsHero = true, bool protectionOnly = false)\n'),
 ('        if (th.HasPerk(Sk.SapMagic)) m *= 0.8;                                                   // p.114\n'
  '        if (th.HasPerk(Sk.Boneward) && spell.School == "MAGIC_SCHOOL_DESTRUCTIVE" && IsUndeadLike(target)) m *= 0.8;\n',
  '        if (th.HasPerk(Sk.SapMagic) && casterIsHero && spell.School == "MAGIC_SCHOOL_DESTRUCTIVE") m *= 0.8;   // exe 0097b009\n'
  '        if (th.HasPerk(Sk.Boneward) && spell.School == "MAGIC_SCHOOL_DESTRUCTIVE") m *= 0.8;                   // exe 0097afc0\n'),
 ('        if (th.HasPerk(Sk.LuckOfBarbarian))\n'
  '            m *= Math.Max(0, 1 - Stats.Get("combat/HeroSkills/Barbarian/LuckOfTheBarbarian_MagicProofPerLuck", 0.05) * Math.Max(0, LuckOf(target)));\n',
  '        if (th.HasPerk(Sk.LuckOfBarbarian) && LuckOf(target) > 0)                                                // exe 0097b051\n'
  '            m *= Math.Clamp(1 - (double)(float)Stats.Get("combat/HeroSkills/Barbarian/LuckOfTheBarbarian_MagicProofPerLuck", 0.05), 0, 1);\n'
  '        if (protectionOnly) return m;\n'),
])
