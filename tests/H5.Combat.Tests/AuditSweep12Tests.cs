using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 12 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_12.md).</summary>
[Collection("game")]
public class AuditSweep12Tests(GameDataFixture f)
{
    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB, ulong seed = 7,
        Cell[]? mineExtra = null, Cell[]? theirsExtra = null)
    {
        mineExtra ??= Array.Empty<Cell>(); theirsExtra ??= Array.Empty<Cell>();
        var armyA = new List<ArmySlot> { new(mine, countA) }; armyA.AddRange(mineExtra.Select(_ => new ArmySlot("CREATURE_PEASANT", 5)));
        var armyB = new List<ArmySlot> { new(theirs, countB) }; armyB.AddRange(theirsExtra.Select(_ => new ArmySlot("CREATURE_PEASANT", 5)));
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = armyA },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = armyB },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var sideA = s.Units.Where(u => u.Side == 0).ToList(); var sideB = s.Units.Where(u => u.Side == 1).ToList();
        var a = sideA[0]; var t = sideB[0];
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        for (int i = 0; i < mineExtra.Length; i++) sideA[i + 1].Pos = sideA[i + 1].StartPos = mineExtra[i];
        for (int i = 0; i < theirsExtra.Length; i++) sideB[i + 1].Pos = sideB[i + 1].StartPos = theirsExtra[i];
        return (new Battle(f.Db, s), a, t);
    }

    static Unit Dummy(int side, Cell pos, int size = 1) => new() { Id = 1000 + pos.X * 20 + pos.Y, Side = side, Pos = pos, Size = size, Count = 1 };

    // exe FUN_00b3e720 (width 0, unbounded flag 1 from FUN_00a5c420 / 00a62075): D = 2(B − A) − sB + sA on the exe max
    // corners, P = 2(c − A) − (sA − 1); a cell is on the line when P·D ≥ 0 and (P × D)² ≤ |D|². A 1×1 mage at (1, 5)
    // shooting (5, 5): (9, 5) past the target is hit, (7, 6) beside the line and (0, 5) behind the mage are not.
    [Fact]
    public void MagicAttackLineRunsPastTheTargetAndIsHalfACellWide()
    {
        var a = Dummy(0, new Cell(1, 5)); var t = Dummy(1, new Cell(5, 5));
        Assert.True(Battle.OnMagicLine(a, t, t));
        Assert.True(Battle.OnMagicLine(a, t, Dummy(1, new Cell(9, 5))));
        Assert.True(Battle.OnMagicLine(a, t, Dummy(0, new Cell(3, 5))));
        Assert.False(Battle.OnMagicLine(a, t, Dummy(1, new Cell(7, 6))));
        Assert.False(Battle.OnMagicLine(a, t, Dummy(1, new Cell(0, 5))));
        // Diagonal (1, 1) → (4, 4): D = (6, 6); (2, 3) gives P = (2, 4), cross = 2·6 − 4·6 = −12, 144 ≤ 72? no.
        var t2 = Dummy(1, new Cell(4, 4));
        Assert.True(Battle.OnMagicLine(Dummy(0, new Cell(1, 1)), t2, Dummy(1, new Cell(6, 6))));
        Assert.False(Battle.OnMagicLine(Dummy(0, new Cell(1, 1)), t2, Dummy(1, new Cell(2, 3))));
    }

    // exe FUN_00b3ea30: the walk has `radius` cells, starts on a target cell of ring 1 around the attacker (ExeRing of
    // FUN_00b3d010), never goes beyond ring 3 (m < 2 for an outward step) and the victims are the target's army only
    // (FUN_00b3d850 with the target's army list).
    [Fact]
    public void RainbowBreathIsARandomWalkOverTheTargetsArmy()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (b, a, t) = Make("CREATURE_RAINBOW_DRAGON", 2, "CREATURE_PEASANT", 30, new Cell(3, 4), new Cell(5, 4), seed,
                new[] { new Cell(6, 4), new Cell(6, 3) }, new[] { new Cell(7, 4) });
            Assert.Equal("CREATURE_RAINBOW_DRAGON", a.CreatureId);
            int radius = 5;
            var walk = b.RainbowWalk(a, t, radius);
            Assert.Equal(radius, walk.Count);
            Assert.Contains(walk[0], t.Cells());
            var p = new Cell(a.Pos.X + a.Size - 1, a.Pos.Y + a.Size - 1);
            foreach (var c in walk)
            {
                int ring = Math.Max(Math.Max(c.X - p.X, p.X - a.Size + 1 - c.X), Math.Max(c.Y - p.Y, p.Y - a.Size + 1 - c.Y));
                Assert.InRange(ring, 1, 3);
            }
            var victims = b.RainbowVictims(a, t, radius);
            Assert.Equal(t, victims[0]);
            Assert.All(victims, v => Assert.Equal(1, v.Side));
        }
    }

    // The first cell is the only ring-1 cell of a 1×1 target; radius 1 → the target alone.
    [Fact]
    public void RainbowBreathRadiusOneIsTheTarget()
    {
        var (b, a, t) = Make("CREATURE_RAINBOW_DRAGON", 2, "CREATURE_PEASANT", 30, new Cell(3, 4), new Cell(5, 4));
        Assert.Equal(new[] { new Cell(5, 4) }, b.RainbowWalk(a, t, 1));
        Assert.Equal(new[] { t }, b.RainbowVictims(a, t, 1));
    }

    // exe 00987cb1 → FUN_00979410 / FUN_009848a0: Random Caster draws from a fixed list filtered by the application check
    // FUN_00982010 on the target — the dark six for 0xae; light six then dark six for 0xaf (only those valid on the target).
    [Fact]
    public void RandomCasterDrawsFromTheExeListValidOnTheTarget()
    {
        var genie = f.Db.Creatures.Values.First(c => c.Abilities.Contains(Ab.RandomCaster)).Id;
        var (b, a, t) = Make(genie, 5, "CREATURE_PEASANT", 30, new Cell(1, 1), new Cell(8, 5), mineExtra: new[] { new Cell(1, 4) });
        var pool = b.RandomCasterPool(a, Ab.RandomCaster, t).Select(s => s.Id).ToList();
        Assert.Equal(Battle.RandomCastDark, pool);
        var friend = b.State.Units.First(u => u.Side == 0 && u.Id != a.Id);
        Assert.Empty(b.RandomCasterPool(a, Ab.RandomCaster, friend));
        var light = b.RandomCasterPool(a, Ab.RandomCasterBless, friend).Select(s => s.Id).ToList();
        Assert.Contains("SPELL_BLESS", light);
        Assert.DoesNotContain("SPELL_CURSE", light);
        var onEnemy = b.RandomCasterPool(a, Ab.RandomCasterBless, t).Select(s => s.Id).ToList();
        Assert.DoesNotContain("SPELL_BLESS", onEnemy);
        Assert.Contains("SPELL_CURSE", onEnemy);
    }

    // exe FUN_00989270 (the use writer, from the payments FUN_008a11c0 / FUN_00bbf610), FUN_00989210(id, 1, 0) for Strong
    // Rune at the set-up and FUN_00989190 (uses < 1 → COMBAT_RUNIC_SPELL_ALREADY_USED): once per stack, twice with
    // Strong Rune — the engine let a Strong Rune hero reuse a rune without limit.
    [Fact]
    public void StrongRuneGivesOneMoreUsePerStack()
    {
        var (b, u, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var h = b.State.Heroes[0];
        var sp = f.Db.Spell("SPELL_RUNE_OF_CHARGE")!;
        Assert.False(Battle.RuneUsedUp(h, sp, u.Id));
        Battle.SpendRuneUse(h, sp, u.Id);
        Assert.True(Battle.RuneUsedUp(h, sp, u.Id));
        h.Perks.Add(Sk.StrongRune);
        Assert.False(Battle.RuneUsedUp(h, sp, u.Id));
        Battle.SpendRuneUse(h, sp, u.Id);
        Assert.True(Battle.RuneUsedUp(h, sp, u.Id));
    }

    // exe 008cf54c..008cf5bf / FUN_0097ea20 (0097ea95..0097eab5): Imbue Arrow queues the strike damage; the queue is flushed
    // before the imbued cast when FUN_00976b90 gives the spell no damage (Slow) and after it for a damage spell (Magic
    // Arrow) — the spell's damage lands first. The engine always dealt the arrow first.
    [Fact]
    public void ImbuedDamageSpellLandsBeforeTheArrow()
    {
        foreach (var (spell, spellFirst) in new[] { ("SPELL_MAGIC_ARROW", true), ("SPELL_SLOW", false) })
        {
            var (b, u, t) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 50, new Cell(1, 1), new Cell(14, 10));
            var h = b.State.Heroes[0];
            h.Perks.Add(Sk.ImbueArrow);
            h.Spells.Add(spell);
            h.Mana = h.MaxMana = 100;
            h.ImbuedSpell = spell;
            Assert.Equal(spellFirst, b.ImbueSpellFirst(h));
            b.Begin();
            b.State.Active = Actor.Hero(0);
            var r = b.Apply(new HeroAttackCmd(t.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            var ev = r.Events.ToList();
            int arrow = ev.FindIndex(e => e is Damaged d && d.Target == t.Id && d.Kind == AttackKind.Hero);
            int cast = ev.FindIndex(e => e is SpellCast c && c.Spell == spell);
            Assert.True(arrow >= 0 && cast >= 0, $"{spell}: arrow {arrow}, cast {cast}");
            Assert.Equal(spellFirst, cast < arrow);
        }
    }

    // exe FUN_0097f610 case 0x14a: a mechanical target qualifies when trunc(its total health − the saboteurs' total health ·
    // SabotageCoef) < 0 — the engine compared the two totals without the coefficient (10 stock, 1 in MMH55).
    [Fact]
    public void SabotageScalesTheSaboteursHealthBySabotageCoef()
    {
        var gremlin = f.Db.Creatures.Values.First(c => c.Abilities.Contains(Ab.Sabotage)).Id;
        var (b, a, t) = Make(gremlin, 10, "CREATURE_IRON_GOLEM", 1, new Cell(1, 1), new Cell(8, 5));
        Assert.True(t.Has(Ab.Mechanical));
        double k = (float)f.Db.Stats.Get("combat/Creatures/Academy/SabotageCoef", 10);
        int u = a.TotalHealth, hp = t.MaxHealthCache;
        for (int n = 1; n <= 200; n++)
        {
            t.Count = n; t.TopHealth = hp;
            Assert.Equal(t.TotalHealth - u * k <= -1, b.SabotageHealthAllows(a, t));
        }
        if (k > 1)
        {
            t.Count = (u + hp - 1) / hp + 1; t.TopHealth = hp;   // more total health than the saboteurs
            Assert.True(t.TotalHealth >= u);
            Assert.Equal(t.TotalHealth < u * k, b.SabotageHealthAllows(a, t));
        }
    }
}
