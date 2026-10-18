"""Skills audit G patch for Battle.SkillAudit.cs: Exorcism has no school check (exe FUN_00977a80)."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import apply
apply(sys.argv[1], [
 ('        if (hero.HasPerk(Sk.Exorcism) && t.Summoned && sp.School == "MAGIC_SCHOOL_DESTRUCTIVE") dmg *= 2;\n',
  '        // No school check: FUN_00977a80 serves every damage spell routed through FUN_0097d600 (skills audit G).\n'
  '        if (hero.HasPerk(Sk.Exorcism) && t.Summoned) dmg *= 2;\n'),
])
