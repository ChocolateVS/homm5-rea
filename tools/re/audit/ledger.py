"""Generates docs/rules/AUDIT_COMBAT.md: one row per combat-relevant ID (creature abilities in use, hero
skills/perks, hero specialisations in use, combat spells, war-machine artifacts) plus core mechanics, with
the H5_Game.exe functions that test each ID, our engine's references and an audit status.

Status:
  FIXED      compared with the exe in this audit and corrected (address + regression test given)
  MATCH      compared with the exe (this audit or an earlier one in EXE_FINDINGS.md) and identical
  GAP        known not to match / not implemented (reason given)
  UNVERIFIED implemented in the engine and tested by the exe, but not yet compared line by line
  N/A        not used by any shipped creature/hero/map, or test content

Inputs: tools/re/audit/out/vc_*.txt (FindVCall maps), tools/re/out/enum_*.txt, extracted game data
(the scratchpad extract of the paks, path in EXTRACT), src/H5.Combat sources, docs/rules/EXE_FINDINGS.md.
"""
import collections, glob, os, re, sys

ROOT = 'G:/H5Reborn'
OUT = ROOT + '/tools/re/audit/out'
TREE = os.environ.get('H5_TREE', ROOT)   # checkout whose sources / fragments / ledger are used (default: ROOT)
EXTRACT = sys.argv[1] if len(sys.argv) > 1 else os.environ.get('H5_EXTRACT', '')

def enum(name):
    m = {}
    for line in open(f'{ROOT}/tools/re/out/enum_{name}.txt'):
        p = line.split()
        if len(p) == 3 and p[1].startswith('0x'): m[p[2]] = int(p[1], 16)
    return m

def calls(slot):
    d = collections.defaultdict(set)
    for line in open(f'{OUT}/vc_{slot}.txt'):
        p = line.split()
        if len(p) >= 3: d[int(p[0], 16)].add(p[2].split('@')[-1] if '@' in p[2] else p[1])
    return d

COMBAT_RANGES = [(0x0089_0000, 0x008f_0000), (0x0096_0000, 0x009b_0000), (0x00a2_0000, 0x00a8_0000),
                 (0x00b3_0000, 0x00b5_0000), (0x00bf_0000, 0x00c0_0000)]
def combat_fns(fns):
    out = []
    for f in sorted(fns):
        try: a = int(f, 16)
        except ValueError: continue
        if any(lo <= a < hi for lo, hi in COMBAT_RANGES): out.append(f)
    return out

src = ''.join(open(f, encoding='utf-8').read() for f in glob.glob(f'{TREE}/src/H5.Combat/**/*.cs', recursive=True))
const = {m.group(2): m.group(1) for m in re.finditer(r'(\w+)\s*=\s*"((?:ABILITY|SPELL|HERO_SKILL|HERO_SPEC|ARTIFACT)_[A-Z0-9_]+)"', src)}
def ours(ident):
    n = src.count('"' + ident + '"')
    c = const.get(ident)
    if c: n += len(re.findall(r'\.' + re.escape(c) + r'\b', src))
    return n
findings = open(f'{TREE}/docs/rules/EXE_FINDINGS.md', encoding='utf-8').read()

# ---- curated audit results (this audit) -------------------------------------------------------------
T = 'tests/H5.Combat.Tests/'
FIXED = {
    'HERO_SKILL_BALLISTA': ('00a75bf0, 00a65260', 'one action = all shots at the chosen target; +1 shot', T + 'WarMachineAuditTests.BallistaShotsPerAction'),
    'HERO_SKILL_TRIPLE_BALLISTA': ('00a75bf0, 00a65260', 'shots per action set to 2 (+1 Ballista = 3) — no extra actions', T + 'WarMachineAuditTests.TripleBallistaFiresThreeShotsInOneActionThenEndsItsTurn'),
    'HERO_SKILL_CATAPULT': ('00a75bf0, 00b3bb50', 'catapult shots fired in one action at the aimed building', T + 'WarMachineAuditTests'),
    'HERO_SKILL_TRIPLE_CATAPULT': ('00a75bf0, 00b3bb50', 'shots per action set to 2 (+1 Catapult)', T + 'WarMachineAuditTests'),
    'ARTIFACT_RING_OF_MACHINE_AFFINITY': ('00a75bf0, 00977190, 008a4800', '+1 ballista/catapult shot, First Aid ×2, Ammo Cart +4 attack', T + 'WarMachineAuditTests.RingOfMachineAffinityDoublesFirstAidAndAddsAmmoCartAttack'),
    'HERO_SPEC_EMPIRIC': ('00977190', 'First Aid Tent +5 HP per level', T + 'WarMachineAuditTests'),
    'HERO_SKILL_FIRST_AID': ('00977190', 'tent heal 10/20/50/100, Empiric, ring ×2', T + 'WarMachineAuditTests'),
    'HERO_SPEC_GRIFFINS': ('008a34c0', 'basic Griffins (creature 7) gain Battle Dive', T + 'AuditSpecTests.GriffonCommanderGivesGriffinsBattleDive'),
    'HERO_SPEC_ELVEN_FURY': ('008a34c0, 00b61890', 'creatures 45..50 gain Enraged (TotE alternates excluded)', '(setup grant; AuditSpecTests)'),
    'ABILITY_INCORPOREAL': ('008a34c0', 'Phantom images / Rune of Etherealness are Incorporeal', T + 'AuditSpecTests.DynamicAbilitiesFollowTheOriginal'),
    'ABILITY_FLESH_AND_BLOOD': ('008a34c0', 'Phantom images lose Flesh and Blood', T + 'AuditSpecTests.DynamicAbilitiesFollowTheOriginal'),
    'ABILITY_UNDEAD': ('008a34c0', 'Vampirism grants Undead', T + 'AuditSpecTests.DynamicAbilitiesFollowTheOriginal'),
    'ABILITY_ENTANGLING_ROOTS': ('008a34c0', 'suppressed by Rage of the Forest', '(Unit.Has)'),
    'ABILITY_HOLD_GROUND': ('008a34c0', 'rooted Take Roots creature holds ground', '(Unit.Has)'),
    'HERO_SKILL_ARCANE_TRAINING': ('00a34740', 'round(min(cost×0.8, cost−1))', T + 'AuditSpecTests.SpellCostsMatchTheOriginalChain'),
    'HERO_SKILL_VOICE_TRAINING': ('00a34740', 'warcries min(cost×(1.001−d), cost−1), ≥1', T + 'AuditSpecTests'),
    'HERO_SPEC_WAR_LEADER': ('00a34740', 'warcry cost −(lvl/4·1+1)', '(HeroManaCost)'),
    'HERO_SPEC_BEATER': ('00a34740', 'Teleport costs half (Rusher)', '(HeroManaCost)'),
    'HERO_SPEC_POWER_BARGAIN': ('00a34740', 'empowered spells ×clamp(1−0.05−0.01·lvl, 0.5, 1)', '(HeroManaCost)'),
    'ABILITY_ENERGY_CHANNEL': ('009781e0, 00978120', '×0.75 rounded, at least −1', '(HeroManaCost)'),
    'HERO_SKILL_SUPRESS_DARK': ('009781e0', 'enemy Dark spells ×2', '(HeroManaCost)'),
    'HERO_SKILL_SUPRESS_LIGHT': ('009781e0', 'enemy Light spells ×2', '(HeroManaCost)'),
    'HERO_SKILL_ELITE_CASTERS': ('009781e0', 'creature casters: integer half', '(CreatureManaCost)'),
    'HERO_SKILL_CORRUPT_DESTRUCTIVE': ('009781e0, 004e6000', 'rounded ×(1+increase)', '(HeroManaCost)'),
    'HERO_SKILL_CORRUPT_DARK': ('009781e0, 004e6000', 'rounded ×(1+increase)', '(HeroManaCost)'),
    'HERO_SKILL_CORRUPT_LIGHT': ('009781e0, 004e6000', 'rounded ×(1+increase)', '(HeroManaCost)'),
    'HERO_SKILL_CORRUPT_SUMMONING': ('009781e0, 004e6000', 'rounded ×(1+increase)', '(HeroManaCost)'),
    'HERO_SPEC_WHITE_KNIGHT': ('009781e0, 00989fd0, 00989790, 0098a430', 'Light spells −1 mana; Prayer bonus ×2', T + 'AuditSpecTests.WhiteKnightDoublesPrayer'),
    'HERO_SKILL_PRAYER': ('00989fd0, 00989790, 0098a430', 'bonus = Prayer_DefenceBonus (data), not hard-coded', T + 'AuditSpecTests.WhiteKnightDoublesPrayer'),
    'RING_OF_MAGI': ('00a34740', 'spell cost halved (integer)', T + 'AuditSpecTests.SpellCostsMatchTheOriginalChain'),
    'HERO_SPEC_DISRUPTER': ('00976db0', 'Vulnerability deals round(10 + (lvl−1)·10/3)', '(ResolveSpell)'),
    'HERO_SPEC_STORMCALLER': ('0097c9f0', 'Air spells skip the resistance roll', '(SpellHits)'),
    'HERO_SPEC_PRUDENT': ('008a4330', 'enemy initiative −clamp(lvl·1 %, 0, 1)', T + 'AuditSpecTests2.PrudentSlowsEnemies'),
    'HERO_SPEC_MASTER_OF_INITIATIVE': ('008a4330', 'enemy initiative penalty (+own +1 %/lvl from description)', '(InitiativeOf)'),
    'HERO_SPEC_RIDERS': ('00a58de0', 'Lizard Bite ×0.5·(1.2+0.01·lvl)', '(LizardBites)'),
    'HERO_SPEC_MOUNTAIN_KING': ('008a6c40', 'Thunderclap rune refreshed at each turn start', '(BeginTurn)'),
    'HERO_SPEC_REANIMATOR': ('009789b0', 'Animate Dead +((lvl−1)/5)+1 spell power', '(HeroSpellPower)'),
    'HERO_SPEC_MASTER_OF_ELEMENTS': ('00a5a5a0, 00a61310', 'elementals on own stack death; summon spell power', T + 'AuditSpecTests2.MasterOfElementsSummonsOnOwnLosses'),
    'HERO_SPEC_SOULHUNTER': ('00a5b440, 00a61310', 'min(count, level) Manes when an enemy stack dies', T + 'AuditSpecTests2.SoulhunterRaisesManesFromDeadEnemies'),
    'HERO_SKILL_DEATH_TREAD': ('008a0ea0', 'siege attacker +4 speed', T + 'AuditSpecTests2.DeathTreadOnlyInSieges'),
    'HERO_SKILL_EXPLODING_CORPSES': ('0097b690, 0085eaa0', 'consumed corpse explodes: fire 1+SP to all adjacent units', '(UseAbility CONSUME_CORPSE)'),
    'HERO_SKILL_CONSUME_CORPSE': ('0097b690', 'mana = round(max(1, HP/k)), capped by missing mana', '(UseAbility CONSUME_CORPSE)'),
    'HERO_SKILL_FIRE_AFFINITY': ('00861630', 'always Fire Elementals; others by terrain', '(ResolveSpell)'),
    'SPELL_SUMMON_ELEMENTALS': ('00861630, 00861120', 'elemental kind from battlefield terrain (random among candidates)', T + 'AuditSpecTests2.GrassArenaSummonsAirOrEarthElementals'),
    'ABILITY_DOUBLE_ATTACK': ('008a0f90, 00a65260, 00a64e90', 'retaliation follows the first blow', T + 'AuditStrikeTests.DoubleAttackIsAnsweredAfterTheFirstBlow'),
    'SPELL_RUNE_OF_BERSERKING': ('008a0f90', '+1 strike (was ×2)', T + 'AuditStrikeTests.RuneOfBerserkingAddsOneStrike'),
    'ABILITY_BRUTALITY': ('00a65260', 'proc after the first strike extends the strike loop', '(PerformMeleeAttack)'),
    'HERO_SPEC_DEMON_HUNTER': ('008a2c20', 'rage gain +((lvl−1)/1)·2+10 when demons fight; Berserk/Power of Blood coefs rounded', '(AddRage)'),
    'SPELL_SPEC_DEMON_HUNTER': ('008a2c20', 'effect of HERO_SPEC_DEMON_HUNTER (rage bonus)', '(AddRage)'),
    'HERO_SKILL_POWER_OF_BLOOD': ('008a2c20', 'rage ×RPCoefPowerOfBlood rounded (was truncated)', '(AddRage)'),
    'UNICORN_HORN_BOW': ('008a34c0', 'army shooters have No Range Penalty', T + 'AuditArtifactTests.UnicornHornBowRemovesRangePenalty'),
    'ARTIFACT_SHAWL_OF_GREAT_LICH': ('008a34c0', 'negates enemy IMMUNITY_TO_MIND_CONTROL', T + 'AuditArtifactTests.ShawlOfTheGreatLichNegatesEnemyMindImmunity'),
}
MATCH = {
    'ABILITY_SHOOTER': ('008a0f90', 'ranged strikes per action 1'),
    'ABILITY_ENRAGED': ('008a3b90', 'on friendly non-summoned death: max(1, attack·killedPower/armyPower) int, permanent; enraged stacks get FriendlyStackKilledRPEnraged rage (fixed)'),
    'ABILITY_LARGE_SHIELD': ('00a5a900', 'ranged damage ×0.5 (own or adjacent Shield Other aura)'),
    'ABILITY_SHIELD_OTHER': ('00a5a900', 'aura type 2 gives adjacent allies the Large Shield reduction'),
    'ABILITY_MAGIC_PROOF_25': ('0097af50 (0097b281)', 'independent ×0.75 (disassembly: levels stack)'),
    'ABILITY_MAGIC_PROOF_50': ('0097af50 (0097b249)', 'independent ×0.5'),
    'ABILITY_MAGIC_PROOF_75': ('0097af50 (0097b265)', 'independent ×0.25'),
    'ABILITY_LIFE_DRAIN': ('00a59c60', 'LifeDrainIntensity of damage'),
    'ABILITY_BASH': ('00a59a90', 'proc via ability roll with Bash_ProcFactor'),
    'ABILITY_SHIELD_WALL': ('00a5a900', 'clamp(cells·perCell, 0, max) reduction'),
    'ABILITY_ARMORED': ('00a5a900', 'ignores Piercing Arrow / Rider Charge defence reduction'),
    'ABILITY_ANTI_GIANT': ('00a5a900', 'AntiGiantDefenceInc vs large'),
    'HERO_SKILL_DEFENCE': ('00a5a900', 'melee damage reduce Basic/Advanced/Expert from data'),
    'HERO_SKILL_EVASION': ('00a5a900', 'ranged ×(1−clamp(Evasion_DamageReduce,0,1))'),
    'HERO_SPEC_EVASIVE': ('00a5a900', 'ranged ×(1−clamp(base+lvl·perLevel,0,1))'),
    'ABILITY_DEMON_RAGED': ('008a2c20, 008a3b90', 'rage events and coefficients per exe (see AddRage)'),
    'ABILITY_ELVES_DOUBLE_SHOT': ('00a673c0', 'second shot re-rolls luck (EXE_FINDINGS)'),
    'HERO_SPEC_CALL_OF_THE_WASP': ('00976db0', 'Wasp Swarm damage ×(1+lvl·perLevel)'),
    'HERO_SKILL_LUCKY_STRIKE': ('00a5a170', 'second ability roll, never luck'),
    'SPELL_DEMON_RAGE_LEVEL_1': ('008a84e0', 'rage level thresholds RPForLevel1..3 (engine RageLevel)'),
    'SPELL_DEMON_RAGE_LEVEL_2': ('008a84e0', 'rage level thresholds RPForLevel1..3 (engine RageLevel)'),
    'SPELL_DEMON_RAGE_LEVEL_3': ('008a84e0', 'rage level thresholds RPForLevel1..3 (engine RageLevel)'),
}
GAP = {
    'HERO_SPEC_INFERNAL_MACHINE': 'test hero only (Tank_Hero); not shipped — N/A in practice',
    'HERO_SPEC_MIND_CLEANER': 'economy spec (gold, Haven discount); combat-side call at 00983790 not analysed',
    'SPELL_EFFECT_SUMMON_ELEMENTALS_EARTH': 'summon-variant effect id at 009a2350 (spell effect table); not mapped',
    'HERO_SKILL_COUNTERSPELL': 'exe 00978670: 2× cost and single use match; ATB after a counter is set to 0.2+0.1·Sorcery (and a Payback branch) — owner of that ATB change not confirmed, engine uses the normal cast reset',
    'HERO_SKILL_ELEMENTAL_BALANCE': 'enemy counter-summon on Summon Elementals (00861630): count/placement not ported',
}
NOT_USED_ABILITIES = set()

# ---- data ------------------------------------------------------------------------------------------
def rx(path, pat):
    try: return re.findall(pat, open(path, encoding='utf-8', errors='ignore').read())
    except OSError: return []
abilities_used = collections.Counter()
if EXTRACT:
    for f in glob.glob(EXTRACT + '/GameMechanics/Creature/**/*.xdb', recursive=True):
        for a in set(rx(f, r'<Item>(ABILITY_[A-Z0-9_]+)</Item>')): abilities_used[a] += 1
    specs_used = collections.Counter()
    for f in glob.glob(EXTRACT + '/MapObjects/**/*.xdb', recursive=True):
        for s in rx(f, r'<Specialization>(HERO_SPEC_[A-Z0-9_]+)</Specialization>'): specs_used[s] += 1
    spell_school = {}
    for f in glob.glob(EXTRACT + '/GameMechanics/Spell/Combat_Spells/**/*.xdb', recursive=True):
        pass
else:
    specs_used = collections.Counter()

E = {k: enum(k) for k in ('ability', 'skill', 'spec', 'spell')}
C = {'ability': calls('28c'), 'skill': calls('290'), 'spec': calls('294'), 'spell': calls('28')}

VISUAL = ('_HIT_EFFECT', '_FINISH_EFFECT', 'DISPEL_FAIL', '_CAST', '_START_EFFECT')
ADVENTURE_SPELLS = {'SPELL_SUMMON_BOAT', 'SPELL_DIMENSION_DOOR', 'SPELL_TOWN_PORTAL', 'SPELL_VISIONS', 'SPELL_SUMMON_CREATURES'}

def alias_refs(ident):
    """Effect IDs the engine implements under its own FX_* / ability / skill names."""
    for pre in ('SPELL_EFFECT_', 'SPELL_ABILITY_', 'SPELL_SKILL_', 'SPELL_SPEC_', 'SPELL_ARTFSET_', 'SPELL_'):
        if ident.startswith(pre):
            name = ident[len(pre):]
            break
    else:
        return 0
    cands = {name, name.replace('_OF_THE_', '_OF_'), name.replace('POWERFULL', 'POWERFUL'), name.rstrip('S'),
             name.replace('SEDUCE', 'SEDUCTION'), name.replace('WEAKNING', 'WEAKENING'), name.replace('LEVEL_', '')}
    n = 0
    for c in cands:
        for full in ('FX_' + c, 'ABILITY_' + c, 'HERO_SKILL_' + c, 'HERO_SPEC_' + c, 'SPELL_' + c):
            n += ours(full)
        n += src.count(c)
        if ident.startswith('SPELL_ARTFSET_'): n += src.count('ARTFSET_EFFECT_' + c.split('_')[0])
    return n

# Per-category audit fragments (docs/rules/audit/*.md, written by the parallel audit workstreams):
# rows "| `ID` | STATUS | exe | note | test |" override the computed status for that ID.
FRAG = {}
for _f in sorted(glob.glob(f'{TREE}/docs/rules/audit/*.md'), key=lambda f: [int(t) if t.isdigit() else t for t in re.split(r'(\d+)', f)]):   # natural order: sweep_10 after sweep_9
    for _line in open(_f, encoding='utf-8'):
        _p = [x.strip() for x in _line.split('|')]
        if len(_p) >= 7 and _p[1].startswith('`') and _p[2] in ('FIXED', 'MATCH', 'GAP', 'UNVERIFIED', 'N/A'):
            FRAG[_p[1].strip('`')] = (_p[2], _p[3], _p[4], _p[5])

def status(ident, kind, exe_sites, refs, used):
    if ident in FRAG: return FRAG[ident]
    if kind == 'spell' and ident not in FIXED and ident not in MATCH and ident not in GAP:
        if any(v in ident for v in VISUAL): return 'N/A', ' '.join(exe_sites[:4]), 'visual effect only (client)', ''
        if ident in ADVENTURE_SPELLS: return 'N/A', ' '.join(exe_sites[:4]), 'adventure-map spell (H5.Adventure)', ''
        if refs == 0 and alias_refs(ident) > 0: refs = 1
    if ident in FIXED: return 'FIXED', FIXED[ident][0], FIXED[ident][1], FIXED[ident][2]
    if ident in MATCH: return 'MATCH', MATCH[ident][0], MATCH[ident][1], ''
    if ident in GAP: return 'GAP', ' '.join(exe_sites[:4]), GAP[ident], ''
    if not used: return 'N/A', ' '.join(exe_sites[:4]), 'not used by shipped data', ''
    if ident in findings: return 'MATCH', ' '.join(exe_sites[:4]) or 'see EXE_FINDINGS', 'verified in EXE_FINDINGS.md', ''
    if refs == 0 and exe_sites: return 'GAP', ' '.join(exe_sites[:4]), 'tested by the exe, not referenced by the engine', ''
    if refs == 0: return 'N/A', '', 'no combat use found in exe or engine', ''
    return 'UNVERIFIED', ' '.join(exe_sites[:4]), 'implemented; not yet compared with the exe', ''

rows = collections.defaultdict(list)
for kind in ('ability', 'skill', 'spec', 'spell'):
    for ident, v in sorted(E[kind].items(), key=lambda kv: kv[1]):
        if ident.endswith('_NONE') or ident.endswith('_UNKNOWN'): continue
        exe_sites = combat_fns(C[kind].get(v, ()))
        refs = ours(ident)
        if kind == 'ability': used = abilities_used[ident] > 0 or ident in FIXED or refs > 0
        elif kind == 'spec': used = specs_used[ident] > 0 or not EXTRACT
        else: used = True
        if kind == 'spell' and not exe_sites and refs == 0 and ident not in FIXED: continue
        if kind == 'skill' and not exe_sites and refs == 0 and ident not in FIXED and ident not in GAP: continue
        st = status(ident, kind, exe_sites, refs, used)
        rows[kind].append((ident,) + st)
for ident in ('ARTIFACT_RING_OF_MACHINE_AFFINITY', 'RING_OF_MAGI', 'UNICORN_HORN_BOW', 'ARTIFACT_SHAWL_OF_GREAT_LICH'):
    rows['artifact'].append((ident,) + status(ident, 'artifact', [], ours(ident), True))
# Week-of-the-month effects (MoonWeek ids) and further artifacts exist only as fragment rows.
_listed = {r[0] for k in rows for r in rows[k]}
for ident in sorted(i for i in FRAG if i not in _listed):
    rows['week' if ident.startswith('WEEK_OF_') else 'artifact'].append((ident,) + FRAG[ident])

MECH = [
    ('Damage pipeline (base roll, attack/defence factor uncapped, truncation)', 'MATCH', '00a597a0, 00a58de0, 00a5c5a0, 00a5a900', 'EXE_FINDINGS'),
    ('Range penalty (squared distance vs (width/2)²)', 'MATCH', '00a5c5a0, 00b3c690', 'EXE_FINDINGS'),
    ('Luck: clamp ±5, 10 %/point, once per attack', 'MATCH', '00a6b820, 008a3ef0, 00a5a170', 'EXE_FINDINGS'),
    ('Morale: clamp ±5 (00a6bd10), 10 %/point, ATB 0.5 good/bad (00a6cf90/00a6cd80)', 'MATCH', '00a6bd10, 00a6cf90, 00a6cd80', 'this audit'),
    ('Ability trigger chance: integer ratio, ProcFactor as exponent, Range(0,100) roll, Soldier\'s Luck double/second roll', 'FIXED', '00a59990, 00a5a170, 00a5d1b0', 'AuditProcChanceTests'),
    ('Retaliation verdict: only Expert Blind blocks, decided before the blow', 'FIXED', '00a5fee0', 'AuditAbilityTests.OnlyExpertBlindStopsRetaliation'),
    ('Retaliation reset at own turn start / Wait / Defend', 'MATCH', '008aa960, 008a1990, 008a6be0', 'EXE_FINDINGS'),
    ('Retaliation timing inside the strike loop', 'FIXED', '00a65260, 00a64e90, 00a646b0', 'AuditStrikeTests'),
    ('Strikes per action (Double Attack, Berserking, Double Shot)', 'FIXED', '008a0f90', 'AuditStrikeTests'),
    ('War machine shots per action', 'FIXED', '00a75bf0', 'WarMachineAuditTests'),
    ('Spell mana cost chain', 'FIXED', '00a34740, 009781e0', 'AuditSpecTests'),
    ('Creature dynamic abilities (HasAbility override)', 'FIXED', '008a34c0', 'AuditSpecTests'),
    ('Stack death summons (Master of Elements, Soulhunter)', 'FIXED', '00a61310', 'AuditSpecTests2'),
    ('Terrain elementals', 'FIXED', '00861120', 'AuditSpecTests2'),
    ('Obstacle generator', 'MATCH', '004b3860', 'EXE_FINDINGS'),
    ('Siege: catapult, towers, walls, earthquake, moat', 'MATCH', '00b3b7d0, 00bfdfb0, 00a5c5a0, 0097d7b0', 'EXE_FINDINGS (moat field CArenaMoat: GAP, combat_sweep_2.md)'),
    ('ATB continuous time (ATBMin/Max dead code)', 'MATCH', 'EXE_FINDINGS', 'EXE_FINDINGS'),
    ('Spell damage rounding / empowered ×1.5', 'MATCH', '00976b90', 'EXE_FINDINGS'),
    ('Surrender / retreat conditions and ransom', 'FIXED', '004d9e20, 004d9780, 004da8d0, 004da9a0', 'AuditRetreatTests (combat_heroes.md, combat_sweep_2.md)'),
    ('Battle-end experience (winner)', 'FIXED', '00af3430, 004d9c60, 004d99b0, 00b4bbb0, 00bcdc60', 'AuditBattleExpTests (combat_heroes_skills_h.md)'),
    ('Tactics phase columns (third column only when exactly one side has Tactics, Flagbearer of Darkness cancels)', 'FIXED', '004d8c10', 'AuditHeroTests.TacticsColumnsFollowTheOriginalRule (combat_heroes.md); deploy zones DeploymentAxisTests'),
]

counts = collections.Counter()
for kind in rows:
    for r in rows[kind]: counts[r[1]] += 1
for m in MECH: counts[m[1]] += 1

lines = ['# Combat audit ledger (H5_Game.exe vs H5.Combat)', '',
         'Generated by `tools/re/audit/ledger.py` from the exe call-site maps (`tools/re/audit/out/vc_*.txt`:',
         'every `HasAbility`/`HasSkill`/`HasSpecialization`/`GetEffect` virtual call in the exe), the enum dumps,',
         'the shipped game data and the engine sources. Addresses are VAs in the GOG 3.1 `H5_Game.exe`.', '',
         'Status: **FIXED** compared and corrected (test named) · **MATCH** compared and identical (this audit or',
         '`EXE_FINDINGS.md`) · **GAP** known mismatch / missing · **UNVERIFIED** implemented, exe tests it, not yet',
         'compared line by line · **N/A** unused by shipped data / test content.', '',
         '## Summary', '', '| Status | Count |', '|---|---|']
for k in ('FIXED', 'MATCH', 'GAP', 'UNVERIFIED', 'N/A'): lines.append(f'| {k} | {counts[k]} |')
lines += ['', '## Core mechanics', '', '| Mechanic | Status | Exe | Evidence |', '|---|---|---|---|']
for m in MECH: lines.append(f'| {m[0]} | {m[1]} | {m[2]} | {m[3]} |')
titles = {'ability': 'Creature abilities', 'skill': 'Hero skills and perks', 'spec': 'Hero specialisations',
          'spell': 'Spells and spell effects', 'artifact': 'Artifacts (combat effects found in this audit)',
          'week': 'Week of the month (combat effects)'}
for kind in ('ability', 'skill', 'spec', 'spell', 'artifact', 'week'):
    lines += ['', f'## {titles[kind]}', '', '| ID | Status | Exe functions | Original behaviour / note | Regression test |', '|---|---|---|---|---|']
    for ident, st, exe, note, test in rows[kind]:
        lines.append(f'| `{ident}` | {st} | {exe} | {note} | {test} |')
open(f'{TREE}/docs/rules/AUDIT_COMBAT.md', 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print(dict(counts))
