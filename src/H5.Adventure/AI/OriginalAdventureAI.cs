using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Combat.AI;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

/// <summary>
/// Strategic adventure AI (NWorld::CPlayerAI) driven by the stock AI/Adventure tunables.
///
/// Exe-confirmed parts (docs/rules/AI.md):
///   • hero spread weight = RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel) (FUN_00868a40, FUN_004e9a80);
///   • hero attribute value Σ attr·ValueOfAttribute* (FUN_004e9f60), artifact value by class (FUN_004eb840);
///   • enemy danger reach = move points · SearchDangerEnemyMobilityMultiplier + SearchDangerEnemyMobilityBonus (FUN_00872330);
///   • all thresholds/limits per difficulty come from the data rows the exe reads.
/// Reconstructed parts (structure from the exe's log strings and call graph, weights from data, exact formulas
/// not yet traced): destination scoring, town build/recruit priorities, threat/garrison handling.
///
/// Determinism: random choices use a private generator seeded from the game seed, day, player and hero, so the
/// game's own RNG stream is never consumed and every peer reproduces the same decisions.
/// </summary>
public sealed partial class OriginalAdventureAI
{
    /// <summary>Routes commands through the game's command sink (e.g. the map-script host) when one is set.</summary>
    static AdvResult Send(Engine.Adventure g, AdvCommand c) => g.CommandSink?.Invoke(c) ?? g.Apply(c);

    public int MaxActionsPerTurn { get; init; } = 80;
    /// <summary>Combat AI used to quick-combat the AI's battles.</summary>
    public IBattleAI BattleAI { get; init; } = new OriginalBattleAI { MaxCandidates = 24 };

    AdventureAiStats? _stats;
    readonly Dictionary<int, Tile> _lastTarget = new();

    public void PlayTurn(Engine.Adventure g, ulong battleSeedBase)
    {
        _stats ??= AdventureAiStats.Load(g.Core);
        var st = _stats;
        var me = g.Current.Index;
        var diff = st.Difficulty[Math.Clamp(g.S.Difficulty, 0, 3)];
        int actions = 0;

        ResolvePending(g, battleSeedBase);
        var towns = g.S.Towns.Where(t => t.Owner == me).ToList();
        if ((g.S.Day - 1) % diff.TownBuildPeriodInDays == 0)
            foreach (var t in towns) TryBuild(g, t, st);
        HireHeroes(g, towns, diff);
        foreach (var t in towns) Recruit(g, t, st);

        // Heroes act in descending spread weight: commanders first, collectors and suppliers after.
        var heroes = g.S.Heroes.Where(h => h.Alive && h.Owner == me).ToList();
        var roles = AssignRoles(g, heroes);
        var order = heroes.OrderByDescending(h => SpreadWeight(st, diff, roles[h.Id], h)).ThenBy(h => h.Id).ToList();
        var claimed = new HashSet<int>();
        foreach (var h in order)
        {
            for (int step = 0; step < 8 && actions < MaxActionsPerTurn && h.Alive && !g.S.Finished; step++)
            {
                ResolvePending(g, battleSeedBase + (ulong)actions);
                LevelUps(g, h, st);
                var target = ChooseDestination(g, h, roles[h.Id], diff, st, claimed);
                if (target == null) break;
                var r = Send(g, new MoveHeroCmd(h.Id, target.Value.Tile) { Player = me });
                actions++;
                if (!r.Ok) break;
                claimed.Add(target.Value.Object);
                _lastTarget[h.Id] = target.Value.Tile;
                ResolvePending(g, battleSeedBase + (ulong)actions);
                LevelUps(g, h, st);
                if (h.InTown >= 0 && g.S.Town(h.InTown) is { } home && home.Owner == me) TakeGarrison(g, h, home);
                if (h.MovePoints < 100) break;
            }
        }
        ResolvePending(g, battleSeedBase + 999);
        foreach (var h in g.S.Heroes.Where(h => h.Alive && h.Owner == me)) LevelUps(g, h, st);
        if (!g.S.Finished) Send(g, new EndTurnCmd { Player = me });
    }

    // ---------------------------------------------------------------- roles

    /// <summary>
    /// Role per hero: the strongest army commands; when there are three or more heroes the weakest supplies
    /// troops from towns; the rest collect. (Role ids and spread multipliers are the exe's; the assignment
    /// rule itself is reconstructed.)
    /// </summary>
    static Dictionary<int, HeroRole> AssignRoles(Engine.Adventure g, List<HeroState> heroes)
    {
        var roles = new Dictionary<int, HeroRole>();
        var byPower = heroes.OrderByDescending(h => g.ArmyPower(h.Army.Live(), h)).ThenBy(h => h.Id).ToList();
        for (int i = 0; i < byPower.Count; i++)
            roles[byPower[i].Id] = i == 0 ? HeroRole.Commander
                : byPower.Count >= 3 && i == byPower.Count - 1 ? HeroRole.Supplier
                : HeroRole.Collector;
        return roles;
    }

    /// <summary>Exe FUN_00868a40 × FUN_004e9a80: RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel).</summary>
    public static double SpreadWeight(AdventureAiStats st, AdventureDifficulty diff, HeroRole role, HeroState h) =>
        st.RoleSpread[(int)role - 1] * (1 + h.Level * diff.ArmyPowerCoefBonusPerHeroLevel);

    // ---------------------------------------------------------------- destinations

    readonly record struct Destination(int Object, Tile Tile, double Score);

    Destination? ChooseDestination(Engine.Adventure g, HeroState h, HeroRole role, AdventureDifficulty diff, AdventureAiStats st, HashSet<int> claimed)
    {
        double myPower = g.ArmyPower(h.Army.Live(), h);
        int maxCost = role == HeroRole.Commander ? diff.RolesSearchFarMaxCost : Math.Max(diff.RolesSearchNearMaxCost, h.MaxMovePoints * 3);
        var dangers = diff.CheckHeroesDanger ? EnemyDangers(g, h, st) : new List<(Tile Pos, int Reach, double Power)>();
        var rng = new Rng(Mix(g.S.Rng.S0, (ulong)g.S.Day, (ulong)h.Owner, (ulong)h.Id));
        Destination? best = null;
        foreach (var o in g.Map.Objects)
        {
            if (o.Pos.Floor != h.Pos.Floor || o.Kind == ObjectKind.Static || claimed.Contains(o.Index)) continue;
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            double value = ObjectValue(g, h, o, os, myPower, diff, st);
            if (value <= 0) continue;
            var tile = o.Kind is ObjectKind.Monster or ObjectKind.Hero ? o.Pos : o.ActiveTiles.FirstOrDefault(o.Pos);
            int d = Math.Max(Math.Abs(tile.X - h.Pos.X), Math.Abs(tile.Y - h.Pos.Y));
            if (d == 0) continue;
            int cost = d * st.SearchAverageTerrainCost;
            if (cost > maxCost) continue;
            // Guards on the way: never walk into a stack we would lose to.
            if (o.Kind != ObjectKind.Monster && g.GuardingMonster(tile) is { } guard &&
                g.S.Objects.TryGetValue(guard.Index, out var gs) && g.ArmyPower(gs.Guards, null) > myPower * (1 - diff.CowardiceVersusComputer)) continue;
            // Danger zones of stronger enemy heroes (FUN_00872330 reach).
            if (dangers.Any(z => z.Power > myPower && Math.Max(Math.Abs(z.Pos.X - tile.X), Math.Abs(z.Pos.Y - tile.Y)) * st.SearchAverageTerrainCost <= z.Reach)) continue;
            double days = cost / (double)Math.Max(100, h.MaxMovePoints);
            double score = value / (1 + days);
            if (_lastTarget.TryGetValue(h.Id, out var last) && last == tile) score *= st.SearchOldTargetMultiplier;
            score *= 1 + (rng.NextDouble() * 2 - 1) * st.SearchBestPositionRandomFactor / 100.0;
            if (best == null || score > best.Value.Score) best = new Destination(o.Index, tile, score);
        }
        if (best == null || g.FindPath(h, best.Value.Tile) == null) return SeaTravel(g, h, myPower, diff, st, claimed);
        return best;
    }

    /// <summary>Gold-equivalent value of visiting an object, using the AI/Adventure ValueOf* tunables.</summary>
    static double ObjectValue(Engine.Adventure g, HeroState h, MapObject o, ObjState? os, double myPower, AdventureDifficulty diff, AdventureAiStats st)
    {
        switch (o.Kind)
        {
            case ObjectKind.Treasure:
                if (os == null) return 500;
                return os.ResourceKind == Purse.Gold ? os.Amount : os.ResourceKind >= 0 ? os.Amount * (os.ResourceKind <= Purse.Ore ? 250 : 500) : 1000;
            case ObjectKind.Artifact:
                var art = o.ArtifactId ?? o.RewardArtifact;
                return art != null && g.Core.Artifacts.TryGetValue(art, out var ad) ? st.ArtifactValue(ad.Type) : st.ValueOfMinorArtifact;
            case ObjectKind.Mine:
            case ObjectKind.AbandonedMine:
                // Daily income in gold equivalents (gold mine 1000, resource mines 2 units × 250/500) scaled by
                // IncomeMultiplier/10. Reconstructed: the exe's exact income valuation is not traced yet.
                if (os != null && os.Owner == h.Owner) return 0;
                bool goldMine = o.Tag.Contains("GOLD", StringComparison.OrdinalIgnoreCase) || o.SharedPath.Contains("Gold", StringComparison.OrdinalIgnoreCase);
                bool common = o.Tag.Contains("WOOD", StringComparison.OrdinalIgnoreCase) || o.Tag.Contains("ORE", StringComparison.OrdinalIgnoreCase)
                              || o.SharedPath.Contains("Sawmill", StringComparison.OrdinalIgnoreCase) || o.SharedPath.Contains("Ore", StringComparison.OrdinalIgnoreCase);
                return (goldMine ? 1000 : common ? 500 : 1000) * st.IncomeMultiplier / 10.0;
            case ObjectKind.Dwelling:
                return os == null || os.Owner != h.Owner ? st.IncomeMultiplier * 40 : 0;
            case ObjectKind.Shrine:
                return h.Spells.Count < 64 ? st.ValueOfSpellPerLevel * 2 : 0;
            case ObjectKind.Monster:
            {
                if (os == null) return 0;
                double enemy = g.ArmyPower(os.Guards, null);
                if (enemy * (1 + diff.CowardiceVersusComputer) >= myPower * diff.AttackComputerAggressivityCoef) return 0;
                return enemy * 0.5;
            }
            case ObjectKind.Town:
            {
                var t = g.S.Town(o.Index);
                if (t == null) return 0;
                if (t.Owner == h.Owner)
                    return h.InTown != t.Object && t.Garrison.StackCount() > 0 ? diff.ValueOfTown * 0.25 : 0;
                double garrison = g.ArmyPower(t.Garrison.Live(), null);
                bool human = t.Owner >= 0 && g.S.Players[t.Owner].Human;
                double aggr = human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef;
                double coward = human ? diff.CowardiceVersusHuman : diff.CowardiceVersusComputer;
                if (garrison * (1 + coward) >= myPower * aggr) return 0;
                return diff.ValueOfTown;
            }
            case ObjectKind.Hero:
            {
                var other = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == o.Pos && x.Owner != h.Owner);
                if (other == null) return 0;
                double enemy = g.ArmyPower(other.Army.Live(), other);
                bool human = g.S.Players[other.Owner].Human;
                double aggr = human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef;
                double coward = human ? diff.CowardiceVersusHuman : diff.CowardiceVersusComputer;
                if (enemy * (1 + coward) >= myPower * aggr) return 0;
                return st.ValueOfHero + st.ValueOfHeroLevelPow2 * other.Level * other.Level;
            }
            default:
                return 0;
        }
    }

    /// <summary>Enemy heroes with their danger reach (exe FUN_00872330: movePoints·mult + bonus) and army power.</summary>
    static List<(Tile Pos, int Reach, double Power)> EnemyDangers(Engine.Adventure g, HeroState h, AdventureAiStats st)
    {
        var team = g.S.Players[h.Owner].Team;
        return g.S.Heroes.Where(x => x.Alive && x.Owner >= 0 && x.Owner != h.Owner && g.S.Players[x.Owner].Team != team && x.Pos.Floor == h.Pos.Floor)
            .Select(x => (x.Pos, (int)Math.Round(x.MaxMovePoints * st.SearchDangerEnemyMobilityMultiplier + st.SearchDangerEnemyMobilityBonus),
                          g.ArmyPower(x.Army.Live(), x)))
            .ToList();
    }

    static ulong Mix(params ulong[] parts)
    {
        ulong h = 0x9E3779B97F4A7C15;
        foreach (var p in parts) { h ^= p + 0x9E3779B97F4A7C15 + (h << 6) + (h >> 2); h *= 0xBF58476D1CE4E5B9; }
        return h;
    }

    // ---------------------------------------------------------------- towns and heroes

    static void TryBuild(Engine.Adventure g, TownState t, AdventureAiStats st)
    {
        if (t.BuiltToday) return;
        // Income first (IncomeMultiplier weighs income buildings heavily), then dwellings low→high, defences, guild.
        var order = new List<string> { "TB_TOWN_HALL", "TB_MARKETPLACE" };
        for (int i = 1; i <= 7; i++) order.Add($"TB_DWELLING_{i}");
        order.AddRange(new[] { "TB_FORT", "TB_MAGIC_GUILD", "TB_TAVERN", "TB_BLACKSMITH" });
        foreach (var b in order.Concat(t.MaxLevel.Keys.Except(order).OrderBy(k => k, StringComparer.Ordinal)))
            if (Send(g, new BuildCmd(t.Object, b) { Player = t.Owner }).Ok) return;
    }

    /// <summary>Buys creatures high tier first, keeping TownCreaturesReserveCoef of the gold back.</summary>
    static void Recruit(Engine.Adventure g, TownState t, AdventureAiStats st)
    {
        int reserve = (int)(g.Current.Purse[Purse.Gold] * st.TownCreaturesReserveCoef);
        for (int tier = 7; tier >= 1; tier--)
        {
            int variant = g.DwellingCreature(t, tier, 1) != null ? 1 : 0;
            var cr = g.DwellingCreature(t, tier, variant);
            if (cr == null || !g.Core.Creatures.TryGetValue(cr, out var cd) || t.Available[tier - 1] <= 0) continue;
            int gold = Math.Max(1, cd.Cost.Gold);
            int n = Math.Min(t.Available[tier - 1], Math.Max(0, g.Current.Purse[Purse.Gold] - reserve) / gold);
            while (n > 0 && !Send(g, new RecruitCmd(t.Object, tier, n, variant) { Player = t.Owner }).Ok) n /= 2;
        }
    }

    /// <summary>Hero cap = min(HireHeroesGlobalLimit, HireHeroesBase + HireHeroesPerTown · towns).</summary>
    static void HireHeroes(Engine.Adventure g, List<TownState> towns, AdventureDifficulty diff)
    {
        var me = g.Current;
        int limit = Math.Min(diff.HireHeroesGlobalLimit, diff.HireHeroesBase + diff.HireHeroesPerTown * towns.Count);
        foreach (var t in towns.Where(t => t.Level("TB_TAVERN") > 0))
        {
            if (g.S.Heroes.Count(h => h.Alive && h.Owner == me.Index) >= limit) return;
            if (me.Purse[Purse.Gold] < Engine.Adventure.HeroHireCost * 2) return;
            for (int slot = 0; slot < me.Tavern.Length; slot++)
                if (me.Tavern[slot] != null && Send(g, new HireHeroCmd(t.Object, slot) { Player = me.Index }).Ok) break;
        }
    }

    static void TakeGarrison(Engine.Adventure g, HeroState h, TownState t)
    {
        for (int from = 0; from < Army.Slots; from++)
        {
            if (t.Garrison[from] is not { Count: > 0 } s) continue;
            int to = Array.FindIndex(h.Army, x => x is { Count: > 0 } && x.Creature == s.Creature);
            if (to < 0) to = Array.FindIndex(h.Army, x => x is not { Count: > 0 });
            if (to < 0) break;
            Send(g, new TransferStackCmd(-1, t.Object, from, h.Id, -1, to, s.Count) { Player = h.Owner });
        }
    }

    /// <summary>Takes the level-up option with the highest ValueOfSkillPerMastery / ValueOfPerkFeat value.</summary>
    static void LevelUps(Engine.Adventure g, HeroState h, AdventureAiStats st)
    {
        for (int guard = 0; guard < 16 && h.Alive && h.PendingLevelUps.Count > 0; guard++)
        {
            var offer = h.PendingLevelUps[0];
            int n = offer.Skills.Count + offer.Perks.Count;
            int bestOpt = 0;
            double bestVal = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double v = i < offer.Skills.Count ? st.ValueOfSkillPerMastery * (int)offer.Skills[i].Mastery : st.ValueOfPerkFeat;
                if (v > bestVal) { bestVal = v; bestOpt = i; }
            }
            if (n == 0 || !Send(g, new ChooseLevelUpCmd(h.Id, bestOpt) { Player = h.Owner }).Ok) break;
        }
    }

    void ResolvePending(Engine.Adventure g, ulong seed)
    {
        for (int guard = 0; guard < 8 && (g.S.Battle != null || g.S.Choice != null); guard++)
        {
            if (g.S.Battle != null) Send(g, QuickCombat.Resolve(g, seed + (ulong)guard, BattleAI));
            else if (g.S.Choice is { } c)
                Send(g, new AnswerChoiceCmd(Accept: c.Kind != "join_paid" || g.Current.Purse[Purse.Gold] >= c.Gold * 2) { Player = g.Current.Index });
        }
    }
}
