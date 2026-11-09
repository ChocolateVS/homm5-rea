using H5.Core.Data;
namespace H5.Combat;

// Combat audit sweep 18 (docs/rules/audit/combat_sweep_18.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    internal const string AvatarOfDeathSpell = "SPELL_ABILITY_AVATAR_OF_DEATH";
    const string AvatarOfDeathMark = "SUMMON_SPELL:" + AvatarOfDeathSpell;
    Cell? _avatarOfDeathPos;

    /// <summary>
    /// The knowledge term of a unique summon's health (the summon info's +0x2c): the caster's hero object (vf 0x74 for
    /// Conjure Phoenix 00861fee, vf 0x78 for Avatar of Death 0086234f — both 00bc2b20 on the combat hero vtable 00e8499c,
    /// then vf 0 = 00819270) asked for vf 0x1c (Knowledge, 00b4cd80: at least 1), or 1 without one.
    /// </summary>
    static int UniqueSummonKnowledge(HeroState? h) => h == null ? 1 : Math.Max(1, h.Knowledge);

    /// <summary>
    /// The summon position FUN_00860f20(out, combat; attacker flag (FUN_00af1c60: the caster's army is combat vf 0x84(1)),
    /// creature size (record +0xdc)): the free-anchor grid FUN_00b3fac0(0, size, 0, 0) (obstacles and every living unit,
    /// dilated for size &gt; 1), then the field columns from the caster's own edge — exe x = 2 … W − 3 for the attacker,
    /// W − 3 … 2 for the defender (W = the field width vf 0x44, two border columns each side) — and in each the rows
    /// y = 1 … H − 2 top down; the first column with a free anchor gives one of them by the battle RNG
    /// (combat vf 0xc → vf 0xc(0, n − 1)); with a null out pointer (the cast test 00862470) nothing is drawn. The exe
    /// anchor is the footprint's maximum corner on the bordered field: rewrite cell = (x − 2, y − 1) − (size − 1).
    /// </summary>
    internal Cell? ExeSummonPosition(int side, int size, Rng? rng)
    {
        var probe = new Unit { Id = -1, Size = size };
        for (int i = 0; i < S.Width; i++)
        {
            int ax = side == 0 ? i : S.Width - 1 - i;
            var col = new List<Cell>();
            for (int ay = 0; ay < S.Height; ay++)
            {
                var p = new Cell(ax - (size - 1), ay - (size - 1));
                if (p.X >= 0 && p.Y >= 0 && LandingFits(probe, p)) col.Add(p);
            }
            if (col.Count > 0) return rng == null ? col[0] : col[rng.Range(0, col.Count - 1)];
        }
        return null;
    }

    /// <summary>
    /// The summoned stack of FUN_00c56820 (FUN_00c557a0 cell test, ctor FUN_008abad0): its base ctor FUN_00a73e70 gives
    /// every unit the battle RNG's float vf 8(0, 0.25f) as its ATB (the turn object FUN_00a2e380), drawn after the
    /// position; the turn counter moves (FUN_00a2db60). The engine gave summons 0.05 · Random(6).
    /// </summary>
    Unit ExeSummonAt(int side, string creature, int count, string by, Cell pos)
    {
        var u = BattleSetup.AddCreature(Db, S, side, creature, count, pos, summoned: true);
        u.Atb = (float)S.Rng.RangeD(0f, 0.25f);
        u.HasMovedFromStart = true;
        Emit(new UnitSummoned(u.Id, creature, count, side, u.Pos, by));
        seqDirty = true;
        return u;
    }

    int AvatarOfDeathSize() =>
        Db.AvatarOfDeath?.Element("PrototypeCreature")?.Value.Trim() is { Length: > 0 } pc && Db.Creatures.TryGetValue(pc, out var d) ? d.CombatSize : 0;

    /// <summary>
    /// The cast test of SPELL_ABILITY_AVATAR_OF_DEATH (0097c320 → FUN_00862470): refused while the caster still has a
    /// unit linked for the spell (vf 0x30 = FUN_00a6eee0 over the hero's (spell, unit) pairs; the avatar's death drops the
    /// pair — 008a6e20 → the dead unit's vf 0x2b0 = FUN_00a730b0 → the partner's vf 0x29c), when vf 0x220 (FUN_00bc0030,
    /// the mana test the cast path makes anyway) fails, or when there is no summon position (FUN_00860f20 without out).
    /// </summary>
    internal string? AvatarOfDeathRefusal(HeroState h)
    {
        if (h.Links.ContainsKey(AvatarOfDeathSpell)) return "avatar of death already summoned";
        int size = AvatarOfDeathSize();
        if (size == 0 || ExeSummonPosition(h.Side, size, null) == null) return "no place for the avatar";
        return null;
    }

    /// <summary>
    /// The cast's own position draw (FUN_00862570 @ 0086262e), before the hero's cast vf 0x224 = FUN_00bbf610 spends the
    /// mana (Erratic Mana's rolls follow it).
    /// </summary>
    internal void AvatarOfDeathPrePick(HeroState h) => _avatarOfDeathPos = ExeSummonPosition(h.Side, AvatarOfDeathSize(), S.Rng);

    /// <summary>
    /// Avatar of Death (FUN_00984a00 case 0xc8 → FUN_00862570). FUN_00862320 builds the summon info (ctor FUN_00862970)
    /// with the DefaultStats Necromancer AvatarOfDeath_Creature record (RPGStats +0x880 → 008628f0), the cast power
    /// (vf 0x244), the caster level (vf 0x23c = FUN_00bbfb90: hero level), the knowledge term (<see cref="UniqueSummonKnowledge"/>),
    /// divisor 1 (the phoenix's is from its damage table) and +0x34 = the spell 0xc8. The position comes first
    /// (<see cref="AvatarOfDeathPrePick"/>; none → nothing), then the cast (vf 0x130 animation, vf 0x224), the creature
    /// FUN_00c56820 (count 1; the base ctor FUN_00a73e70 gives it the battle RNG's float in [0, 0.25) as its ATB, like
    /// every unit; the turn counter moves, FUN_00a2db60) and the link vf 0x298(0xc8, avatar). Its stats and abilities are
    /// the record's at level / 1 (<see cref="ApplyUniqueCreatureStats"/>, HasAbility FUN_008a34c0); it stays until killed.
    /// The engine had no cast (the spell was not offered).
    /// </summary>
    void AvatarOfDeath(int side, HeroState? hero, double power, int level)
    {
        var pos = _avatarOfDeathPos;
        _avatarOfDeathPos = null;
        var rec = Db.AvatarOfDeath;
        if (hero == null || pos is not Cell p || rec?.Element("PrototypeCreature")?.Value.Trim() is not { Length: > 0 } proto
            || !Db.Creatures.ContainsKey(proto)) return;
        var u = ExeSummonAt(side, proto, 1, AvatarOfDeathSpell, p);
        ApplyUniqueCreatureStats(u, rec, power, level, 1, UniqueSummonKnowledge(hero));
        u.AbilityUses[AvatarOfDeathMark] = 1;
        if (u.Alive) hero.Links[AvatarOfDeathSpell] = u.Id;
    }

    /// <summary>
    /// The death branch of the damage routine FUN_008a6e20 (008a748b..008a74f2): a unit whose summon info's spell
    /// (FUN_004bb080, +0x34) is 0xc8 makes its army's hero (vf 0xc → vf 0xc) set its mana to 0 (vf 0x22c(0) =
    /// FUN_00bbfac0, the field the casts spend) — "If this creature dies, the Hero will lose all his Mana points".
    /// </summary>
    void AvatarOfDeathDied(Unit t)
    {
        if (!t.AbilityUses.ContainsKey(AvatarOfDeathMark)) return;
        var h = S.Heroes[t.Side];
        if (!h.Present) return;
        h.Mana = 0;
        Emit(new ManaChanged(Actor.Hero(h.Side), 0));
    }
}
