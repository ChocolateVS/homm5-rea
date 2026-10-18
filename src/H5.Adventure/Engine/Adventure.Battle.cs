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
    /// Join / flee / fight decision for neutral stacks (fan manual p.319-320, exe FUN_008e1ad0 and
    /// FUN_008dc380). Mood coefficients 0.5/1.0/1.5/2.0 minus 0.2 per Diplomacy level and the
    /// ×3 (×2 with Diplomacy) join price are confirmed from the executable (FUN_008da210, constants
    /// 0x3f000000…0x40000000, 0x3e4ccccd). TODO(exe): the exact random range of the join threshold
    /// (the manual's random(1;7)·Mood_Coef is used).
    /// </summary>
    void StartNeutralEncounter(HeroState h, MapObject mon)
    {
        var os = S.Obj(mon.Index);
        var guards = os.Guards.Select(g => new Stack(g.Creature, g.Count)).ToList();
        bool mixed = guards.Select(g => g.Creature).Distinct().Count() > 1;
        switch (mon.Courage)
        {
            case "MONSTER_COURAGE_ALWAYS_JOIN":
                OfferJoin(h, mon, guards, paid: false);
                return;
            case "MONSTER_COURAGE_ALWAYS_FIGHT":
                StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false);
                return;
        }
        if (mixed) { StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false); return; }

        int diplomacy = h.HasPerk("HERO_SKILL_DIPLOMACY") ? 1 : 0;
        if (h.HasArtifact("ARTIFACT_CROWN_OF_LEADER")) diplomacy++;
        double moodCoef = new[] { 0.5, 1.0, 1.5, 2.0 }[MoodIndex(mon.Mood)];
        if (diplomacy > 0) moodCoef -= 0.2 * diplomacy;
        double ratio = ArmyPower(h.Army.Live(), h) / Math.Max(1, ArmyPower(guards, null));
        double joinThreshold = S.Rng.Range(1, 7) * moodCoef;
        double fightLimit = joinThreshold + 3 * moodCoef;

        if (ratio < joinThreshold) { StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false); return; }
        bool freeCase = ratio > fightLimit;
        int chance = ChanceToJoin(h, guards[0].Creature, mon.Mood, diplomacy);
        if (S.Rng.Next(100) < chance)
        {
            OfferJoin(h, mon, guards, paid: !freeCase && S.MoonWeek != "WEEK_OF_DIPLOMACY", priceMul: Math.Max(0, 3 - diplomacy));
            return;
        }
        if (freeCase)
        {
            // The neutrals flee.
            Emit(new ObjectVisited(h.Id, mon.Index, "monster_fled"));
            RemoveObject(mon.Index);
        }
        else StartBattle(h, -1, mon.Index, mon.Pos, guards, siege: false);
    }

    /// <summary>Chance to join (p.320): mood base 10/5/0/−5 % plus alignment, faction, army and Diplomacy modifiers.</summary>
    int ChanceToJoin(HeroState h, string creature, string mood, int diplomacy)
    {
        int chance = new[] { 10, 5, 0, -5 }[MoodIndex(mood)];
        var cd = Core.Creatures.GetValueOrDefault(creature);
        if (cd != null)
        {
            static bool Good(string town) => town is "TOWN_ACADEMY" or "TOWN_HEAVEN" or "TOWN_PRESERVE" or "TOWN_FORTRESS";
            static bool Evil(string town) => town is "TOWN_DUNGEON" or "TOWN_INFERNO" or "TOWN_NECROMANCY" or "TOWN_STRONGHOLD";
            if ((Good(cd.Town) && Good(h.Town)) || (Evil(cd.Town) && Evil(h.Town))) chance += 5;
            if (cd.Town == h.Town) chance += 5;
            if (h.Army.Live().Any(s => s.Creature == creature)) chance += 5;
            if (h.Army.Live().Any(s => cd.Upgrades.Contains(s.Creature))) chance += 5;
        }
        if (diplomacy > 0) chance += 10;
        // TODO: +20% for a matching creature specialisation (hero specialisation table).
        return chance;
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
            arena: town == null ? SeaBattleArena(att, pb, seed) : null);
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
        return state;
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
            Ballista = h.Ballista, FirstAidTent = h.FirstAidTent, AmmoCart = h.AmmoCart,
        };
    }

    string? DoResolveBattle(ResolveBattleCmd c)
    {
        var pb = S.Battle!;
        S.Battle = null;
        var att = S.Hero(pb.AttackerHero)!;
        var defHero = pb.DefenderHero >= 0 ? S.Hero(pb.DefenderHero) : null;
        bool attackerWon = c.WinnerSide == 0;

        // Experience: killed creatures' Exp (p.298), neutral difficulty coefficient, +500 for a defeated hero.
        long ExpFor(List<Stack> before, List<ArmySlot> after)
        {
            long sum = 0;
            foreach (var g in before.GroupBy(s => s.Creature))
            {
                int start = g.Sum(s => s.Count), left = after.Where(a => a.Creature == g.Key).Sum(a => a.Count);
                if (Core.Creatures.TryGetValue(g.Key, out var cd)) sum += (long)Math.Max(0, start - left) * cd.Exp;
            }
            return sum;
        }

        long expAtt = ExpFor(pb.DefenderArmy, c.DefenderSurvivors);
        if (defHero == null && pb.DefenderObject >= 0 && O(pb.DefenderObject).Kind == ObjectKind.Monster)
        {
            var coefs = Db.Stats.Node(S.Campaign ? "adventure/NeutralsInCampaign/ExpCoefs" : "adventure/NeutralsNotInCampaign/ExpCoefs")?.Elements("Item").ToList();
            if (coefs is { Count: > 0 }) expAtt = (long)(expAtt * XmlHelpers.ParseDouble(coefs[Math.Min(S.Difficulty, coefs.Count - 1)].Value, 1));
        }
        expAtt = (long)(expAtt * (S.MoonWeek switch { "WEEK_OF_FOLLY" => 0.5, "WEEK_OF_HONOR" => 2.0, _ => 1.0 }));
        if (attackerWon && defHero != null) expAtt += 500;

        SetArmy(att.Army, c.AttackerSurvivors);
        att.VisitedSinceBattle.Clear(); att.NextBattleMorale = 0; att.NextBattleLuck = 0;
        if (defHero != null)
        {
            SetArmy(defHero.Army, c.DefenderSurvivors);
            defHero.VisitedSinceBattle.Clear(); defHero.NextBattleMorale = 0; defHero.NextBattleLuck = 0;
        }

        if (attackerWon)
        {
            Emit(new BattleResolved(0, expAtt));
            if (defHero != null) RemoveHero(defHero, "defeated");
            if (pb.DefenderObject >= 0)
            {
                var o = O(pb.DefenderObject);
                var os = S.Obj(o.Index);
                os.Guards.Clear();
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
            GainExperience(att, expAtt);
            if (att.Army.StackCount() == 0) RemoveHero(att, "army destroyed");
            if (att.Perks.Contains("HERO_SKILL_PATH_OF_WAR")) att.MovePoints += 350;   // Warpath (p.300)
        }
        else
        {
            Emit(new BattleResolved(1, 0));
            if (defHero != null) GainExperience(defHero, ExpFor(att.Army.Live().ToList(), c.AttackerSurvivors) + 500);
            if (pb.DefenderObject >= 0 && O(pb.DefenderObject).Kind != ObjectKind.Town)
            {
                var os = S.Obj(pb.DefenderObject);
                os.Guards = c.DefenderSurvivors.Select(a => new Stack(a.Creature, a.Count)).ToList();
            }
            else if (pb.DefenderObject >= 0 && S.Town(pb.DefenderObject) is { } t && defHero == null)
                SetArmy(t.Garrison, c.DefenderSurvivors);
            RemoveHero(att, c.AttackerFled ? "fled" : "defeated");
        }
        CheckEliminations();
        return null;
    }

    static void SetArmy(Stack?[] army, List<ArmySlot> survivors)
    {
        for (int i = 0; i < army.Length; i++) army[i] = null;
        foreach (var s in survivors) if (s.Count > 0) army.TryAdd(s.Creature, s.Count);
    }
}
