"""HEROES audit: morale rolls per exe 00a6b8f0 (bad) / 00a6ba00 (good): m clamped to ±5,
success iff Range(1,100) < 10·|m| (inclusive range → (10|m|−1) % chance). Usage: <Battle.cs>"""
import sys
p = sys.argv[1]; t = open(p, encoding='utf-8').read()
old_bad = "        if (morale < 0 && S.Rng.Chance(Math.Min(0.5, -morale * 0.1)))\n"
new_bad = "        if (morale < 0 && MoraleRoll(morale))\n"
old_good = "            if (morale > 0 && S.Rng.Chance(Math.Min(0.5, morale * 0.1)))\n"
new_good = "            if (morale > 0 && MoraleRoll(morale))\n"
anchor = "    /// <summary>Start-of-turn processing. Returns true if the actor needs a command.</summary>\n"
helper = ("    /// <summary>Morale roll (exe 00a6b8f0 bad / 00a6ba00 good): |morale| clamped to 5; succeeds iff the battle RNG's\n"
          "    /// inclusive Range(1,100) is below 10·|morale|, i.e. (10·|m| − 1) % (9 % at ±1, 49 % at ±5).</summary>\n"
          "    internal bool MoraleRoll(int morale) => S.Rng.Range(1, 100) < 10 * Math.Min(5, Math.Abs(morale));\n\n")
for o, n in ((old_bad, new_bad), (old_good, new_good)):
    if n in t: continue
    if o not in t: sys.exit('anchor missing: ' + o.strip())
    t = t.replace(o, n, 1)
if 'bool MoraleRoll(' not in t:
    if anchor not in t: sys.exit('helper anchor missing')
    t = t.replace(anchor, helper + anchor, 1)
open(p, 'w', encoding='utf-8').write(t); print('patched', p)
