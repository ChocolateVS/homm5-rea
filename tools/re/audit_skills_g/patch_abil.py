"""Skills audit G patch for Battle.Abilities.cs: Explosion is cast by a creature, so Sap Magic does not apply."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import apply
apply(sys.argv[1], [
 ('SpellDamageMultiplier(v, spell, Controller(u))', 'SpellDamageMultiplier(v, spell, Controller(u), casterIsHero: false)'),
])
