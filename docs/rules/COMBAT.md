# Combat rules engine (`src/H5.Combat`)

Deterministic reimplementation of HoMM5 Tribes of the East 3.1 tactical combat. All numbers are read
from the original data (`GameDatabase`, `DefaultStats.xdb`) when the data has them; formulas without a data
source cite the fan manual (`FanDocuments/Heroes5_Manual_3.0.pdf`, page numbers in code comments).

## API (for the server / client)

```csharp
var db = GameDatabase.Load(PakFileSystem.OpenInstall(GameInstall.Locate()));
var state = BattleSetup.CreateDuel(db, presetA, presetB, seed);        // or BattleSetup.Create(db, armyA, armyB, seed)
var battle = new Battle(db, state);
var opening = battle.Begin();                                           // tactics phase (or combat if skipped)
ApplyResult r = battle.Apply(new MoveCmd(unitId, cell) { Side = 0 });  // r.Ok / r.Error / r.Events
LegalActions? la = battle.GetLegalActions();                            // for highlighting & AI
int? side = battle.SideToAct;                                           // who must send the next command
string hash = state.ComputeHash();                                      // desync check after each command
string json = state.Serialize(); BattleState.Deserialize(json);         // snapshots / reconnect / replays
IBattleAI ai = new GreedyBattleAI(); BattleCommand cmd = ai.Decide(battle, side);
```

* Commands (`$t`): `deploy`, `end_tactics`, `move`, `attack` (target + optional from-cell), `shoot`, `wait`, `defend`,
  `cast` (hero = `Actor.Hero(side)` or caster creature; unit target and/or cell), `ability` (creature or hero ability id),
  `hero_attack`, `surrender`. `Side` must be filled by the server from the connection.
* Events (`$t`): `turn`, `moved`, `teleported`, `attack`, `damaged`, `healed`, `died`, `summoned`, `spell`, `resisted`,
  `effect_added`/`effect_removed`, `ability`, `morale`, `luck`, `atb`, `mana`, `waited`, `defended`, `skipped`, `field`,
  `phase`, `deployed`, `ended`. `MessageJson` (de)serialises both with camelCase + `$t`, matching H5.Net.
* Illegal commands return `Ok=false` and never mutate state (validation precedes mutation).
* All randomness uses `BattleState.Rng` (xoshiro256**, serialised in the state).

## Implemented

* Battlefield 12x10 (x = attacker→defender axis, as in the Lua combat API), 1x1 and 2x2 creatures, obstacles
  (random per `combat/Obstacles` until arena layouts are decoded), war machines just outside the grid.
* ATB (p.309-313): start 0..0.25, rate = Initiative/10, wait → 0.5, defend → 0 (+30% defence, ×2 with Stand Your Ground),
  good morale → 0.5 after acting, bad morale → 0.5 and skip, hero initiative 10, spell durations in initiative-10 turns,
  Sorcery/mass-spell hero ATB resets, Swift Mind, Divine Guidance, Teleport Assault, Wasp Swarm/Lightning/Ice ATB effects.
* Movement: 100/141 octile costs vs Speed×100, Dijkstra, flyers, entangle, mines/firewalls on the path.
* Damage (p.301-302): Count × random(min,max) × A/D factor (DefaultStats caps), range/melee penalties, Archery/Evasion,
  Offence/Defence skills, Training on retaliation, luck ×2/×0.5 (Elven Luck, Nature's Luck, Absolute Protection),
  Retribution, Jousting, Shield Wall, Rider Charge, Precise Shot, Large Shield / Shield Allies, Deflect Missile, Deep Freeze.
* Retaliation once per own turn (unlimited ability), Preparation, no-retaliation abilities, ranged retaliation,
  Battle Frenzy, Fierce Retaliation.
* Hero strike table (DefaultStats HeroDamageLevel1/30, p.303), Excruciating/Stunning/Powerful Blow, Suffering Strike.
* Morale/luck with faction relations (p.313-314), Leadership/Luck, auras (Frightful, Bravery, Festering, Death Stare).
* Ability trigger chance formula (p.315) with DefaultStats ProcFactors and Soldier's Luck.
* 147 of 163 creature abilities (see `Ab` ids in `Ids.cs`), including patterns (three/six-headed, spray, war dance, whirlwind,
  cross, breath attacks, death cloud, magic line, chain shot, crushing blow, champion charge), on-hit effects, reactive
  abilities (cowardice, maneuver, acid blood, fire/magma shield), activated abilities, gating, Blood Rage from the
  DefaultStats DemonRage table.
* Combat spells of all schools with xdb damage/duration per mastery, mass spells via Master-of perks, runes, warcries,
  resistance/immunity/protection (multiplicative), Irresistible Magic, shatter/weaken/corrupt/detain/seal skills.
* Hero perks used by duel presets (Offence/Defence/Leadership/Luck/Sorcery trees, racial abilities), artifact stats,
  artifact set bonuses, hero specialisations (creature families parsed from the original specialisation text).
* Greedy one-ply AI (`GreedyBattleAI`) that simulates candidate commands on cloned states.

## Not implemented yet

* Hero: Consume Artifact (no creature artifacts in duels), Tremors (siege only), Power Bargain.
* Sieges (14x14 field, walls, towers, moat), catapult, summoned elemental type by terrain.
* Exact original hero / war machine cells, ATB integer timing.

## Confirmed against the executable

See `EXE_FINDINGS.md`: damage pipeline (uncapped A/D factor, 1-3 rolls / 3-roll average, truncation with 1e-4,
pattern share), luck once per attack, Absolute Protection, range penalty metric, retaliation reset, Storm Wind and
Fog Veil data values, Cavalry Commander, Evasive / Forest Guardian, inclusive RNG ranges, obstacle generator,
spell base damage rounding and Empowered ×1.5, Magic Mirror / Spell Twister, Treachery, Taunt, Howl, Resurrect Allies.
