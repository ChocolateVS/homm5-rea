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
/// (FUN_008d10e0) runs only when lookAheadTime = round(unit vf 0x174 · combat_ai_lookahead) is not 0 (think +0x20,
/// FUN_008d27a0); the console variable (DAT_00ff699c, registered at 008d761f) is 0 by default, so it is not ported. All randomness
/// comes from simulations on cloned states, so decisions are deterministic (the exe shuffles the survivors
/// with the combat RNG before choosing, which only matters for exact ties; ours keep generation order).
/// Ledger: docs/rules/AUDIT_AI.md, "Combat AI".
/// </summary>
public sealed partial class OriginalBattleAI : IBattleAI
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

    /// <summary>
    /// Per side, the owner's money estimations (combat army vf 0x140 = FUN_004db230: 7 doubles, 0.0 each unless the
    /// side has a player and the combat is not a simulation, then the AI money values FUN_00a31e50 / FUN_00a325b0) used by
    /// the rune purchase test <see cref="BuysRune"/>. Null (or a null side): the exe's 0.0 default.
    /// </summary>
    public double[]?[]? RuneMoneyValues { get; init; }

    /// <summary>
    /// The option `combat_ai_use_magic_for_human` (stock default 0), copied into each army's auto-combat options by
    /// CSetArmyAutocombatOptions (00580600 → army vf 0x148; read back as army vf 0x144 = army +0xb0). Off: the mana limit
    /// FUN_00a2f280 keeps a human side's hero from spending mana (<see cref="ExeManaLimit"/>).
    /// </summary>
    public bool UseMagicForHuman { get; init; }

    /// <summary>A copy for one battle: game difficulty (script scale) and which sides are human.</summary>
    public OriginalBattleAI For(int gameDifficulty, bool[]? humanSides, double[]?[]? runeMoneyValues = null) =>
        new()
        {
            GameDifficulty = gameDifficulty, HumanSides = humanSides, RowOverride = RowOverride, Retreats = Retreats, _stats = _stats,
            UseMagicForHuman = UseMagicForHuman,
            RuneMoneyValues = runeMoneyValues ?? RuneMoneyValues,
        };

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
        /// <summary>Record +0xa8: the actor's mana after the simulated command (actor vf 0x234, FUN_008d5eb0).</summary>
        public int ManaAfter;
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
        // An AI army's tactics phase (004d9520 → army vf 0x44 = 004d95b0): auto placement FUN_0085a190 (the battle-start
        // placement, ExeAutoPlacement), then the phase is ended at once (army vf 0xd0); the AI places nothing itself.
        if (s.Phase == BattlePhase.Tactics) return new EndTacticsCmd { Side = side };
        var la = b.GetLegalActions();
        if (la == null || b.SideToAct != side) return new WaitCmd(Actor.Hero(side)) { Side = side };

        // FUN_00c97930 runs the flee decision (FUN_00c972d0) before every think; the flag becomes a retreat (0054b650).
        LastRetreat = Retreats ? RetreatDecision(b, side) : null;
        if (LastRetreat != null) return new SurrenderCmd { Side = side, PayRansom = false };

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

        int manaLimit = ManaLimit(b, side, la, start, human);
        var cache = new Dictionary<BattleCommand, (CombatValue?, int)>();
        var survivors = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (!cache.TryGetValue(c.Command, out var r))
            {
                var sim = new Battle(b.Db, s.Clone());
                r = sim.Apply(c.Command).Ok ? (CombatValue.Of(sim, side), la.IsHero ? sim.State.Heroes[side].Mana : 0) : (null, 0);
                cache[c.Command] = r;
            }
            if (r.Item1 == null) continue;
            (c.Value, c.ManaAfter) = r;
            if (Excluded(c, start, coefs, mode, manaLimit)) continue;
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
    /// Otherwise (also a type 1 the start does not beat) a spell (record +0xb4) is "Excluded by mana limit" when the
    /// actor's mana after it (record +0xa8) is below the think's limit (think +0x1b0 = settings +0x9c, <see cref="ExeManaLimit"/>).
    /// </summary>
    public static bool Excluded(Candidate c, CombatValue start, CombatModeCoefs coefs, CombatMode mode, int manaLimit = 0)
    {
        if (c.CompareType == 1 && CombatValue.IsBetter(start, c.Value!, coefs, mode)) return true;
        if (c.CompareType == 2 && (CombatValue.IsBetter(start, c.Value!, coefs, mode) || c.Value!.My.Health < start.My.Health)) return true;
        return c.IsSpell && c.ManaAfter < manaLimit;
    }

    /// <summary>
    /// The think's mana limit (FUN_00a30460 → FUN_00a2f280(actor), once per think). A creature has no caster (actor
    /// vf 0x74 = 0: limit 0). For a hero: a computer player (vf 0x18c) → 0; a human player (vf 0x190) whose army's
    /// auto-combat option is off (<see cref="UseMagicForHuman"/>) → the caster's mana M, so every spell that spends mana is
    /// dropped; otherwise the damage rule <see cref="ExeManaLimit"/> on the start value. GAP-detail: M is read as caster
    /// vf 0 → vf 4, not identified; the hero's current mana is used (any M ≥ it gives the same exclusions when the option is off).
    /// </summary>
    int ManaLimit(Battle b, int side, LegalActions la, CombatValue start, bool human)
    {
        if (!la.IsHero || !human) return 0;
        int m = b.State.Heroes[side].Mana;
        if (!UseMagicForHuman) return m;
        return ExeManaLimit(start.My.DamageVs(start.Enemy), (int)start.Enemy.Health, m);
    }

    /// <summary>
    /// FUN_00a2f280's damage rule: d = our damage (FUN_00bfa1b0 of our half vs the enemy's), h = the enemy health
    /// (value [6]), both as floats. d &lt; h → 0; d ≤ 10·h (00e22820) → trunc((1 − h/d) · mana) in single precision; else the whole mana.
    /// </summary>
    public static int ExeManaLimit(int ourDamage, int enemyHealth, int mana)
    {
        float d = ourDamage, h = enemyHealth;
        if (d < h) return 0;
        if (d <= h * 10f) return (int)((1f - h / d) * mana);
        return mana;
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
            bool rune = def.School == "MAGIC_SCHOOL_RUNIC" && hero != null;
            switch (sp.Targeting)
            {
                case Battle.SpellTargeting.None:
                    if (rune && !RunePurchase(b, hero!, def, null, side, start)) break;
                    list.Add(Spell(b, new CastSpellCmd(la.Actor, sp.Spell) { Side = side }, sp.Spell, Array.Empty<Unit>(), sp.Spell));
                    break;
                case Battle.SpellTargeting.Unit:
                    foreach (var t in sp.Targets)
                    {
                        if (rune && !RunePurchase(b, hero!, def, t, side, start)) continue;
                        list.Add(Spell(b, new CastSpellCmd(la.Actor, sp.Spell, t) { Side = side }, sp.Spell, Hit(s, t), $"{sp.Spell}->{t}"));
                    }
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
        // Guard / block moves (FUN_008d64e0 head): only when the combat is not short (think +0x1b7 = settings +0xa3, the
        // combat length evaluation FUN_00b393a0 below 3) and while WE out-shoot the enemy (FUN_00bf9eb0 on the start value:
        // our FUN_00bf9d30 above the enemy's). GAP-detail: the actor vf 0x6c → vf 0x58 (field +0x48) test is not applied.
        bool shortCombat = ShortCombat(b, side, start);
        void GuardAndBlock()
        {
            if (!SideValue.OutShoots(start.My, start.Enemy)) return;
            if (diff.UseDefendShootersMoves) list.AddRange(DefendShooterMoves(b, u, side, reach));
            if (diff.UseBlockEnemyMoves) list.AddRange(BlockEnemyMoves(b, u, side, reach, enemies, diff));
        }
        if (!shortCombat) GuardAndBlock();
        bool defensive = Defensive(b, side, start);
        list.AddRange(Attacks(b, u, side, la, enemies, diff, tooWeak, defensive));
        list.AddRange(GoblinGuardMoves(b, u, side, reach));
        // FUN_008d5b90: approach records only while the strategy is not defensive (think +0x1ac).
        if (list.Count == before && la.Moves.Count > 0 && !defensive)
            list.AddRange(ApproachMoves(b, u, side, la, enemies, diff));
        // FUN_008d68c0: a short combat that added nothing after the spells clears think +0x1b7 and runs FUN_008d64e0 again.
        if (shortCombat && list.Count == before) GuardAndBlock();
        return list;
    }

    /// <summary>
    /// The defensive strategy flag (think +0x1ac = settings +0x98, FUN_00a30460 → FUN_00a2fd40 with the think's nested
    /// flag +0x1c, 0 at the top level; logged "Ranged advantage: %d" / "Strategy: defensive"). With E / O the enemy / own
    /// halves of the start value: part = O.FUN_00bf9d30(E) / O.FUN_00bfc4a0(E), adv = O.FUN_00bf9d30(E) / E.FUN_00bf9d30(O)
    /// (100 when the enemy's is 0); defensive = (<see cref="AllRanged"/>(own army, enemy's 00bf9d30 = 0) or (part &gt;
    /// OurShootersMinRelativePower (RPGStats +0xbfc) and adv &gt; OurShootersMinRelativeAdvantage (+0xc00))) and not
    /// AllRanged(enemy army, our 00bf9d30 = 0). GAP-details: the player's defensive-combat-AI flag (player vf 0x1ec, set
    /// by CSetPlayerDefensiveCombatAI: defensive at once; no stock source of that command was found) is not modelled.
    /// </summary>
    public bool Defensive(Battle b, int side, CombatValue start)
    {
        var st = _stats ??= CombatAiStats.Load(b.Db);
        SideValue e = start.Enemy, o = start.My;
        int oShoot = o.ShooterPowerWithExtras(e), eShoot = e.ShooterPowerWithExtras(o);
        float part = (float)((double)oShoot / o.DamageMeasure(e));
        float adv = eShoot == 0 ? 100f : (float)((double)oShoot / eShoot);
        bool ranged = AllRanged(b, side, eShoot == 0) || part > (float)st.OurShootersMinRelativePower && adv > (float)st.OurShootersMinRelativeAdvantage;
        return ranged && !AllRanged(b, 1 - side, oShoot == 0);
    }

    /// <summary>
    /// FUN_00a2f710(army, flag): every stack of the army shoots (vf 0x1e0) or has a spell potential FUN_00bfa4f0 =
    /// FUN_00bfa360(10, 0) ≥ 1 (<see cref="Battle.AiSpellPotential"/>), or — with the flag — has Strike and Return and No
    /// Enemy Retaliation; true for an army without stacks. The army list is vf 0x6c = 004da670: the creature interfaces of the
    /// army's units (no war machine or hero) that pass FUN_00a6d490 (not vf 0x5c, vf 0x4c ≠ 1: hidden — ours: Invisible — left out).
    /// </summary>
    internal static bool AllRanged(Battle b, int side, bool flag) =>
        b.State.Living.Where(u => u.Side == side && !u.IsObject && !u.IsWarMachine && !u.HasEffect(Fx.Invisible))
            .All(u => flag && u.Has(Ab.StrikeAndReturn) && u.Has(Ab.NoEnemyRetaliation) || b.CanShoot(u) || b.AiSpellPotential(u, null) >= 1);

    /// <summary>
    /// The rune purchase gate on a rune candidate (FUN_008d30e0: a rune (FUN_00a345b0) is generated only when
    /// FUN_008d0960 accepts its cost FUN_00419ad0(caster, 0), i.e. without the Fine Rune roll). The in-combat resources
    /// (army vf 0x138 = army +0x84) are the hero's <see cref="HeroState.Resources"/> (debited at each cast); the player's
    /// purse (player vf 0xd8, debited after the battle) is those plus <see cref="HeroState.ResourcesSpent"/>.
    /// A battle without modelled resources (Resources null: duels, tests) pays nothing for runes and is not gated.
    /// GAP-detail: in the exe the rune is the creature's own non-aimed spell; ours is the hero's cast on a stack.
    /// </summary>
    bool RunePurchase(Battle b, HeroState h, H5.Core.Data.SpellDef def, int? target, int side, CombatValue start)
    {
        if (h.Resources is not { Length: 7 } have) return true;
        var cost = b.RuneCost(h, def, target, fineRune: false);
        var purse = new int[7];
        for (int i = 0; i < 7; i++) purse[i] = have[i] + h.ResourcesSpent[i];
        var money = RuneMoneyValues is { } mv && side < mv.Length ? mv[side] : null;
        return BuysRune(start.MyPower, start.EnemyPower, cost, purse, have, money, _stats!.MaxRuneMoneyEstimate);
    }

    /// <summary>
    /// FUN_008d0960(cost): r = own power / enemy power (FUN_00bf9b80 both ways on the start value, float division).
    /// Refused when wood (resp. ore) is paid and the in-combat resources after paying (FUN_009d0ad0) keep fewer than 15.
    /// Bought when every paid resource is above 49 in the player's purse; otherwise when
    /// scale · Σ cost[i] · money[i] &lt; MaxRuneMoneyEstimate (RPGStats +0xb8c), scale = 0.5 below r = 0.8 (00e1857c),
    /// 7.5r − 5.5 below 2, 6r − 4 below 4, else 20 (constants 00e0ab70, 00e531c4 / 00e531c0, 00e3a3a8 / 00e11c80, 00e531bc).
    /// <paramref name="money"/> null = the 0.0 default of FUN_004db230.
    /// </summary>
    public static bool BuysRune(int ownPower, int enemyPower, int[] cost, int[] purse, int[] inCombat, double[]? money, int maxRuneMoneyEstimate)
    {
        float r = (float)ownPower / (float)enemyPower;
        if (cost[0] > 0 && inCombat[0] - cost[0] < 15) return false;
        if (cost[1] > 0 && inCombat[1] - cost[1] < 15) return false;
        double value = 0;
        bool rich = true;
        for (int i = 0; i < 7; i++)
        {
            if (cost[i] <= 0) continue;
            rich = rich && purse[i] > 49;
            value = cost[i] * (money != null && i < money.Length ? money[i] : 0.0) + value;
        }
        if (rich) return true;
        double scale = !(0.8f <= r) ? 0.5 : !(2.0f <= r) ? r * 7.5 - 5.5 : !(4.0 <= r) ? r * 6.0 - 4.0 : 20.0;
        return scale * value < maxRuneMoneyEstimate;
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
    /// Attack records (FUN_008d64e0 → FUN_008d4330(map think +0xcc, think +0x1bc, 1) → FUN_008d3c40 per enemy, "Attack"). The map
    /// +0xcc is FUN_008d0720(map, 1, 1, 0): every stack blocks (own and enemy), no budget (<see cref="Battle.AiPathMap"/>). Per enemy
    /// stack that FUN_00a6af50 lets through (its visibility vf 0x4c: 1 = hidden is skipped; ours: the Invisible effect), the ring
    /// cells (FUN_00b3f2f0) in map cost order (FUN_008d6dd0); per cell FUN_00c540c0 with the target plans the move twice in
    /// mode 2 (kinds 3 and 1, both must succeed), then in mode 1 (a flyer whose plan fails tries again on map think +0xe4 =
    /// FUN_008d0720(map, 1, 1, 1)). The mode-1 plan within this turn (Turns 0) is the strike from that cell; a plan with Turns ≥ 1
    /// is a move without target to the plan's stop cell (FUN_00c540c0: Turns &gt; 0 in mode 1 → command without target), so every
    /// ring cell the map reaches gives a record, and the comparator FUN_008d0030 (fewer Turns first) prefers any strike now over
    /// moving toward a later one. Record: Turns = the mode-1 count, MovePrice = the map cost of the ring cell (FUN_00b5eb90),
    /// Priority FUN_008d00d0(target, actor, 1), compare type 1 when the target's vf 0x8c ≠ 0 — 00c98940 (returns 0) on the creature
    /// (00e4fba4), hero (00e8499c) and war machine (00e70594) unit vtables, so it stays 0. Under the defensive strategy (think
    /// +0x1ac) a record with Turns &gt; 0 of a stack its owner controls (actor vf 0xc == vf 0x18) is dropped when the stack has Strike
    /// and Return and No Enemy Retaliation (FUN_00a2f130) and the record is not "Move-no-attack", or when think +0x1bd is set.
    /// With Strike and Return and not too weak (think +0x1b4) a second pass over the ring makes the "Move-no-attack" records
    /// (FUN_008d3c40 arg 4 = 1). ConsiderOnlyShortestPathToEnemy (row +0xb) or think level 2 ends a pass at its first record.
    /// GAP-details: think +0x1bd (settings +0xa9 = FUN_00a2fa20(actor, 1): a unit next to the actor (FUN_00b3e620 → list of actor
    /// vf 0xc's vf 0x60 filtered by FUN_00a6d4d0, adjacency FUN_00b3d4e0) that shoots, or with the actor inside that object's vf
    /// 0xb0 rectangle a neighbour's neighbour (recursion, flag 0); the object behind actor vf 0xc is not identified) is not applied;
    /// a "Move-no-attack" record with Turns 0 (a strike without the return) has no command here and is left out; the enemy gates
    /// FUN_00b3e1c0 (think +0x1bc), the vf 0x80 record +0x1c == 2 skip (think +0x1b6) and the adjacency / think +0x1b7 /
    /// FUN_008d0440 skip are not applied; the map's moat stop (map +0x10 = 2 when think +0x1c8 is clear: FUN_00b5f0d0 does not expand
    /// a non-start cell whose flag bit 0x1000000 (+7 bit 0, the second grid FUN_00b3cf30(combat, actor) of FUN_00b5ebd0) is set) and
    /// the siege attacker's second pass on FUN_00c52e20(0, 1, 1, 0) (think +0x1c8) are not modelled (the same map here).
    /// </summary>
    IEnumerable<Candidate> Attacks(Battle b, Unit u, int side, LegalActions la, List<Unit> enemies, CombatDifficulty diff, bool tooWeak, bool defensive)
    {
        var map = b.AiPathMap(u, true, true, false);
        (Dictionary<Cell, int>, Dictionary<Cell, Cell>)? mapE4 = null;
        int Cost(Cell c) => map.Dist.TryGetValue(c, out var v) ? v : Unreachable;
        bool strikeAndReturn = u.Has(Ab.StrikeAndReturn);
        bool harpy = strikeAndReturn && u.Has(Ab.NoEnemyRetaliation);       // FUN_00a2f130
        bool owned = b.Controller(u) == u.Side;
        foreach (var t in enemies)
        {
            if (t.HasEffect(Fx.Invisible)) continue;                            // FUN_00a6af50
            for (int pass = 0; pass < (strikeAndReturn && !tooWeak ? 2 : 1); pass++)
            {
                bool moveNoAttack = pass == 1;
                foreach (var (cell, cost) in SortedRing(t, u, Cost))
                {
                    if (PlanMove(b, u, cell, 2, map, true) == null) continue;
                    var plan = PlanMove(b, u, cell, 1, map, true);
                    if (plan == null && u.Flying) plan = PlanMove(b, u, cell, 1, mapE4 ??= b.AiPathMap(u, true, true, true), true);
                    if (plan is not { } p) continue;
                    BattleCommand cmd;
                    if (p.Turns == 0)
                    {
                        // The strike from the ring cell; our engine's legality decides (a cell it refuses gives no record).
                        if (moveNoAttack || !la.Melee.TryGetValue(t.Id, out var cells) || !cells.Contains(cell)) continue;
                        cmd = new AttackCmd(u.Id, t.Id, cell == u.Pos ? null : cell) { Side = side };
                    }
                    else
                    {
                        if (p.Stop == u.Pos) continue;
                        if (defensive && owned && (harpy && !moveNoAttack)) continue;
                        cmd = new MoveCmd(u.Id, p.Stop) { Side = side };
                    }
                    yield return new Candidate
                    {
                        Command = cmd, Turns = p.Turns, MovePrice = cost, Priority = TargetPriority(b, t, u, true, p.Turns, cost),
                        Label = (moveNoAttack ? "move-no-attack->" : "attack->") + $"{t.Id} via {cell.X},{cell.Y}",
                    };
                    if (diff.ConsiderOnlyShortestPathToEnemy) break;
                }
            }
        }
    }

    /// <summary>
    /// The goblin pass at the end of FUN_008d64e0 (FUN_008d4b30, records named "Defend shooter"): for every other stack of the
    /// own army (think +8 vf 0x6c) for which FUN_008d03a0(stack, actor) holds — both have a creature interface (vf 0x6c), the
    /// stack's family FUN_004babf0 is 0x75 CREATURE_GOBLIN and the actor has ABILITY_SACRIFICE_GOBLIN (0x72),
    /// ABILITY_SWALLOW_GOBLIN (0x79) or ABILITY_GOBLIN_THROWER (0x7a) — FUN_008d4070 on map think +0xcc, the helper of the
    /// shooter guard (<see cref="DefendShooterMoves"/>): a move to each ring cell around it, Turns 1. Not gated by the shooting
    /// test or think +0x1b7. GAP-detail: FUN_008d4070 is ported as the shooter guard is (cells reachable this turn).
    /// </summary>
    IEnumerable<Candidate> GoblinGuardMoves(Battle b, Unit u, int side, Dictionary<Cell, int> reach)
    {
        if (u.IsWarMachine || !(u.Has(Ab.SacrificeGoblin) || u.Has(Ab.SwallowGoblin) || u.Has(Ab.GoblinThrower))) yield break;
        int Cost(Cell c) => reach.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var g in b.State.Living.Where(g => g.Id != u.Id && !g.IsObject && !g.IsWarMachine && g.Side == side && b.AiGoblinFamily(g)))
            foreach (var (cell, cost) in SortedRing(g, u, Cost))
            {
                if (cost == Unreachable) continue;
                yield return new Candidate
                {
                    Command = new MoveCmd(u.Id, cell) { Side = side }, Turns = 1, MovePrice = cost, Label = $"defend goblin {g.Id}",
                };
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
}
