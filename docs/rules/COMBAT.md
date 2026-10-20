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
* Movement (exe FUN_00b5f0d0): 8-connected Dijkstra, steps 2 orthogonal / 3 diagonal vs Speed×2, no corner rule, flyers on an empty board; entangle, mines/firewalls on the path.
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

* Hero: Power Bargain. Summoned elemental type by terrain.
* Exact original hero / war machine cells (not located in the exe).
* Siege: the ×1.5 tower damage condition of `00bcdf10` (ids 0x49/0x4b from the battle object) is unidentified;
  town building combat bonuses (Tavern morale, grail luck, Infernal Loom, …) belong to the adventure layer and are
  not applied by `SiegeSetup` yet.

## Sieges (`Siege.cs`, `BattleSetup.Create(..., siege: new SiegeSetup { … })`)

* Layout per town type from data (`GameDatabase.TownCombat`), hit points from `ArenaBuildingsStats`
  (base + fortification bonus; Stoneworks ×1.5, defence specialisation +50), moats from `TownTypesInfo`.
* Fort = walls + gate, Citadel = + moat + central tower, Castle = + side towers; 14×14 field when fortified,
  plain field otherwise. The attacker's Catapult joins automatically.
* Gate blocks attackers, opens for defenders; destroyed gate leaves only its end tiles blocked; destroyed walls
  are passable; destroyed towers keep their rubble tiles (data `blockedTilesForDestroyed`).
* Catapult: accuracy 20 % without hero, else (War Machines mastery + 2) × 10 %, +2 % per 3 levels (rounded up) for
  HERO_SPEC_WAR_MACHINES; a miss (roll 1..100 above the chance) hits a random other target, possibly a ruin;
  luck applies; damage range from `WarMachines.xdb` by mastery; shots 1 (+Catapult, +Triple Catapult); manual
  aiming with the Catapult perk (`UseAbilityCmd(catapult, "CATAPULT_SHOT", Cell: any tile of the building)`).
  The central tower is a valid target only after both side towers fall.
* Towers: defender actors with Initiative 10 that shoot the most valuable visible attacker each turn for
  70 (central) / 50 (side) × 1.25 (towers specialisation) × town level ratio, ignoring Defence and Evasion;
  they cannot be attacked by creatures, heroes or spells, only by the catapult and Earthquake.
* Shots from outside the town at targets inside across an intact fortification are halved (also for creatures
  with No Range Penalty).
* Moat: walking attackers stop in the first moat tile and take its damage (MoatMaxDamage × town level ratio),
  then the town's moat spells: Sylvan roots (50 %), Dungeon poison 28, Necropolis Curse + Disrupting Ray,
  Academy single-use land mines (Blind), Fortress one of six spell / rune pairs (p.323). Flyers trigger it when
  landing in it.
* Earthquake: each standing structure takes 50 × Range(duration/50, damage/50); Tremors also hits every creature
  inside the walls for round(10 + 5 × SP) and lowers its ATB by 0.1.
* Fortress Guard Post: `SiegeSetup.GuardPostDefenders` Defenders at the DefaultStats cell.

## Confirmed against the executable

See `EXE_FINDINGS.md`: damage pipeline (uncapped A/D factor, 1-3 rolls / 3-roll average, truncation with 1e-4,
pattern share), luck once per attack, Absolute Protection, range penalty metric, retaliation reset, Storm Wind and
Fog Veil data values, Cavalry Commander, Evasive / Forest Guardian, inclusive RNG ranges, obstacle generator,
spell base damage rounding and Empowered ×1.5, Magic Mirror / Spell Twister, Treachery, Taunt, Howl, Resurrect Allies.
