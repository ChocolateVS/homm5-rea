namespace H5.Combat.AI;

/// <summary>
/// Port of the original combat AI (NWorld::CCombatAI; think loop FUN_008d6a70). Per decision it
/// 1. picks the think level (FUN_008d0840) and from it the AI/Combat Difficulty* row (FUN_00a2f0d0),
/// 2. builds candidate commands for the acting unit or hero (FUN_008d6940 and the generators it calls),
/// 3. simulates each on a copy of the battle and scores the resulting position with the two-sided
///    combat value (FUN_008d5eb0 → FUN_00bfc2a0, see <see cref="CombatValue"/>),
/// 4. drops candidates whose compare type rules them out (FUN_008d1cb0 "Excluded by start value" /
///    "…by start health value" / "…by mana limit"),
/// 5. picks the best survivor with the original ordering (FUN_008d0030): fewer turns, lower priority,
///    better combat value, non-spell over spell, cheaper move; and defends when nothing survives
///    ("End of choose. Chose defend.").
/// Every generated candidate is evaluated: the exe has no candidate cap. Look-ahead search
/// (lookAheadTime &gt; 0, FUN_008d10e0) is not used for normal AI turns and is not ported. All randomness
/// comes from simulations on cloned states, so decisions are deterministic (the exe shuffles the survivors
/// with the combat RNG before choosing, which only matters for exact ties; ours keep generation order).
/// Ledger: docs/rules/AUDIT_AI.md, "Combat AI".
/// </summary>
public sealed class OriginalBattleAI : IBattleAI
{
    /// <summary>
    /// Game difficulty on the script scale (AdvState.Difficulty: 0 Easy, 1 Normal, 2 Hard, 3 Heroic). The exe reads
    /// the engine value through the combat (vf 0x38 → CCombat+0x94) in FUN_008d0840.
    /// </summary>
    public int GameDifficulty { get; init; } = 1;
    /// <summary>
    /// Which sides belong to a human player (the exe asks the side's player, vf 0x190). Null: a side is human unless
    /// <see cref="BattleState.AiControlled"/> marks it as a computer side.
    /// </summary>
    public bool[]? HumanSides { get; init; }
    /// <summary>Forces a Difficulty* row (tools and tests); null uses the exe's choice.</summary>
    public AiDifficulty? RowOverride { get; init; }

    CombatAiStats? _stats;

    /// <summary>A copy for one battle: game difficulty (script scale) and which sides are human.</summary>
    public OriginalBattleAI For(int gameDifficulty, bool[]? humanSides) =>
        new() { GameDifficulty = gameDifficulty, HumanSides = humanSides, RowOverride = RowOverride, _stats = _stats };

    public sealed class Candidate
    {
        public required BattleCommand Command;
        /// <summary>Turns before the action takes effect (record +0xc, 0 by default; approach and guard moves ≥ 1).</summary>
        public int Turns;
        /// <summary>Rank (record +0xac); lower wins. Default 2 (FUN_008d7550); generators set it with FUN_008d00d0.</summary>
        public int Priority = 2;
        public int MovePrice;
        public bool IsSpell;
        /// <summary>Record +0xb0: 1 = must not be worse than the start position, 2 = also keep own HP, 0 = always kept.</summary>
        public int CompareType;
        public CombatValue? Value;
        public string Label = "";
    }

    /// <summary>The last decision's candidates, for logging and tests.</summary>
    public IReadOnlyList<Candidate> LastCandidates { get; private set; } = Array.Empty<Candidate>();
    public CombatMode LastMode { get; private set; }
    /// <summary>The think level and row used by the last decision.</summary>
    public int LastLevel { get; private set; }
    public AiDifficulty LastRow { get; private set; }

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
        var coefs = _stats.CompareCoefs;
        bool human = HumanSides is { } hs ? hs[side] : !s.AiControlled[side];
        LastLevel = ExeThinkLevel(human, EngineDifficulty(GameDifficulty));
        LastRow = RowOverride ?? ExeRow(LastLevel);
        var diff = _stats.Difficulty[(int)LastRow];

        // FUN_00a2f430 ("We are too weak to win", settings +0xa0 → think +0x1b4).
        bool tooWeak = TooWeak(start);
        var candidates = Generate(b, la, side, diff, tooWeak, start);
        // FUN_008d6940: nothing generated with the restricting switches on → clear them and generate again.
        if (candidates.Count == 0 && tooWeak) candidates = Generate(b, la, side, diff, false, start);

        var cache = new Dictionary<BattleCommand, CombatValue?>();
        var survivors = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (!cache.TryGetValue(c.Command, out var v))
            {
                var sim = new Battle(b.Db, s.Clone());
                v = sim.Apply(c.Command).Ok ? CombatValue.Of(sim, side) : null;
                cache[c.Command] = v;
            }
            if (v == null) continue;
            c.Value = v;
            if (Excluded(c, start, coefs, mode)) continue;
            survivors.Add(c);
        }
        LastCandidates = candidates;
        if (survivors.Count == 0) return Defend(la, side);

        var best = survivors[0];
        for (int i = 1; i < survivors.Count; i++)
            if (CandidateBetter(best, survivors[i], coefs, mode)) best = survivors[i];
        return best.Command;
    }

    // ---------------------------------------------------------------- think level and row

    /// <summary>Engine difficulty (0 Normal, 1 Easy, 2 Hard, 3 Heroic) from the script scale; inverse of the script map 00a33780.</summary>
    public static int EngineDifficulty(int script) => script switch { 0 => 1, 1 => 0, 2 => 2, _ => 3 };

    /// <summary>
    /// Think level (think +0x18, set to 4 by the constructor FUN_008d27a0, then FUN_008d0840): a nested look-ahead
    /// think → 2 (not ported); combat vf 0x110 (CCombat+0x138) 1 → 3, 2 → 1 (never set by our battles); otherwise a
    /// human side → max(level, 5) = 5, a computer side → FUN_004f66c0(engine difficulty): 0, 1 → 0; 2 → 4; 3 → 5; 4 → 6.
    /// </summary>
    public static int ExeThinkLevel(bool humanSide, int engineDifficulty)
    {
        if (humanSide) return 5;
        return engineDifficulty switch { 0 or 1 => 0, 3 => 5, 4 => 6, _ => 4 };
    }

    /// <summary>
    /// Row getter FUN_00a2f0d0: level ≤ 3 → DifficultyNormal (RPGStats+0xb60); level ≥ 4 → DifficultyHeroic (+0xb78).
    /// Its middle branch (3 &lt; level &lt; 4 → DifficultyHard, +0xb6c) can never be taken and DifficultyEasy (+0xb54) is never read.
    /// </summary>
    public static AiDifficulty ExeRow(int level) => level <= 3 ? AiDifficulty.Normal : AiDifficulty.Heroic;

    // ---------------------------------------------------------------- mode, exclusion, comparator

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

    /// <summary>FUN_00a2f430: our/enemy power ratio below the (clamped) KamikazeModeTreshold → "We are too weak to win".</summary>
    bool TooWeak(CombatValue start)
    {
        int enemy = start.EnemyPower;
        if (enemy <= 0) return false;
        double t = Math.Max(0, Math.Min(_stats!.KamikazeModeThreshold, 1000));
        return start.MyPower / (double)enemy < t;
    }

    /// <summary>
    /// FUN_008d1cb0 exclusions. Compare type 1: dropped when the start position is better (FUN_00bfae20(candidate, start));
    /// type 2: also dropped when own HP (value [6] of the own half, record +0x74 vs think +0x98) falls below the start's.
    /// The mana-limit rule (spell flag and record +0xa8 &lt; think +0x1b0) is a GAP (limit FUN_00a2f280 not ported: 0).
    /// </summary>
    public static bool Excluded(Candidate c, CombatValue start, CombatModeCoefs coefs, CombatMode mode)
    {
        if (c.CompareType == 1) return CombatValue.IsBetter(start, c.Value!, coefs, mode);
        if (c.CompareType == 2) return CombatValue.IsBetter(start, c.Value!, coefs, mode) || c.Value!.My.Health < start.My.Health;
        return false;
    }

    /// <summary>True when <paramref name="cand"/> should replace <paramref name="cur"/> (exe FUN_008d0030(cur, cand)).</summary>
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

    // ---------------------------------------------------------------- spell tables

    /// <summary>Exe spell name of a spell or ability id (creature abilities are SPELL_ABILITY_* spells in the exe).</summary>
    static string ExeName(string id) =>
        id.StartsWith("SPELL_") ? id : "SPELL_ABILITY_" + (id.StartsWith("ABILITY_") ? id["ABILITY_".Length..] : id);

    /// <summary>Compare type of a spell candidate (FUN_008d02b0, the same in every spell generator).</summary>
    public static int SpellCompareType(string id) => ExeName(id) switch
    {
        "SPELL_ABILITY_RESURRECT_ALLIES" or "SPELL_ABILITY_SCATTER_SHOT" or "SPELL_ABILITY_GATING" or "SPELL_ABILITY_SUMMON_BALOR"
            or "SPELL_ABILITY_EXPLOSION" or "SPELL_ABILITY_RANDOM_CAST_DARK" or "SPELL_ABILITY_RANDOM_CAST_DARK_LIGHT"
            or "SPELL_ABILITY_DASH" or "SPELL_WARCRY_WORD_OF_THE_CHIEF" or "SPELL_ABILITY_DEATH_WAIL"
            or "SPELL_ABILITY_ORDER_OF_THE_CHIEF" => 1,
        "SPELL_ABILITY_BERSERKER_RAGE" or "SPELL_ABILITY_SUMMON_OTHER" => 2,
        _ => 0,
    };

    /// <summary>
    /// Spells allowed while "too weak" (FUN_008d6670 with think +0x1b4): FUN_008d0bd0 — a damage spell (FUN_00977c80) or
    /// one of BATTLE_DIVE, SCATTER_SHOT, EXPLOSION, BERSERKER_RAGE, STORMBOLT, DEATH_WAIL, LEAP, PACK_DIVE — or INVISIBILITY.
    /// </summary>
    public static bool AllowedWhenTooWeak(Battle b, string id, HeroState? hero) => b.ExeDamageSpell(id, hero) || ExeName(id) is
        "SPELL_ABILITY_BATTLE_DIVE" or "SPELL_ABILITY_SCATTER_SHOT" or "SPELL_ABILITY_EXPLOSION" or "SPELL_ABILITY_BERSERKER_RAGE"
        or "SPELL_ABILITY_STORMBOLT" or "SPELL_ABILITY_DEATH_WAIL" or "SPELL_ABILITY_LEAP" or "SPELL_ABILITY_PACK_DIVE"
        or "SPELL_ABILITY_INVISIBILITY";

    /// <summary>Area spells that get their own centre search (FUN_008d04c0 kinds 1 and 2) and have an area in our engine.</summary>
    static bool AreaSpell(string id) => id is "SPELL_FIREBALL" or "SPELL_FROST_RING" or "SPELL_METEOR_SHOWER" or "SPELL_STONE_SPIKES";

    // ---------------------------------------------------------------- candidate generation (FUN_008d6940)

    List<Candidate> Generate(Battle b, LegalActions la, int side, CombatDifficulty diff, bool tooWeak, CombatValue start)
    {
        var s = b.State;
        var list = new List<Candidate>();
        var enemies = s.Living.Where(e => b.Controller(e) != side && !e.IsObject).ToList();
        var hero = la.IsHero ? s.Heroes[side] : null;

        // Spells and abilities (FUN_008d6670).
        foreach (var sp in la.Spells)
        {
            var def = b.Db.Spell(sp.Spell);
            if (def == null || def.Level > diff.MaxSpellLevelToCast) continue;
            if (tooWeak && !AllowedWhenTooWeak(b, sp.Spell, hero)) continue;
            switch (sp.Targeting)
            {
                case Battle.SpellTargeting.None:
                    list.Add(Spell(b, new CastSpellCmd(la.Actor, sp.Spell) { Side = side }, sp.Spell, Array.Empty<Unit>(), sp.Spell));
                    break;
                case Battle.SpellTargeting.Unit:
                    foreach (var t in sp.Targets)
                        list.Add(Spell(b, new CastSpellCmd(la.Actor, sp.Spell, t) { Side = side }, sp.Spell, Hit(s, t), $"{sp.Spell}->{t}"));
                    break;
                case Battle.SpellTargeting.Cell:
                    foreach (var (cell, hit) in SpellCells(b, sp.Spell, side, enemies, diff))
                        list.Add(Spell(b, new CastSpellCmd(la.Actor, sp.Spell, null, cell) { Side = side }, sp.Spell, hit, $"{sp.Spell}@{cell.X},{cell.Y}"));
                    break;
            }
        }
        foreach (var ab in la.Abilities)
        {
            if (tooWeak && !AllowedWhenTooWeak(b, ab.Ability, hero)) continue;
            switch (ab.Targeting)
            {
                case Battle.AbilityTargeting.None:
                    list.Add(Spell(b, new UseAbilityCmd(la.Actor, ab.Ability) { Side = side }, ab.Ability, Array.Empty<Unit>(), ab.Ability));
                    break;
                case Battle.AbilityTargeting.Cell:
                    foreach (var (cell, hit) in SpellCells(b, ab.Ability, side, enemies, diff))
                        list.Add(Spell(b, new UseAbilityCmd(la.Actor, ab.Ability, null, cell) { Side = side }, ab.Ability, hit, $"{ab.Ability}@{cell.X},{cell.Y}"));
                    break;
                default:
                    foreach (var t in ab.Targets)
                        list.Add(Spell(b, new UseAbilityCmd(la.Actor, ab.Ability, t) { Side = side }, ab.Ability, Hit(s, t), $"{ab.Ability}->{t}"));
                    break;
            }
        }

        if (la.IsHero)
        {
            // Hero turn (FUN_008d2e90 "Hero melee"): a strike at every valid target, priority FUN_008d00d0(target, hero, 0).
            foreach (var t in la.HeroAttack)
                list.Add(new Candidate
                {
                    Command = new HeroAttackCmd(t) { Side = side }, Label = $"hero->{t}",
                    Priority = s.Unit(t) is { } tu ? TargetPriority(b, tu, null, false, 0, 0) : 2,
                });
            return list;
        }

        var u = s.Unit(la.Actor)!;
        // Shooters (FUN_008d6850 → FUN_008d3950 "Shoot"): spells and one shot per target, nothing else.
        if (la.Shoot.Count > 0)
        {
            foreach (var t in la.Shoot)
                list.Add(new Candidate
                {
                    Command = new ShootCmd(u.Id, t) { Side = side }, Label = $"shoot->{t}",
                    Priority = s.Unit(t) is { } tu ? TargetPriority(b, tu, u, false, 0, 0) : 2,
                });
            return list;
        }

        // Melee (FUN_008d68c0 → FUN_008d64e0; FUN_008d5b90 when that adds nothing).
        var reach = b.Reachable(u);
        int before = list.Count;
        // Guard / block moves (FUN_008d64e0 head): only while the enemy out-shoots us (FUN_00bf9eb0 on the start value).
        if (start.My.ShooterPower(start.Enemy) < start.Enemy.ShooterPower(start.My))
        {
            if (diff.UseDefendShootersMoves) list.AddRange(DefendShooterMoves(b, u, side, reach));
            if (diff.UseBlockEnemyMoves) list.AddRange(BlockEnemyMoves(b, u, side, reach, enemies, diff));
        }
        list.AddRange(Attacks(b, u, side, la, reach, enemies, diff));
        if (list.Count == before && la.Moves.Count > 0)
            list.AddRange(ApproachMoves(b, u, side, la, enemies, diff));
        return list;
    }

    static IReadOnlyList<Unit> Hit(BattleState s, int target) => s.Unit(target) is { } t ? new[] { t } : Array.Empty<Unit>();

    // Creature abilities are spells in the exe (spell flag record +0xb4 set).
    Candidate Spell(Battle b, BattleCommand cmd, string id, IReadOnlyList<Unit> hit, string label) => new()
    {
        Command = cmd, IsSpell = true, CompareType = SpellCompareType(id), Label = label, Priority = SpellPriority(b, hit),
    };

    /// <summary>FUN_008d08f0: start at 2, take the minimum of FUN_008d00d0(target, caster, 0) over the affected stacks.</summary>
    int SpellPriority(Battle b, IReadOnlyList<Unit> hit)
    {
        int p = 2;
        foreach (var t in hit) p = Math.Min(p, TargetPriority(b, t, null, false, 0, 0));
        return p;
    }

    /// <summary>
    /// FUN_008d00d0(target, attacker, flag) with the candidate's Turns / MovePrice. War machines: flag 0 → 1; flag 1 → 2
    /// unless the machine's HP ≤ 2 × the attacker's expected damage, then Ballista 0, other machines 1 (Ammo Cart 2 when
    /// FUN_00a2f8f0 holds: not ported). A creature target: 1 when the candidate needs Turns &gt; 0 and MovePrice &gt; 0 and
    /// round(initiative·speed) of the attacker &lt; round(initiative·speed·2/3) of the target, else 0. GAP-detail: the
    /// creature vf 0x58 rule (→ 1 unless SPELL_PHANTOM) is not applied; expected damage is our mean damage × count.
    /// </summary>
    int TargetPriority(Battle b, Unit t, Unit? attacker, bool flag, int turns, int movePrice)
    {
        if (t.IsWarMachine)
        {
            if (!flag || attacker == null) return 1;
            var (min, max) = b.DamageRangeOf(attacker);
            double dmg = attacker.Count * (min + max) * 0.5;
            if (t.TotalHealth > dmg * 2) return 2;
            return t.CreatureId == "WAR_MACHINE_BALLISTA" ? 0 : 1;
        }
        if (t.IsObject) return 2;
        if (attacker != null && movePrice > 0 && turns > 0)
        {
            int mine = (int)Math.Round(b.InitiativeOf(attacker) * b.SpeedOf(attacker));
            int theirs = (int)Math.Round(b.InitiativeOf(t) * b.SpeedOf(t) * (2.0 / 3.0));
            return mine < theirs ? 1 : 0;
        }
        return 0;
    }

    /// <summary>
    /// Centres for a cell-targeted spell (FUN_008d61b0). With CenterAreaSpellsOnCreatureOnly (except Frost Ring), or a
    /// spell without a centre search, the single-target generator FUN_008d3380 is used: every enemy creature's cell.
    /// Otherwise FUN_008d12a0: every cell of the field is tried as the centre; the stacks it would hit are split into
    /// own and enemy sets, a centre hitting no enemy is dropped, and only centres not dominated by another one survive
    /// (dominated: hits a subset of its enemies and a superset of its own stacks, FUN_008d6ce0; equal sets keep the first).
    /// </summary>
    internal static List<(Cell, IReadOnlyList<Unit>)> SpellCells(Battle b, string id, int side, List<Unit> enemies, CombatDifficulty diff)
    {
        var s = b.State;
        var result = new List<(Cell, IReadOnlyList<Unit>)>();
        if (!AreaSpell(id) || diff.CenterAreaSpellsOnCreatureOnly && id != "SPELL_FROST_RING")
        {
            foreach (var e in enemies.Where(e => !e.IsWarMachine).OrderBy(e => e.Pos.Y).ThenBy(e => e.Pos.X))
                if (result.All(r => r.Item1 != e.Pos)) result.Add((e.Pos, new[] { e }));
            return result;
        }
        var best = new List<(Cell C, HashSet<int> E, HashSet<int> F, List<Unit> Hit)>();
        for (int x = 0; x < s.Width; x++)
        for (int y = 0; y < s.Height; y++)
        {
            var c = new Cell(x, y);
            var area = Battle.AreaCells(id, c);
            var hit = s.Living.Where(u => !u.IsObject && u.CellsAt(u.Pos).Any(area.Contains)).ToList();
            var en = hit.Where(u => b.Controller(u) != side).Select(u => u.Id).ToHashSet();
            var fr = hit.Where(u => b.Controller(u) == side).Select(u => u.Id).ToHashSet();
            if (en.Count == 0) continue;
            bool dominated = false;
            for (int i = 0; i < best.Count; i++)
            {
                if (en.IsSubsetOf(best[i].E) && best[i].F.IsSubsetOf(fr)) { dominated = true; break; }
                if (best[i].E.IsSubsetOf(en) && fr.IsSubsetOf(best[i].F)) best.RemoveAt(i--);
            }
            if (!dominated) best.Add((c, en, fr, hit));
        }
        foreach (var r in best) result.Add((r.C, r.Hit));
        return result;
    }

    /// <summary>
    /// Anchor cells from which a stack of size <paramref name="size"/> stands next to <paramref name="t"/>, in the order of
    /// FUN_00b3d010: right column, top row (right to left), left column, bottom row (left to right).
    /// </summary>
    public static List<Cell> Ring(Unit t, int size)
    {
        int px = t.Pos.X, py = t.Pos.Y, ts = t.Size;
        var r = new List<Cell>();
        for (int dy = 1 - size; dy <= ts - 1; dy++) r.Add(new Cell(px + ts, py + dy));
        for (int dx = ts; dx >= -size; dx--) r.Add(new Cell(px + dx, py + ts));
        for (int dy = ts - 1; dy >= 1 - size; dy--) r.Add(new Cell(px - size, py + dy));
        for (int dx = -size; dx <= ts; dx++) r.Add(new Cell(px + dx, py - size));
        return r;
    }

    const int Unreachable = 0xffffff;

    /// <summary>Ring cells sorted by path cost (FUN_008d6dd0: stable merge sort on the path map; unreachable last).</summary>
    static List<(Cell Cell, int Cost)> SortedRing(Unit t, Unit u, Func<Cell, int> cost) =>
        Ring(t, u.Size).Select(c => (c, cost(c))).OrderBy(p => p.Item2).ToList();

    /// <summary>
    /// Attacks (FUN_008d4330 "Attack" → FUN_008d3c40): for every enemy, the ring cells in cost order; each cell the unit
    /// may strike from this turn is a candidate (Turns 0, MovePrice = path cost). With ConsiderOnlyShortestPathToEnemy
    /// only the first valid cell per enemy.
    /// </summary>
    IEnumerable<Candidate> Attacks(Battle b, Unit u, int side, LegalActions la, Dictionary<Cell, int> reach, List<Unit> enemies, CombatDifficulty diff)
    {
        int Cost(Cell c) => c == u.Pos ? 0 : reach.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var t in enemies)
        {
            if (!la.Melee.TryGetValue(t.Id, out var cells)) continue;
            var order = SortedRing(t, u, Cost).Where(p => cells.Contains(p.Cell)).Select(p => p.Cell).ToList();
            // Strike cells our engine allows outside the ring (none for square footprints) come after it.
            order.AddRange(cells.Where(c => !order.Contains(c)).OrderBy(Cost));
            foreach (var from in order)
            {
                int price = Cost(from);
                yield return new Candidate
                {
                    Command = new AttackCmd(u.Id, t.Id, from == u.Pos ? null : from) { Side = side },
                    MovePrice = price, Priority = TargetPriority(b, t, u, true, 0, price),
                    Label = $"attack->{t.Id} from {from.X},{from.Y}",
                };
                if (diff.ConsiderOnlyShortestPathToEnemy) break;
            }
        }
    }

    /// <summary>
    /// Block enemy (FUN_008d4500): for every enemy that can shoot, FUN_008d3c40 without the attack — moves to the ring
    /// cells around it reachable this turn (Turns 0), first only with ConsiderOnlyShortestPathToEnemy.
    /// GAP-detail: the gate on the enemy side's vf 0x1a0 and the FUN_00b3e1c0 filter (settings +0xa8) are not applied.
    /// </summary>
    IEnumerable<Candidate> BlockEnemyMoves(Battle b, Unit u, int side, Dictionary<Cell, int> reach, List<Unit> enemies, CombatDifficulty diff)
    {
        int Cost(Cell c) => reach.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var e in enemies.Where(e => b.CanShoot(e)))
            foreach (var (cell, cost) in SortedRing(e, u, Cost))
            {
                if (cost == Unreachable) continue;
                yield return new Candidate
                {
                    Command = new MoveCmd(u.Id, cell) { Side = side }, MovePrice = cost,
                    Priority = TargetPriority(b, e, u, true, 0, cost), Label = $"block shooter {e.Id}",
                };
                if (diff.ConsiderOnlyShortestPathToEnemy) break;
            }
    }

    /// <summary>
    /// Defend shooter (FUN_008d58c0 → FUN_008d4070): for every other friendly stack that can shoot and lacks
    /// NO_MELEE_PENALTY, a move to each ring cell around it reachable this turn; Turns = 0 + 1, Priority 2 (default),
    /// MovePrice = path cost. GAP-detail: the extra rage candidate (settings +0xa9) is not ported.
    /// </summary>
    IEnumerable<Candidate> DefendShooterMoves(Battle b, Unit u, int side, Dictionary<Cell, int> reach)
    {
        int Cost(Cell c) => reach.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var f in b.State.Living.Where(f => f.Id != u.Id && !f.IsObject && b.Controller(f) == side && b.CanShoot(f) && !f.Has(Ab.NoMeleePenalty)))
            foreach (var (cell, cost) in SortedRing(f, u, Cost))
            {
                if (cost == Unreachable) continue;
                yield return new Candidate
                {
                    Command = new MoveCmd(u.Id, cell) { Side = side }, Turns = 1, MovePrice = cost, Label = $"guard shooter {f.Id}",
                };
            }
    }

    /// <summary>
    /// Approach (FUN_008d5b90 → FUN_008d4330 with the whole-path map): for every enemy, the ring cells in whole-path cost
    /// order; each reachable one gives a move along the path as far as this turn allows, Turns = turns still needed
    /// (cost over the per-turn budget), MovePrice = whole-path cost; first only with ConsiderOnlyShortestPathToEnemy.
    /// GAP-detail: the path planner FUN_00c53f60 / FUN_00c540c0 (turn count, where the move stops) is approximated by a
    /// budget-free Dijkstra on our grid; the strategy gate (think +0x1ac, FUN_00a2fd40) and the siege branch
    /// (FUN_008d4610) are not ported.
    /// </summary>
    IEnumerable<Candidate> ApproachMoves(Battle b, Unit u, int side, LegalActions la, List<Unit> enemies, CombatDifficulty diff)
    {
        int budget = Math.Max(1, b.SpeedOf(u)) * 100;
        var (dist, prev) = WholePath(b, u);
        var moves = la.Moves.ToHashSet();
        int Cost(Cell c) => dist.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var e in enemies)
            foreach (var (cell, cost) in SortedRing(e, u, Cost))
            {
                if (cost == Unreachable || cell == u.Pos) continue;
                // Walk back from the ring cell to the last cell of the path this turn's budget reaches (a flyer: the
                // reachable cell nearest to the ring cell).
                var stop = cell;
                if (u.Flying)
                    stop = moves.Count == 0 ? u.Pos : moves.OrderBy(m => Battle.Octile(m, cell)).ThenBy(m => Battle.Octile(u.Pos, m))
                        .ThenBy(m => m.Y).ThenBy(m => m.X).First();
                else
                    while (stop != u.Pos && (dist[stop] > budget || !moves.Contains(stop))) stop = prev[stop];
                if (stop == u.Pos) continue;
                int turns = Math.Max(1, (cost + budget - 1) / budget - 1);
                yield return new Candidate
                {
                    Command = new MoveCmd(u.Id, stop) { Side = side }, Turns = turns, MovePrice = cost,
                    Priority = TargetPriority(b, e, u, true, turns, cost), Label = $"approach {e.Id} via {cell.X},{cell.Y}",
                };
                if (diff.ConsiderOnlyShortestPathToEnemy) break;
            }
    }

    /// <summary>Whole-path costs over the field ignoring the move budget (flyers: straight octile distance).</summary>
    static (Dictionary<Cell, int>, Dictionary<Cell, Cell>) WholePath(Battle b, Unit u)
    {
        var s = b.State;
        var dist = new Dictionary<Cell, int> { [u.Pos] = 0 };
        var prev = new Dictionary<Cell, Cell>();
        if (u.Flying)
        {
            for (int x = 0; x <= s.Width - u.Size; x++)
            for (int y = 0; y <= s.Height - u.Size; y++)
            {
                var p = new Cell(x, y);
                if (p == u.Pos || !b.Fits(u, p)) continue;
                dist[p] = Battle.Octile(u.Pos, p);
                prev[p] = u.Pos;
            }
            return (dist, prev);
        }
        var open = new SortedSet<(int Cost, int X, int Y)> { (0, u.Pos.X, u.Pos.Y) };
        while (open.Count > 0)
        {
            var (cost, cx, cy) = open.Min;
            open.Remove(open.Min);
            var cur = new Cell(cx, cy);
            if (cost > dist[cur]) continue;
            foreach (var d in Cell.Neighbours8)
            {
                var n = cur + d;
                bool diag = d.X != 0 && d.Y != 0;
                int nc = cost + (diag ? Battle.DiagCost : Battle.OrthCost);
                if (!b.Fits(u, n)) continue;
                if (diag && !b.Fits(u, new Cell(cur.X + d.X, cur.Y)) && !b.Fits(u, new Cell(cur.X, cur.Y + d.Y))) continue;
                if (dist.TryGetValue(n, out var old) && old <= nc) continue;
                dist[n] = nc;
                prev[n] = cur;
                open.Add((nc, n.X, n.Y));
            }
        }
        return (dist, prev);
    }
}
