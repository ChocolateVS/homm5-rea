using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Encounters, combat hand-off to H5.Combat, and applying battle results.
public sealed partial class Adventure
{
    // ------------------------------------------------------------------ army power

    /// <summary>
    /// Army power for diplomacy and stack splitting (fan manual p.320): sum of creature Power × count,
    /// each stack scaled by (1 + C·Morale)(1 + C·Luck) with C = 0.0173 for positive and 0.0122 for
    /// negative values (also DefaultStats AI/Adventure Creature{Good,Bad}{Morale,Luck}Coef).
    /// TODO(exe): artifact initiative modifier (1 + ΔInitiative/10).
    /// </summary>
    public double ArmyPower(IEnumerable<Stack> army, HeroState? hero)
    {
        double total = 0;
        int morale = hero != null ? HeroMorale(hero) : 0, luck = hero != null ? HeroLuck(hero) : 0;
        static double F(int v) => 1 + (v > 0 ? 0.0173 : 0.0122) * v;
        foreach (var s in army)
            if (Core.Creatures.TryGetValue(s.Creature, out var c))
                total += (double)c.Power * s.Count * (hero != null ? F(morale) * F(luck) : 1);
        return total;
    }

    int HeroMorale(HeroState h) =>
        (int)h.MasteryOf("HERO_SKILL_LEADERSHIP") + h.NextBattleMorale + h.ScriptMorale +
        h.Artifacts.Sum(a => Core.Artifacts.TryGetValue(a, out var d) ? d.Morale : 0);

    int HeroLuck(HeroState h) =>
        (int)h.MasteryOf("HERO_SKILL_LUCK") + h.NextBattleLuck + h.ScriptLuck +
        h.Artifacts.Sum(a => Core.Artifacts.TryGetValue(a, out var d) ? d.Luck : 0);

    // ------------------------------------------------------------------ neutral encounters

    static int MoodIndex(string mood) => mood switch
    {
        "MONSTER_MOOD_FRIENDLY" => 0, "MONSTER_MOOD_AGGRESSIVE" => 1, "MONSTER_MOOD_HOSTILE" => 2, "MONSTER_MOOD_WILD" => 3, _ => 1,
    };

    /// <summary>
    /// Join / flee / fight decision for neutral stacks (exe FUN_008e1ad0). Diplomacy level d = 1 + (Crown of Leadership)
    /// when the hero has Diplomacy (hero vcall 0x174(0x1e) / 0x310), else 0; mood coefficient M = 0.5 / 1 / 1.5 / 2 − 0.2·d
    /// (008da210); threshold T = rand(40..100) · 0.1 · M (008e7840 inclusive); R = hero / creature strength (004eabb0).
    /// R ≥ T: flee or join for free; T − 3/M ≤ R &lt; T: fight or join for money; below: fight. The join roll is
    /// rand(1..100) ≤ chance (008dc380). A money join costs the army's price × max(0, 3 − d) and is offered only when
    /// affordable, else the monsters fight; a zero price (Week of Diplomacy, d ≥ 3) joins for free.
    /// The join offer (008dd0d0, <see cref="JoinOffer"/>) comes first: result 0 → the free join box at once, 1 → fight,
    /// 2 → the check above (mixed armies included).
    /// TODO(exe): the Courtier factor on R (hero vcall 0x190(0x3b), RPGStats +0x928 per level).
    /// </summary>
    void StartNeutralEncounter(HeroState h, MapObject mon)
    {
        if (Refused(h, -1, mon.Index)) return;
        var os = S.Obj(mon.Index);
        var guards = os.Guards.Select(g => new Stack(g.Creature, g.Count)).ToList();
        var offer = JoinOffer(h, mon);
        if (offer.Mode == 0) { OfferJoin(h, mon, offer.Stacks, paid: false); return; }
        if (offer.Mode == 1) { StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false); return; }

        int diplomacy = DiplomacyLevel(h);
        double moodCoef = new[] { 0.5, 1.0, 1.5, 2.0 }[MoodIndex(mon.Mood)];
        if (diplomacy > 0) moodCoef -= 0.2 * diplomacy;
        double ratio = ArmyPower(h.Army.Live(), h) / Math.Max(1, ArmyPower(guards, null));
        double threshold = S.Rng.Range(40, 100) * 0.1 * moodCoef;
        bool fleeCase = ratio >= threshold;
        if (!fleeCase && threshold - 3.0 / moodCoef > ratio) { StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false); return; }
        int chance = ChanceToJoin(h, guards, mon.Mood, diplomacy);
        if (S.Rng.Range(1, 100) <= chance)
        {
            int mul = diplomacy > 0 ? Math.Max(0, 3 - diplomacy) : 3;
            if (fleeCase || mul == 0 || S.MoonWeek == "WEEK_OF_DIPLOMACY") { OfferJoin(h, mon, offer.Stacks, paid: false); return; }
            if (JoinPrice(offer.Stacks, mul) <= (PlayerByIndex(h.Owner)?.Purse[Purse.Gold] ?? 0)) { OfferJoin(h, mon, offer.Stacks, paid: true, priceMul: mul); return; }
            // 008e210d: not enough money → the monsters fight.
        }
        if (fleeCase)
        {
            // 008e1a80: CFleeFightDialogLogEntry — Pursue (fight) or Leave them (CMonsterFleeCmd).
            S.Choice = new PendingChoice { Kind = "monster_flee", Hero = h.Id, Object = mon.Index, Creatures = guards };
            Emit(new ChoiceOffered(S.Choice.Kind, h.Id, mon.Index, 0, 0));
        }
        else StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false);
    }

    /// <summary>008e1ad0: hero vcall 0x174(0x1e) (Diplomacy) &gt; 0 → 1 + (Crown of Leadership, vcall 0x310 &gt; 0), else 0.</summary>
    public int DiplomacyLevel(HeroState h) =>
        h.HasPerk("HERO_SKILL_DIPLOMACY") ? 1 + (h.HasArtifact("ARTIFACT_CROWN_OF_LEADER") ? 1 : 0) : 0;

    int JoinPrice(List<Stack> guards, int mul) =>
        guards.Sum(g => Core.Creatures.TryGetValue(g.Creature, out var c) ? c.Cost.Gold * g.Count : 0) * mul;

    /// <summary>
    /// CMonsterFleeCmd (execute 0042ad40): the hero lets the monsters go and gains half of Σ count · (creature Exp, or
    /// Health when Exp is 0; FUN_0045a600 / 004baad0) through +0x198, after world vf 0x1b4 (0x0099e500): × HeroesExpCoef / 100
    /// unless the coefficient is 100. That command is the only caller of the world's vf 0x1b4 (SetHeroesExpCoef).
    /// </summary>
    /// <summary>World vf 0x1b4 (0x0099e500): coef · exp / 100 (integer), unchanged at 100.</summary>
    public long ApplyHeroesExpCoef(long exp) => S.HeroesExpCoef != 100 ? S.HeroesExpCoef * exp / 100 : exp;

    /// <summary>World vf 0x1b0 (0x0099e5b0): stores the percentage clamped to 0..1000.</summary>
    public void SetHeroesExpCoef(int percent) => S.HeroesExpCoef = Math.Clamp(percent, 0, 1000);

    void LetMonstersFlee(HeroState h, int monster)
    {
        long total = 0;
        foreach (var g in S.Obj(monster).Guards)
            if (Core.Creatures.TryGetValue(g.Creature, out var c)) total += (long)g.Count * (c.Exp != 0 ? c.Exp : c.Health);
        RemoveObject(monster);
        Emit(new ObjectVisited(h.Id, monster, "monster_fled"));
        GainExperience(h, ApplyHeroesExpCoef(total / 2), modifiersApplied: true);
    }

    /// <summary>
    /// Chance to join (008dc380) over the monster's stacks with a count: mood 10 / 5 / 0 / −5; +5 when every stack has
    /// one alignment and it is the hero's town's (hero vfunc 0x254); +5 when every stack has one town and it is the
    /// hero's; + 5 · (stacks of a creature the hero's army holds, 0045b170) / stacks; + 5 · (stacks the army holds a
    /// upgrade of, 0045b180) / stacks (integer divisions); + 10 · diplomacy level; +20 when every stack has one base
    /// creature (004baf60, up to 3 steps) matching the hero's specialisation (vfunc 0x298 → 004baf90, not modelled).
    /// </summary>
    int ChanceToJoin(HeroState h, IReadOnlyList<Stack> stacks, string mood, int diplomacy)
    {
        int chance = new[] { 10, 5, 0, -5 }[MoodIndex(mood)];
        static int Align(string town) => town is "TOWN_ACADEMY" or "TOWN_HEAVEN" or "TOWN_PRESERVE" or "TOWN_FORTRESS" ? 1
            : town is "TOWN_DUNGEON" or "TOWN_INFERNO" or "TOWN_NECROMANCY" or "TOWN_STRONGHOLD" ? 2 : 0;
        var defs = stacks.Where(s => s.Count > 0).Select(s => Core.Creatures.GetValueOrDefault(s.Creature))
            .Where(d => d != null).Select(d => d!).ToList();
        int n = defs.Count, same = 0, related = 0;
        var army = h.Army.Live().Select(s => s.Creature).ToHashSet();
        foreach (var cd in defs)
        {
            if (army.Contains(cd.Id)) same++;
            if (cd.Upgrades.Any(army.Contains)) related++;   // 0045b180: the army holds one of its Upgrades
        }
        if (n > 0)
        {
            if (defs.Select(d => Align(d.Town)).Distinct().Count() == 1 && Align(defs[0].Town) == Align(h.Town)) chance += 5;
            if (defs.Select(d => d.Town).Distinct().Count() == 1 && defs[0].Town == h.Town) chance += 5;
            if (same > 0) chance += same * 5 / n;
            if (related > 0) chance += related * 5 / n;
        }
        if (diplomacy > 0) chance += 10 * diplomacy;
        // TODO(exe): +20 for the hero's creature specialisation (vfunc 0x298 → 004baf90; no specialisation creature table yet).
        return chance;
    }

    /// <summary>
    /// The join offer (008dd0d0): Mode = the monster's join mode for the hero's player (interface vfunc 0xc = 004ee9a0,
    /// a per-player array, 2 by default; the map's Courage ALWAYS_JOIN 0 / ALWAYS_FIGHT 1 / CAN_FLEE_JOIN 2); Stacks =
    /// the monster's stacks. A HERO_SPEC_HEIR_OF_UNDEAD hero (vfunc 0x190(0x1d)) facing only Necropolis creatures (town
    /// 7, 004ba910) gets Mode 0 and each count = ((level + 25) · count · 2 + 50) / 100. Herald of Death (skill 0x66,
    /// vfunc 0x174 &gt; 0) turns each creature into its undead counterpart (004bbe50). Fits (result +4, passed to the join
    /// box) = the army takes every stack: a creature the army does not hold uses up one empty slot.
    /// </summary>
    public (int Mode, List<Stack> Stacks, bool Fits) JoinOffer(HeroState h, MapObject mon)
    {
        int mode = mon.Courage switch { "MONSTER_COURAGE_ALWAYS_JOIN" => 0, "MONSTER_COURAGE_ALWAYS_FIGHT" => 1, _ => 2 };
        var stacks = S.Obj(mon.Index).Guards.Select(g => new Stack(g.Creature, g.Count)).ToList();
        bool allUndead = stacks.All(s => Core.Creatures.TryGetValue(s.Creature, out var c) && c.Town == "TOWN_NECROMANCY");
        if (h.Specialization == "HERO_SPEC_HEIR_OF_UNDEAD" && allUndead)
        {
            mode = 0;
            foreach (var s in stacks) s.Count = (int)((((long)h.Level + 25) * s.Count * 2 + 50) / 100);
        }
        if (h.HasPerk("HERO_SKILL_HERALD_OF_DEATH"))
            foreach (var s in stacks) s.Creature = HeraldOfDeathCreature(s.Creature);
        int free = h.Army.Count(s => s == null);
        var held = h.Army.Where(s => s != null).Select(s => s!.Creature).ToHashSet();
        bool fits = true;
        foreach (var s in stacks)
        {
            if (held.Contains(s.Creature)) continue;
            if (free <= 0) { fits = false; break; }
            free--;
            held.Add(s.Creature);
        }
        return (mode, stacks, fits);
    }

    static readonly string[,] HeraldUndead =
    {
        { "CREATURE_SKELETON", "CREATURE_SKELETON_ARCHER", "CREATURE_SKELETON_WARRIOR" },
        { "CREATURE_WALKING_DEAD", "CREATURE_ZOMBIE", "CREATURE_DISEASE_ZOMBIE" },
        { "CREATURE_MANES", "CREATURE_GHOST", "CREATURE_POLTERGEIST" },
        { "CREATURE_VAMPIRE", "CREATURE_VAMPIRE_LORD", "CREATURE_NOSFERATU" },
        { "CREATURE_LICH", "CREATURE_DEMILICH", "CREATURE_LICH_MASTER" },
        { "CREATURE_WIGHT", "CREATURE_WRAITH", "CREATURE_BANSHEE" },
        { "CREATURE_BONE_DRAGON", "CREATURE_SHADOW_DRAGON", "CREATURE_HORROR_DRAGON" },
    };

    /// <summary>
    /// 004bbe50: a creature without ABILITY_UNDEAD / ABILITY_ELEMENTAL (00b61ba0 0xa / 0xc) of tier 1..7 (def +0x8c)
    /// becomes the table entry (00f2e734 + tier · 0xc + grade · 4) of its tier and grade (004bb980: 0 without a base
    /// creature (+0x100), else 1 + its index in its own Upgrades list (+0x104), 0 when absent).
    /// </summary>
    public string HeraldOfDeathCreature(string creature)
    {
        if (!Core.Creatures.TryGetValue(creature, out var c) || c.Has("ABILITY_UNDEAD") || c.Has("ABILITY_ELEMENTAL") || c.Tier < 1 || c.Tier > 7)
            return creature;
        int grade = 0;
        if (c.BaseCreature is { Length: > 0 } b && b != "CREATURE_UNKNOWN")
        {
            int i = c.Upgrades.IndexOf(creature);
            grade = i >= 0 ? i + 1 : 0;
        }
        return grade <= 2 ? HeraldUndead[c.Tier - 1, grade] : creature;
    }

    void OfferJoin(HeroState h, MapObject mon, List<Stack> guards, bool paid, int priceMul = 3)
    {
        int gold = 0;
        if (paid)
            gold = guards.Sum(g => Core.Creatures.TryGetValue(g.Creature, out var c) ? c.Cost.Gold * g.Count : 0) * priceMul;
        S.Choice = new PendingChoice { Kind = paid ? "join_paid" : "join_free", Hero = h.Id, Object = mon.Index, Gold = gold, Creatures = guards };
        Emit(new ChoiceOffered(S.Choice.Kind, h.Id, mon.Index, gold, 0));
    }

    // ------------------------------------------------------------------ battles

    void StartBattle(HeroState attacker, int defenderHero, int defenderObject, Tile where, List<Stack> defenderArmy, bool siege)
    {
        // Neutral monsters are split into 2–4 stacks by relative power (p.321).
        if (defenderHero < 0 && defenderObject >= 0 && O(defenderObject).Kind == ObjectKind.Monster)
            defenderArmy = SplitNeutrals(attacker, defenderArmy);
        S.Battle = new PendingBattle
        {
            AttackerHero = attacker.Id, DefenderHero = defenderHero, DefenderObject = defenderObject,
            Where = where, DefenderArmy = defenderArmy, Siege = siege,
        };
        Emit(new BattleRequested(attacker.Id, defenderHero, defenderObject, where, defenderArmy.Select(s => new ArmySlot(s.Creature, s.Count)).ToList()));
    }

    /// <summary>
    /// Stack splitting (p.321): 4 stacks if power ratio &lt; 0.5, 3 if &lt; 1, else 2; then 30% +1, 30% −1;
    /// finally a 50% chance that one stack is upgraded. Only single-creature armies are split.
    /// </summary>
    List<Stack> SplitNeutrals(HeroState attacker, List<Stack> army)
    {
        if (army.Count != 1) return army;
        var s = army[0];
        double ratio = ArmyPower(attacker.Army.Live(), attacker) / Math.Max(1, ArmyPower(army, null));
        int n = ratio < 0.5 ? 4 : ratio < 1 ? 3 : 2;
        int r = S.Rng.Next(100);
        if (r < 30) n++; else if (r < 60) n--;
        n = Math.Clamp(n, 1, Math.Min(7, s.Count));
        var result = new List<Stack>();
        for (int i = 0; i < n; i++)
        {
            int count = s.Count / n + (i < s.Count % n ? 1 : 0);
            if (count > 0) result.Add(new Stack(s.Creature, count));
        }
        if (S.Rng.Next(2) == 0 && Core.Creatures.TryGetValue(s.Creature, out var cd) && cd.Upgrades.Count > 0 && result.Count > 0)
            result[S.Rng.Next(result.Count)].Creature = cd.Upgrades[S.Rng.Next(cd.Upgrades.Count)];
        return result;
    }

    /// <summary>
    /// Builds the H5.Combat battle for the pending encounter. Side 0 is the attacking hero, side 1 the
    /// defender (hero, garrison or neutrals). Next-battle morale/luck bonuses are applied to the heroes.
    /// </summary>
    public BattleState CreatePendingBattle(ulong seed)
    {
        var pb = S.Battle ?? throw new InvalidOperationException("no pending battle");
        var att = S.Hero(pb.AttackerHero)!;
        var attackerSetup = new ArmySetup { Hero = ToInstance(att), Army = att.Army.ToSlots() };
        var defHero = pb.DefenderHero >= 0 ? S.Hero(pb.DefenderHero) : null;
        var defenderSetup = new ArmySetup { Hero = defHero != null ? ToInstance(defHero) : null, Army = pb.DefenderArmy.Select(s => new ArmySlot(s.Creature, s.Count)).ToList() };
        // Town assault: fortifications, moat, towers, Guard Post, and town combat bonuses.
        var town = pb.DefenderObject >= 0 ? S.Town(pb.DefenderObject) : null;
        TownCombatBonus? defBonus = null, attBonus = null;
        if (town != null)
        {
            (defBonus, attBonus) = TownBonuses(town);
            if (defenderSetup.Hero is { } dh) { dh.SpellPower += defBonus.HeroSpellPower; dh.Knowledge += defBonus.HeroKnowledge; }
        }
        var state = BattleSetup.Create(Core, attackerSetup, defenderSetup, seed, siege: town != null ? SiegeFor(town) : null,
            arena: town == null ? SeaBattleArena(att, pb, seed) : null, week: S.MoonWeek);
        state.Heroes[0].Morale += att.NextBattleMorale; state.Heroes[0].Luck += att.NextBattleLuck;
        if (defHero != null) { state.Heroes[1].Morale += defHero.NextBattleMorale; state.Heroes[1].Luck += defHero.NextBattleLuck; }
        if (defBonus != null) AddSide(state.SideBonuses[1], defBonus);
        if (attBonus != null) AddSide(state.SideBonuses[0], attBonus);
        foreach (var (side, h) in new[] { (0, (HeroState?)att), (1, defHero) })
        {
            if (h == null) continue;
            var (ga, gl) = GrailHeroBonus(h.Owner);
            state.Heroes[side].Attack += ga;
            state.Heroes[side].Luck += gl;
        }
        // DenyAIHeroFlee & co. (exe FUN_00c972d0): the AI hero neither flees nor surrenders. Our combat AI has
        // no flee decision of its own; the denial is enforced through the side's retreat gate (only the AI acts there).
        if (AiFleeDenied(att, defHero)) state.RetreatDenied[0] = true;
        if (defHero != null && AiFleeDenied(defHero, att)) state.RetreatDenied[1] = true;
        // CCombatArmy::CanRetreat (004d9e20) reason 8: the army's player owns no town with a tavern.
        for (int side = 0; side < 2; side++)
            if (BattleSideOwner(pb, side) is var owner && owner >= 0 && !OwnsTavernTown(owner)) state.NoTavernTown[side] = true;
        return state;
    }

    /// <summary>The player owning a side of the pending battle (0 attacking hero, 1 defending hero or town), -1 for none.</summary>
    public int BattleSideOwner(PendingBattle pb, int side)
    {
        if (side == 0) return S.Hero(pb.AttackerHero)?.Owner ?? -1;
        if (pb.DefenderHero >= 0 && S.Hero(pb.DefenderHero) is { } dh) return dh.Owner;
        return pb.DefenderObject >= 0 && S.Town(pb.DefenderObject) is { } t ? t.Owner : -1;
    }

    /// <summary>CanRetreat's town loop (004d9e20): any town of the player (player vf 0xc4) with TB_TAVERN (town vf 0x34(4)) built.</summary>
    public bool OwnsTavernTown(int player) => S.Towns.Any(t => t.Owner == player && t.Level("TB_TAVERN") >= 1);

    /// <summary>
    /// The ransom CFinishCombat takes for a surrender (00af64b0 case 3 → the loser army's vf 0xf4 = 004da9a0):
    /// half the gold value of the surviving army (creature gold cost × count, reserve stacks included), truncated.
    /// </summary>
    public long RansomCost(IEnumerable<ArmySlot> survivors) =>
        survivors.Sum(s => Core.Creatures.TryGetValue(s.Creature, out var cd) ? (long)cd.Cost.Gold * s.Count : 0) / 2;

    /// <summary>
    /// A hero leaves the battle alive (CFinishCombat 00af64b0, then the loser player's 004fe770 flee / 004fe890
    /// surrender). Flight (case 2): the army is emptied (hero vf 0x48 → 0045a590) and the hero's flee handler runs
    /// (vf 0x1d8 = 00b52e00: surrender flag +0x22e cleared). Surrender (case 3): the army is kept, the ransom is paid at
    /// once (loser's gold − cost, not below 0; the winner's player + cost) and vf 0x1dc = 00b52e50 sets +0x22e. Both then
    /// go through 004fe360: the hero leaves the player (004fe000) and the SAME hero object (level, skills, artifacts,
    /// spells) is put into that player's own tavern: the first empty slot of the two, else a random slot, whose hero
    /// returns to the pool. GAP: 004fe360 prefers replacing slots whose hero lacks flag +0x1e4 (or fails pool vf 0x4c);
    /// neither is identified, so the random pick is over both slots (the exe's fallback).
    /// </summary>
    void RetreatHero(HeroState h, bool surrender, List<ArmySlot> survivors, HeroState? winner)
    {
        var p = PlayerByIndex(h.Owner);
        if (surrender)
        {
            long cost = RansomCost(survivors);
            if (p != null) GiveOne(p, Purse.Gold, -(int)Math.Min(cost, Math.Max(0, p.Purse[Purse.Gold])));
            if (winner != null && PlayerByIndex(winner.Owner) is { } wp) GiveOne(wp, Purse.Gold, (int)Math.Min(int.MaxValue, cost));
        }
        else Array.Fill(h.Army, null);
        h.Alive = false;
        h.Retreat = surrender ? 2 : 1;
        h.InTown = -1;
        h.Garrisoned = false;
        if (p != null)
        {
            if (!S.HeroPool.Contains(h.DefId)) S.HeroPool.Add(h.DefId);
            int slot = Array.IndexOf(p.Tavern, null);
            if (slot < 0) slot = S.Rng.Next(p.Tavern.Length);
            p.Tavern[slot] = h.DefId;
        }
        Emit(new HeroRemoved(h.Id, surrender ? "surrendered" : "fled"));
    }

    /// <summary>
    /// The hero a tavern hire brings (004fec50): a hero that fled or surrendered is the same object again (its surrender
    /// flag cleared, vf 0x1ec), otherwise a new one from the hero's definition with a starting army.
    /// GAP: 004fec50 gives a returning hero no army; a fled hero's army was emptied, and where the exe refills it is not
    /// traced, so an empty army gets the starting army of a new hire.
    /// </summary>
    HeroState HireHero(string defId, int owner, Tile pos, out bool returning)
    {
        var h = S.Heroes.Find(x => !x.Alive && x.Retreat != 0 && x.DefId == defId);
        returning = h != null;
        if (h == null)
        {
            h = HeroFromDef(Core.Heroes[defId], owner, pos);
            GiveStartingArmy(h);
            return h;
        }
        h.Alive = true;
        h.Retreat = 0;
        h.Owner = owner;
        h.Pos = pos;
        h.InBoat = false;
        h.BoatId = -1;
        if (h.Army.StackCount() == 0) GiveStartingArmy(h);
        return h;
    }

    static void ClearBattleBonuses(HeroState h)
    {
        h.VisitedSinceBattle.Clear();
        h.NextBattleMorale = h.NextBattleLuck = 0;
        h.NextBattleAttack = h.NextBattleDefence = h.NextBattleHitPoints = h.NextBattleInitiative = h.NextBattleSpeed = 0;
    }

    HeroInstance ToInstance(HeroState h)
    {
        Core.Heroes.TryGetValue(h.DefId, out var def);
        return new HeroInstance
        {
            DisplayName = h.Name, Shared = def, Experience = (int)Math.Min(int.MaxValue, h.Experience), Level = h.Level,
            Army = h.Army.ToSlots(), Artifacts = h.Artifacts.ToList(),
            Attack = h.Attack, Defence = h.Defence, SpellPower = h.SpellPower, Knowledge = h.Knowledge,
            Skills = h.Skills.ToList(), Perks = h.Perks.ToList(), Spells = h.Spells.ToList(),
            Ballista = h.Ballista, FirstAidTent = h.FirstAidTent, AmmoCart = h.AmmoCart, Mana = h.Mana,
            FavoriteEnemies = h.FavoriteEnemies.Where(e => e.Length > 0).ToList(),
            // Morale and luck ([0], [1]) are added to the combat heroes by CreatePendingBattle.
            BattleBonus = new[] { 0, 0, h.NextBattleAttack, h.NextBattleDefence, h.NextBattleHitPoints, h.NextBattleInitiative, h.NextBattleSpeed },
        };
    }

    string? DoResolveBattle(ResolveBattleCmd c)
    {
        var pb = S.Battle!;
        S.Battle = null;
        var att = S.Hero(pb.AttackerHero)!;
        var defHero = pb.DefenderHero >= 0 ? S.Hero(pb.DefenderHero) : null;
        bool attackerWon = c.WinnerSide == 0;
        // The loser left alive (CFinishCombat 00af64b0 cases 2 flee / 3 surrender); either side can retreat.
        if (c.EndReason.Length == 0 && c.AttackerFled && !attackerWon) c = c with { EndReason = "fled" };
        bool retreat = c.EndReason is "fled" or "surrender", surrender = c.EndReason == "surrender";
        bool attRetreated = retreat && !attackerWon, defRetreated = retreat && attackerWon && defHero != null;
        // Spell points spent in the battle stay spent (exe: the combat hero is the adventure hero).
        if (c.HeroMana is { Length: 2 } mana) { att.Mana = Math.Max(0, mana[0]); if (defHero != null) defHero.Mana = Math.Max(0, mana[1]); }

        // Experience: killed creatures' Exp (p.298), neutral difficulty coefficient, +500 for a defeated hero.
        long ExpFor(List<Stack> before, List<ArmySlot> after)
        {
            long sum = 0;
            foreach (var g in before.GroupBy(s => s.Creature))
            {
                int start = g.Sum(s => s.Count), left = after.Where(a => a.Creature == g.Key).Sum(a => a.Count);
                // exe FUN_004d9c60: the creature's Exp (record +0x80), its hit points when Exp < 1.
                if (Core.Creatures.TryGetValue(g.Key, out var cd)) sum += (long)Math.Max(0, start - left) * (cd.Exp >= 1 ? cd.Exp : cd.Health);
            }
            return sum;
        }

        long expAtt = ExpFor(pb.DefenderArmy, c.DefenderSurvivors);
        double neutralCoef = 1;
        // Captured before the survivors replace the attacker's army (the defender's experience and necromancy
        // need the attacker's pre-battle stacks).
        var attackerBefore = att.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
        static IEnumerable<(string, int)> Killed(List<Stack> before, List<ArmySlot> after) =>
            before.GroupBy(s => s.Creature).Select(g => (g.Key, g.Sum(s => s.Count) - after.Where(a => a.Creature == g.Key).Sum(a => a.Count)));
        if (defHero == null && pb.DefenderObject >= 0 && O(pb.DefenderObject).Kind == ObjectKind.Monster)
        {
            var coefs = Db.Stats.Node(S.Campaign ? "adventure/NeutralsInCampaign/ExpCoefs" : "adventure/NeutralsNotInCampaign/ExpCoefs")?.Elements("Item").ToList();
            if (coefs is { Count: > 0 }) neutralCoef = XmlHelpers.ParseDouble(coefs[Math.Min(S.Difficulty, coefs.Count - 1)].Value, 1);
        }
        // exe FUN_00af3430: +500 for a defeated hero, Talented Warrior, neutral coefficient, Enlightenment & co, week.
        // +500 only when the defending hero is lost, not when he fled or surrendered (exe 004d99b0 via 00af3430).
        expAtt = BattleExperience(att, expAtt, opponentHeroLost: defHero != null && !defRetreated, neutralCoef);
        var (combatResult, savedArmies) = SavedCombatRecord(c, pb, att, defHero, attackerBefore);

        // The army of a hero that fled is emptied by RetreatHero (0045a590), a surrendered hero keeps its survivors.
        SetArmy(att.Army, c.AttackerSurvivors);
        ClearBattleBonuses(att);
        if (defHero != null)
        {
            SetArmy(defHero.Army, c.DefenderSurvivors);
            ClearBattleBonuses(defHero);
        }

        // Hero kill counters (hero vtable +0x60, read by the Avenger's Guild list check 009a8be0).
        // TODO(exe): the counter's writer (slot 1, FUN_00a844d0) is not traced; counted here as the enemy's losses.
        AddKills(att, Killed(pb.DefenderArmy, c.DefenderSurvivors));
        if (defHero != null) AddKills(defHero, Killed(attackerBefore, c.AttackerSurvivors));
        if (attackerWon)
        {
            Emit(new BattleResolved(0, expAtt) { Result = combatResult, Armies = savedArmies });
            if (defRetreated) RetreatHero(defHero!, surrender, c.DefenderSurvivors, att);
            else if (defHero != null) RemoveHero(defHero, "defeated");
            if (pb.DefenderObject >= 0)
            {
                var o = O(pb.DefenderObject);
                var os = S.Obj(o.Index);
                os.Guards.Clear();
                Array.Fill(os.Army, null);
                switch (o.Kind)
                {
                    case ObjectKind.Monster: RemoveObject(o.Index); break;
                    case ObjectKind.Town when S.Town(o.Index) is { } t:
                        t.Garrison = State.Army.Empty();
                        CaptureTown(att, t);
                        break;
                    case ObjectKind.Building:
                        LootBank(att, o, os);
                        break;
                    default:
                        // Guarded pickups/mines: continue the visit now that the guards are gone.
                        Visit(att, o);
                        break;
                }
            }
            GainExperience(att, expAtt, modifiersApplied: true);
            NecromancyRaise(att, Killed(pb.DefenderArmy, c.DefenderSurvivors));   // exe FUN_00c26be0
            if (att.Army.StackCount() == 0) RemoveHero(att, "army destroyed");
            if (att.Perks.Contains("HERO_SKILL_PATH_OF_WAR")) att.MovePoints += 350;   // Warpath (p.300)
        }
        else
        {
            Emit(new BattleResolved(1, 0) { Result = combatResult, Armies = savedArmies });
            if (defHero != null)
            {
                // +500 only when the attacking hero is lost, not when he fled or surrendered (exe 004d99b0 via 00af3430).
                GainExperience(defHero, BattleExperience(defHero, ExpFor(attackerBefore, c.AttackerSurvivors), opponentHeroLost: !attRetreated, 1),
                    modifiersApplied: true);
                NecromancyRaise(defHero, Killed(attackerBefore, c.AttackerSurvivors));
            }
            if (pb.DefenderObject >= 0 && O(pb.DefenderObject).Kind != ObjectKind.Town)
            {
                var os = S.Obj(pb.DefenderObject);
                if (IsArmyHolder(O(pb.DefenderObject)) && os.Owner >= 0 && os.Guards.Count == 0) SetArmy(os.Army, c.DefenderSurvivors);
                else os.Guards = c.DefenderSurvivors.Select(a => new Stack(a.Creature, a.Count)).ToList();
            }
            else if (pb.DefenderObject >= 0 && S.Town(pb.DefenderObject) is { } t && defHero == null)
                SetArmy(t.Garrison, c.DefenderSurvivors);
            if (attRetreated) RetreatHero(att, surrender, c.AttackerSurvivors, defHero);
            else RemoveHero(att, "defeated");
        }
        CheckEliminations();
        return null;
    }

    /// <summary>
    /// The saved-combat record the exe makes when a battle ends (FUN_00996d50, read by the GetSavedCombat*
    /// script functions): the result (engine 1 win → COMBAT_RESULT_WIN, 2 surrender → 3, 3 retreat → 2) and
    /// the winner's then the loser's army (FUN_00996ac0: owner, hero, stacks with their start count and
    /// losses; the loser of a win or a surrender is recorded with every creature dead).
    /// </summary>
    (int Result, List<BattleSideRecord> Armies) SavedCombatRecord(ResolveBattleCmd c, PendingBattle pb, HeroState att,
        HeroState? defHero, List<Stack> attackerBefore)
    {
        int result = c.EndReason switch { "fled" => 2, "surrender" => 3, _ => 1 };
        int defOwner = defHero?.Owner ?? (pb.DefenderObject >= 0 && S.Town(pb.DefenderObject) is { } town ? town.Owner : -1);
        BattleSideRecord Side(int owner, HeroState? hero, List<Stack> before, List<ArmySlot> survivors, bool allDied)
        {
            var left = survivors.GroupBy(s => s.Creature).ToDictionary(g => g.Key, g => g.Sum(s => s.Count));
            var stacks = new List<BattleStackRecord>();
            foreach (var s in before.Where(s => s.Count > 0))
            {
                int alive = Math.Min(s.Count, left.GetValueOrDefault(s.Creature));
                left[s.Creature] = left.GetValueOrDefault(s.Creature) - alive;
                stacks.Add(new BattleStackRecord(s.Creature, s.Count, allDied ? s.Count : s.Count - alive));
            }
            return new BattleSideRecord(owner < 0 ? 0 : owner + 1, hero?.DefId ?? "", stacks);
        }
        bool loserAllDied = result is 1 or 3;
        var attSide = (Owner: att.Owner, Hero: (HeroState?)att, Before: attackerBefore, Survivors: c.AttackerSurvivors);
        var defSide = (Owner: defOwner, Hero: defHero, Before: pb.DefenderArmy, Survivors: c.DefenderSurvivors);
        var (w, l) = c.WinnerSide == 0 ? (attSide, defSide) : (defSide, attSide);
        return (result, new List<BattleSideRecord>
        {
            Side(w.Owner, w.Hero, w.Before, w.Survivors, allDied: false),
            Side(l.Owner, l.Hero, l.Before, l.Survivors, allDied: loserAllDied),
        });
    }

    static void SetArmy(Stack?[] army, List<ArmySlot> survivors)
    {
        // Creature artifacts stay on the surviving stacks (survivors are reported per creature: the first artifact of
        // that creature is kept).
        var arts = army.Where(s => s is { Count: > 0 } && !MicroArtifact.IsEmpty(s.Artifact)).Select(s => (s!.Creature, s.Artifact)).ToList();
        for (int i = 0; i < army.Length; i++) army[i] = null;
        foreach (var s in survivors) if (s.Count > 0) army.TryAdd(s.Creature, s.Count);
        foreach (var st in army)
            if (st != null && arts.FindIndex(a => a.Creature == st.Creature) is var k && k >= 0) { st.Artifact = arts[k].Artifact; arts.RemoveAt(k); }
    }
}
