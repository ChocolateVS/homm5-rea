"""Skills audit G patch for Battle.Spells.cs: Sap Magic needs a hero caster (exe 0097b009, caster vfunc 0x78)."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import apply
apply(sys.argv[1], [
 ('            double mult = SpellDamageMultiplier(t, sp, side) * ElementProtectionArtifacts(t, sp.Element);\n',
  '            double mult = SpellDamageMultiplier(t, sp, side, casterIsHero: hero != null) * ElementProtectionArtifacts(t, sp.Element);\n'),
])
