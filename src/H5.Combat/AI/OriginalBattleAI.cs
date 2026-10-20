namespace H5.Combat.AI;

/// <summary>
/// Port of the original combat AI (NWorld::CCombatAI; think loop FUN_008d6a70). Per decision it
/// 1. builds candidate commands for the acting unit or hero (FUN_008d6940 and the generators it calls),
/// 2. simulates each on a copy of the battle and scores the resulting position with the two-sided
///    combat value (FUN_008d5eb0 → FUN_00bfc2a0, see <see cref="CombatValue"/>),
/// 3. drops candidates that do not beat the start position when they must (FUN_008d1cb0 "Excluded by
///    start value"), spells above the difficulty's spell level, and spells that would breach the mana reserve,
/// 4. picks the best survivor with the original ordering (FUN_008d0030): fewer turns to act, lower priority
///    rank, better combat value under the current mode's weights, non-spell over spell, cheaper move;
///    and defends when nothing survives ("End of choose. Chose defend.").
/// The mode (kamikaze / normal / power saving) comes from the our/enemy power ratio (FUN_00a2f4d0).
/// Look-ahead search (lookAheadTime &gt; 0, FUN_008d10e0) is not used by the shipped game for normal AI turns
/// and is not ported. All randomness comes from simulations on cloned states, so decisions are deterministic.
/// </summary>
public sealed class OriginalBattleAI : IBattleAI
{
    public AiDifficulty Difficulty { get; init; } = AiDifficulty.Normal;
    /// <summary>Upper bound on simulated candidates per decision (keeps CPU bounded).</summary>
    public int MaxCandidates { get; init; } = 96;
    /// <summary>Mana the hero keeps in reserve (exe compares against CCombatAI+0x1b0; the shipped value is 0).</summary>
    public int ManaReserve { get; init; }

    CombatAiStats? _stats;

    public sealed class Candidate
    {
        public required BattleCommand Command;
        /// <summary>Turns needed before the action happens; −1 when not applicable (immediate actions).</summary>
        public int Turns = -1;
        /// <summary>Rank; lower wins. Every candidate type the port generates uses 0, as the shipped generators do for normal play.</summary>
        public int Priority;
        public int MovePrice;
        public bool IsSpell;
        /// <summary>True when the candidate must improve on the start position to be considered (compare type 1).</summary>
        public bool MustBeatStart;
        public CombatValue? Value;
        public string Label = "";
    }

    /// <summary>The last decision's candidates, for logging and tests.</summary>
    public IReadOnlyList<Candidate> LastCandidates { get; private set; } = Array.Empty<Candidate>();
    public CombatMode LastMode { get; private set; }

    public BattleCommand Decide(Battle b, int side)
    {
        _stats ??= CombatAiStats.Load(b.Db);
        var s = b.State;
        if (s.Phase == BattlePhase.Tactics) return new EndTacticsCmd { Side = side };
        var la = b.GetLegalActions();
        if (la == null || b.SideToAct != side) return new WaitCmd(Actor.Hero(side)) { Side = side };

        var start = CombatValue.Of(b, side);
        var mode = SelectMode(start);
        LastMode = mode;
        var coefs = _stats.Coefs(mode);
        var diff = _stats.Difficulty[(int)Difficulty];

        var candidates = Generate(b, la, side, diff).Take(MaxCandidates).ToList();
        var survivors = new List<Candidate>();
        foreach (var c in candidates)
        {
            var sim = new Battle(b.Db, s.Clone());
            if (!sim.Apply(c.Command).Ok) continue;
            c.Value = CombatValue.Of(sim, side);
            if (c.MustBeatStart && !CombatValue.IsBetter(c.Value, start, coefs, mode)) continue;
            survivors.Add(c);
        }
        LastCandidates = candidates;
        if (survivors.Count == 0) return Defend(la, side);

        var best = survivors[0];
        for (int i = 1; i < survivors.Count; i++)
            if (CandidateBetter(best, survivors[i], coefs, mode)) best = survivors[i];
        return best.Command;
    }

    /// <summary>Kamikaze below min(threshold, 1000), power saving above its threshold (exe FUN_00a2f4d0).</summary>
    public CombatMode SelectMode(CombatValue start)
    {
        var st = _stats!;
        int enemy = start.EnemyPower;
        if (enemy <= 0) return CombatMode.Normal;
        double ratio = start.MyPower / (double)enemy;
        double kamikaze = Math.Max(0, Math.Min(st.KamikazeModeThreshold, 1000));
        double saving = Math.Max(0, Math.Min(st.PowerSavingThreshold, 1000));
        if (ratio < kamikaze) return CombatMode.Kamikaze;
        if (saving < ratio) return CombatMode.PowerSaving;
        return CombatMode.Normal;
    }

    /// <summary>True when <paramref name="cand"/> should replace <paramref name="cur"/> (exe FUN_008d0030).</summary>
    public static bool CandidateBetter(Candidate cur, Candidate cand, CombatModeCoefs coefs, CombatMode mode)
    {
        if (cur.Turns >= 0 && cand.Turns >= 0 && cur.Turns != cand.Turns) return cand.Turns < cur.Turns;
        if (cur.Priority != cand.Priority) return cand.Priority < cur.Priority;
        if (CombatValue.IsBetter(cand.Value!, cur.Value!, coefs, mode)) return true;
        if (CombatValue.IsBetter(cur.Value!, cand.Value!, coefs, mode)) return false;
        if (cur.IsSpell != cand.IsSpell) return cur.IsSpell;
        return cand.MovePrice < cur.MovePrice;
    }

    static BattleCommand Defend(LegalActions la, int side) =>
        la.CanDefend && !la.IsHero ? new DefendCmd(la.Actor) { Side = side } : new WaitCmd(la.Actor) { Side = side };

    // ---------------------------------------------------------------- candidate generation

    IEnumerable<Candidate> Generate(Battle b, LegalActions la, int side, CombatDifficulty diff)
    {
        var s = b.State;
        var list = new List<Candidate>();
        var enemies = s.Living.Where(e => b.Controller(e) != side && !e.IsObject).ToList();

        // Spells and abilities (FUN_008d6670): each must beat the start position (compare type 1).
        foreach (var sp in la.Spells)
        {
            var def = b.Db.Spell(sp.Spell);
            if (def == null || def.Level > diff.MaxSpellLevelToCast) continue;
            int manaAfter = (la.IsHero ? s.Heroes[side].Mana : s.Unit(la.Actor)!.Mana) - sp.ManaCost;
            if (manaAfter < ManaReserve) continue;   // "Excluded by mana limit"
            switch (sp.Targeting)
            {
                case Battle.SpellTargeting.None:
                    list.Add(Spell(new CastSpellCmd(la.Actor, sp.Spell) { Side = side }, sp.Spell));
                    break;
                case Battle.SpellTargeting.Unit:
                    foreach (var t in sp.Targets)
                        list.Add(Spell(new CastSpellCmd(la.Actor, sp.Spell, t) { Side = side }, $"{sp.Spell}->{t}"));
                    break;
                case Battle.SpellTargeting.Cell:
                    foreach (var cell in AreaCells(b, enemies, diff))
                        list.Add(Spell(new CastSpellCmd(la.Actor, sp.Spell, null, cell) { Side = side }, $"{sp.Spell}@{cell.X},{cell.Y}"));
                    break;
            }
        }
        foreach (var ab in la.Abilities)
        {
            switch (ab.Targeting)
            {
                case Battle.AbilityTargeting.None:
                    list.Add(Ability(new UseAbilityCmd(la.Actor, ab.Ability) { Side = side }, ab.Ability));
                    break;
                case Battle.AbilityTargeting.Cell:
                    foreach (var cell in AreaCells(b, enemies, diff))
                        list.Add(Ability(new UseAbilityCmd(la.Actor, ab.Ability, null, cell) { Side = side }, $"{ab.Ability}@{cell.X},{cell.Y}"));
                    break;
                default:
                    foreach (var t in ab.Targets)
                        list.Add(Ability(new UseAbilityCmd(la.Actor, ab.Ability, t) { Side = side }, $"{ab.Ability}->{t}"));
                    break;
            }
        }

        if (la.IsHero)
        {
            // Hero turn (FUN_008d2e90): a strike at every valid target.
            foreach (var t in la.HeroAttack)
                list.Add(new Candidate { Command = new HeroAttackCmd(t) { Side = side }, Label = $"hero->{t}" });
            return list;
        }

        var u = s.Unit(la.Actor)!;
        var reach = b.Reachable(u);

        // Shooters (FUN_008d6850 → FUN_008d3950 "Shoot"): one candidate per target.
        foreach (var t in la.Shoot)
            list.Add(new Candidate { Command = new ShootCmd(u.Id, t) { Side = side }, Label = $"shoot->{t}" });

        // Melee (FUN_008d64e0): every target from every cell the unit may strike from.
        foreach (var (tid, cells) in la.Melee)
            foreach (var from in cells)
                list.Add(new Candidate
                {
                    Command = new AttackCmd(u.Id, tid, from == u.Pos ? null : from) { Side = side },
                    MovePrice = from == u.Pos ? 0 : reach.GetValueOrDefault(from),
                    Label = $"attack->{tid} from {from.X},{from.Y}",
                });

        // Approach moves (FUN_008d5b90) when the unit cannot attack this turn.
        if (la.Melee.Count == 0 && la.Shoot.Count == 0 && la.Moves.Count > 0)
            list.AddRange(ApproachMoves(b, u, side, la, reach, enemies, diff));

        // Positional moves gated by the difficulty switches.
        if (diff.UseDefendShootersMoves) list.AddRange(DefendShootersMoves(b, u, side, la, reach, enemies));
        if (diff.UseBlockEnemyMoves) list.AddRange(BlockEnemyMoves(b, u, side, la, reach, enemies));
        return list;
    }

    static Candidate Spell(BattleCommand cmd, string label) => new() { Command = cmd, IsSpell = true, MustBeatStart = true, Label = label };
    static Candidate Ability(BattleCommand cmd, string label) => new() { Command = cmd, MustBeatStart = true, Label = label };

    /// <summary>
    /// Centre cells for area spells: enemy creature positions; unless CenterAreaSpellsOnCreatureOnly, also the
    /// empty cells between pairs of enemies, which can catch two stacks at once.
    /// </summary>
    static IEnumerable<Cell> AreaCells(Battle b, List<Unit> enemies, CombatDifficulty diff)
    {
        var cells = new SortedSet<(int, int)>();
        foreach (var e in enemies.Where(e => !e.IsWarMachine)) cells.Add((e.Pos.X, e.Pos.Y));
        if (!diff.CenterAreaSpellsOnCreatureOnly)
            foreach (var a in enemies)
                foreach (var c in enemies)
                {
                    if (a.Id >= c.Id) continue;
                    int mx = (a.Pos.X + c.Pos.X) / 2, my = (a.Pos.Y + c.Pos.Y) / 2;
                    if (Math.Abs(a.Pos.X - c.Pos.X) <= 2 && Math.Abs(a.Pos.Y - c.Pos.Y) <= 2) cells.Add((mx, my));
                }
        return cells.Select(c => new Cell(c.Item1, c.Item2));
    }

    /// <summary>
    /// Moves toward enemies: with ConsiderOnlyShortestPathToEnemy only the single cell that gets closest to the
    /// nearest enemy, otherwise the closest reachable cell toward each enemy stack. Turns = rounds still needed to
    /// reach striking distance afterwards (compared before value, so faster approaches win).
    /// </summary>
    static IEnumerable<Candidate> ApproachMoves(Battle b, Unit u, int side, LegalActions la, Dictionary<Cell, int> reach,
        List<Unit> enemies, CombatDifficulty diff)
    {
        var targets = enemies.Where(e => !e.IsWarMachine).ToList();
        if (targets.Count == 0) targets = enemies;
        if (targets.Count == 0) yield break;
        int speed = Math.Max(1, b.SpeedOf(u));
        IEnumerable<Unit> goals = diff.ConsiderOnlyShortestPathToEnemy
            ? new[] { targets.OrderBy(e => Battle.Distance(u, e)).ThenBy(e => e.Id).First() }
            : targets.OrderBy(e => e.Id);
        var seen = new HashSet<Cell>();
        foreach (var e in goals)
        {
            Cell? bestCell = null;
            int bestDist = int.MaxValue, bestCost = int.MaxValue;
            foreach (var c in la.Moves)
            {
                int d = Battle.Distance(u, c, e, e.Pos);
                int cost = reach.GetValueOrDefault(c);
                if (d < bestDist || (d == bestDist && cost < bestCost) || (d == bestDist && cost == bestCost && Before(c, bestCell)))
                { bestCell = c; bestDist = d; bestCost = cost; }
            }
            if (bestCell is not { } cell || !seen.Add(cell)) continue;
            int turns = (int)Math.Ceiling(Math.Max(0, bestDist - 1) / (double)speed);
            yield return new Candidate
            {
                Command = new MoveCmd(u.Id, cell) { Side = side },
                Turns = turns,
                MovePrice = bestCost,
                Label = $"approach {e.Id} via {cell.X},{cell.Y}",
            };
        }
    }

    static bool Before(Cell a, Cell? b) => b is not { } c || a.Y < c.Y || (a.Y == c.Y && a.X < c.X);

    /// <summary>Stand beside a friendly shooter that an enemy melee stack can reach next turn ("UseDefendShootersMoves").</summary>
    static IEnumerable<Candidate> DefendShootersMoves(Battle b, Unit u, int side, LegalActions la, Dictionary<Cell, int> reach, List<Unit> enemies)
    {
        if (b.CanShoot(u)) yield break;
        foreach (var f in b.State.Living.Where(f => f.Id != u.Id && b.Controller(f) == side && b.CanShoot(f)).OrderBy(f => f.Id))
        {
            bool threatened = enemies.Any(e => !e.IsWarMachine && !b.CanShoot(e)
                && Battle.Distance(e, f) <= b.SpeedOf(e) + 1);
            if (!threatened) continue;
            var cell = la.Moves.Where(c => Battle.Distance(u, c, f, f.Pos) <= 1)
                .OrderBy(c => reach.GetValueOrDefault(c)).ThenBy(c => c.Y).ThenBy(c => c.X).Cast<Cell?>().FirstOrDefault();
            if (cell is { } c0)
                yield return new Candidate { Command = new MoveCmd(u.Id, c0) { Side = side }, MovePrice = reach.GetValueOrDefault(c0), MustBeatStart = true, Label = $"guard shooter {f.Id}" };
        }
    }

    /// <summary>Step next to an enemy shooter to force its melee penalty ("UseBlockEnemyMoves").</summary>
    static IEnumerable<Candidate> BlockEnemyMoves(Battle b, Unit u, int side, LegalActions la, Dictionary<Cell, int> reach, List<Unit> enemies)
    {
        foreach (var e in enemies.Where(e => !e.IsWarMachine && b.CanShoot(e)).OrderBy(e => e.Id))
        {
            var cell = la.Moves.Where(c => Battle.Distance(u, c, e, e.Pos) <= 1)
                .OrderBy(c => reach.GetValueOrDefault(c)).ThenBy(c => c.Y).ThenBy(c => c.X).Cast<Cell?>().FirstOrDefault();
            if (cell is { } c0)
                yield return new Candidate { Command = new MoveCmd(u.Id, c0) { Side = side }, MovePrice = reach.GetValueOrDefault(c0), MustBeatStart = true, Label = $"block shooter {e.Id}" };
        }
    }
}
