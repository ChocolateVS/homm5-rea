using H5.Adventure.Map;
using H5.Adventure.State;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// Threats (exe phase 0 FUN_0086d750 → FUN_0086cd10, table at CPlayerAI +0xe8, methods FUN_00c28xxx) and their users:
// "player in danger" (FUN_00866c30), the hire danger gate (FUN_008672d0), the build skip (FUN_009c3020) and the town
// protection / interception in the destination evaluator (FUN_00a78b60).
public sealed partial class OriginalAdventureAI
{
    /// <summary>Per (enemy hero, town) record: AccComing (+4), Threat (+8), the defender hero reserved for it (+0x14), path cells and their costs.</summary>
    public sealed class ThreatRecord
    {
        public int Town;
        public int AccComing;
        public double Threat;
        public int Defender = -1;
        public List<(Tile Cell, int Cost)> Path = new();
        public int Cost => Path.Count > 0 ? Path[^1].Cost : 0;
    }

    public sealed class HeroThreats
    {
        public HeroState Hero = null!;
        public double ArmyPower;
        public List<ThreatRecord> Records = new();
        public double SumThreat;
        public ThreatRecord? Find(int town) => Records.Find(r => r.Town == town);
    }

    public sealed class TownThreats
    {
        public double Total;
        public List<int> Heroes = new();
        public int Owner;
        public string TownType = "";
    }

    public sealed class ThreatTable
    {
        public readonly Dictionary<int, HeroThreats> H = new();
        public readonly Dictionary<int, TownThreats> T = new();

        /// <summary>FUN_00c289a0: only a positive threat over a non-empty path is recorded.</summary>
        public void Add(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState eh, TownState town, List<(Tile, int)> path, int acc, double threat)
        {
            if (path.Count == 0 || !(threat > 0.0)) return;
            if (!H.TryGetValue(eh.Id, out var eH))
                H[eh.Id] = eH = new HeroThreats { Hero = eh, ArmyPower = AiArmyPower(g, st, diff, eh.Army.Live(), eh) };
            if (!T.TryGetValue(town.Object, out var eT))
                T[town.Object] = eT = new TownThreats { Owner = town.Owner, TownType = town.Type };
            var r = eH.Find(town.Object);
            if (r == null) eH.Records.Add(r = new ThreatRecord { Town = town.Object });
            r.Path = path; r.Threat = threat; r.AccComing = acc; r.Defender = -1;
            eH.SumThreat += threat;
            eT.Total += threat;
            eT.Heroes.Add(eh.Id);
        }
    }

    readonly Dictionary<int, ThreatTable> _threats = new();

    /// <summary>Today's threat table of a player (empty before its first turn).</summary>
    public ThreatTable Threats(int player) => _threats.TryGetValue(player, out var t) ? t : new ThreatTable();

    /// <summary>Exe FUN_00ce9b00: days to cover <paramref name="cost"/>: 0 with the current move points, else (cost − cur) / max + 1.</summary>
    public static int DaysTo(int cost, int cur, int max)
    {
        if (cur >= cost) return 0;
        if (max <= 0) return 0xFFFFFF;
        return Math.Max(0, (cost - cur) / max) + 1;
    }

    /// <summary>
    /// The attacker side of a battle estimate wins (FUN_00bc3af0 judged by FUN_008664c0: state &gt; 0, or 0 with a
    /// positive value). GAP-detail: the full estimate with a defender hero / town (gain, aggressivity, survival
    /// minimum) is not ported; here the attacker wins when its AI army sum A ≥ 1 exceeds the defender's E.
    /// </summary>
    public static bool AttackerWins(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, IEnumerable<Stack> att, HeroState? attHero,
                                    IEnumerable<Stack> def, HeroState? defHero)
    {
        double a = AiArmyPower(g, st, diff, att, attHero), e = AiArmyPower(g, st, diff, def, defHero);
        return a >= 1.0 && a > e;
    }

    /// <summary>Exe FUN_00c02b20: threat multiplier by the town owner (neutral, ours, ally, enemy).</summary>
    static int ThreatMultiplier(Engine.Adventure g, AdventureDifficulty diff, int me, int owner)
    {
        if (owner < 0) return diff.ThreatsMultiplierNeutral;
        if (owner == me) return diff.ThreatsMultiplierOurs;
        return g.Player(owner) is { } o && g.Player(me) is { } m && o.Team == m.Team ? diff.ThreatsMultiplierAllys : diff.ThreatsMultiplierEnemys;
    }

    static bool SameTeam(Engine.Adventure g, int a, int b) =>
        a == b || (a >= 0 && b >= 0 && g.Player(a) is { } pa && g.Player(b) is { } pb && pa.Team == pb.Team);

    /// <summary>The town's garrison hero (town vf 0x18(1)) and visiting hero (vf 0x18(0)).</summary>
    static (HeroState? Garrison, HeroState? Visitor) TownHeroes(Engine.Adventure g, TownState t) =>
        (g.S.Heroes.FirstOrDefault(x => x.Alive && x.InTown == t.Object && x.Garrisoned),
         g.S.Heroes.FirstOrDefault(x => x.Alive && x.InTown == t.Object && !x.Garrisoned));

    /// <summary>
    /// CheckThreats (exe FUN_0086cd10), rebuilt every day: for every map hero of the players not on our team, its own
    /// path flood up to ThreatsDistanceInDays × max(100, max move points); every town it reaches (the town only) gets a
    /// threat unless already recorded today. Defence test: the weaker (hero estimate) of the town's two heroes, or the
    /// garrison when there is no garrison hero; skipped when the enemy is there within a day, or the owner is on the
    /// enemy's team; when the enemy would not win there is no threat. Threat = value · multiplier / max(1e-4, eff / MP),
    /// value = the town value (FUN_004eb480) for our own towns, ValueOfTown otherwise; eff = trunc(cost − acc ·
    /// ThreatsAccComingCoef), acc from yesterday's record: + (yesterday's cost − today's) when the hero came closer,
    /// else trunc(acc / ThreatsAccComingCalmingDivisor); at most ThreatsAccComingLimit, at least 0.
    /// GAP-details: the flood is the engine's path costs (the exe's mode-2 flood includes map spells, not decoded);
    /// the order of reached towns is the town list.
    /// </summary>
    public ThreatTable CheckThreats(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int me)
    {
        var prev = _threats.GetValueOrDefault(me);
        var table = new ThreatTable();
        foreach (var e in g.S.Players)
        {
            if (!e.Alive || SameTeam(g, me, e.Index)) continue;
            foreach (var eh in g.S.Heroes)
            {
                if (!eh.Alive || eh.Owner != e.Index || eh.Garrisoned) continue;
                int mp = Math.Max(100, eh.MaxMovePoints);
                var costs = g.ReachableCosts(eh, unchecked(diff.ThreatsDistanceInDays * mp));
                var prevH = prev?.H.GetValueOrDefault(eh.Id);
                foreach (var town in g.S.Towns)
                {
                    var entrance = g.EntranceOf(town);
                    if (!costs.TryGetValue(entrance, out int cost)) continue;
                    if (table.H.TryGetValue(eh.Id, out var eR) && eR.Find(town.Object) != null) continue;
                    var (g1, g0) = TownHeroes(g, town);
                    IEnumerable<Stack>? defArmy = null;
                    HeroState? defHero = null;
                    if (g1 == null) defArmy = town.Garrison.Live();
                    if (g1 != null && g0 != null)
                    {
                        long v0 = HeroEstimate(g, st, g0), v1 = HeroEstimate(g, st, g1);
                        defHero = v1 < v0 ? g1 : g0;
                        defArmy = defHero.Army.Live();
                    }
                    if (DaysTo(cost, eh.MovePoints, eh.MaxMovePoints) <= 1) defArmy = null;
                    if (!SameTeam(g, town.Owner, e.Index) && defArmy != null && defArmy.Any() &&
                        !AttackerWins(g, st, diff, eh.Army.Live(), eh, defArmy, defHero)) continue;
                    double mult = ThreatMultiplier(g, diff, me, town.Owner);
                    double value = town.Owner == me ? TownValue(g, st, diff, town, me) : diff.ValueOfTown;
                    if (!(value > 0.0)) continue;
                    int acc = 0;
                    if (prevH?.Find(town.Object) is { } pr)
                    {
                        int d = pr.Path.Count > 0 ? pr.Cost - cost : 0;
                        int a = d > 0 ? pr.AccComing + d : (int)((float)pr.AccComing / (float)diff.ThreatsAccComingCalmingDivisor);
                        acc = Math.Max(Math.Min(a, diff.ThreatsAccComingLimit), 0);
                    }
                    int eff = Math.Max(0, (int)((float)cost - (float)acc * (float)diff.ThreatsAccComingCoef));
                    double threat = value * mult / Math.Max(1e-4, (double)eff / mp);
                    table.Add(g, st, diff, eh, town, EnemyPath(g, eh, entrance, costs), acc, threat);
                }
            }
        }
        _threats[me] = table;
        return table;
    }

    /// <summary>The enemy's path cells to the town with its flood cost at each (FUN_00c07360; the last cell is the town).</summary>
    static List<(Tile, int)> EnemyPath(Engine.Adventure g, HeroState eh, Tile to, Dictionary<Tile, int> costs)
    {
        var path = g.FindPath(eh, to);
        var list = new List<(Tile, int)>();
        if (path == null) { list.Add((to, costs[to])); return list; }
        foreach (var c in path.Skip(1)) list.Add((c, costs.TryGetValue(c, out int k) ? k : 0));
        if (list.Count == 0) list.Add((to, costs[to]));
        return list;
    }

    /// <summary>
    /// Towns whose threat exceeds ThreatsTownInDangerThreshold (float, strict) — the k of FUN_00866c30 — and the build
    /// skip set (FUN_009c3020: any town with a threat entry).
    /// </summary>
    static ISet<int> ThreatenedTownsOf(ThreatTable t) => t.T.Keys.ToHashSet();

    int EndangeredTowns(Engine.Adventure g, AdventureDifficulty diff, int me) =>
        g.S.Towns.Count(t => t.Owner == me && Threats(me).T.TryGetValue(t.Object, out var e) && (float)diff.ThreatsTownInDangerThreshold < e.Total);

    /// <summary>
    /// "Town in danger" for hiring (exe FUN_008672d0): only a town with a garrison hero; true when one of the enemy heroes
    /// threatening it would beat the candidate hero in the field.
    /// </summary>
    public bool TownInDanger(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState candidate, TownState town)
    {
        if (TownHeroes(g, town).Garrison == null) return false;
        if (!Threats(town.Owner).T.TryGetValue(town.Object, out var e)) return false;
        foreach (int id in e.Heroes)
        {
            if (Threats(town.Owner).H.GetValueOrDefault(id)?.Hero is not { Alive: true } eh) continue;
            if (AttackerWins(g, st, diff, eh.Army.Live(), eh, candidate.Army.Live(), candidate)) return true;
        }
        return false;
    }

    /// <summary>
    /// Exe FUN_00a78830 "important town": on day 6 / 7 of the week a town reachable this week; otherwise our own town of
    /// development level ≥ 4 with a commander of its town type, or level ≥ 7.
    /// UNVERIFIED-detail: the level (threat entry +0x20) is taken as the town's development level.
    /// </summary>
    static bool ImportantTown(Engine.Adventure g, int cur, int me, TownState town, int cost, int max, Dictionary<int, HeroRole> roles)
    {
        int wd = g.S.DayOfWeek;
        if (wd >= 6)
        {
            if (wd == 7 && cost <= cur) return true;
            if (cost < cur + max) return true;
        }
        if (town.Owner != me) return false;
        int level = town.DevLevel;
        if (level >= 4 && g.S.Heroes.Any(h => h.Alive && h.Owner == me && roles.GetValueOrDefault(h.Id) == HeroRole.Commander && h.Town == town.Type)) return true;
        return level >= 7;
    }
}
