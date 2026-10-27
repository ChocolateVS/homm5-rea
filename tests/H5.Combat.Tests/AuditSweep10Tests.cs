using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 10 of the combat audit: GAP rows closed against H5_Game.exe (docs/rules/audit/combat_sweep_10.md).</summary>
[Collection("game")]
public class AuditSweep10Tests(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Use(Battle b, Unit u, string ability, Unit? t = null, Cell? cell = null)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, TargetId = t?.Id, Cell = cell });
    }

    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB, ulong seed = 7)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot(mine, countA) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(theirs, countB) } },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var a = s.Units.First(u => u.Side == 0 && u.CreatureId == mine);
        var t = s.Units.First(u => u.Side == 1 && u.CreatureId == theirs);
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        return (new Battle(f.Db, s), a, t);
    }

    // exe FUN_009789b0 case 0xf1 @ 00978be8..00978bf2: FUN_00a34ca0 is called with ECX = 0x30 (SPELL_RESURRECT) and
    // EDX = 3 (Expert) — Resurrect's damage table at Expert with the hero's level as power (Base 240, PerPower 30), not
    // SPELL_EFFECT_GUARDIAN_ANGEL's own 1 + 1·level table (sweep 9 read the wrong record).
    [Fact]
    public void GuardianAngelUsesResurrectsExpertTable()
    {
        var (b, _, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var h = b.State.Heroes[0];
        h.Level = 12;
        var res = f.Db.Spell("SPELL_RESURRECT")!;
        Assert.Equal((int)Math.Round(res.DamageAt(Mastery.Expert).Eval(12)), b.GuardianAngelAmount(h));
        Assert.Equal(240 + 30 * 12, b.GuardianAngelAmount(h));
    }

    // exe FUN_00860660 → FUN_008605a0, FUN_00861d30 (c56750 with creature 0x1a at the chosen cell): the Balors appear at
    // the corpse — for a small corpse the 2-cell placement nearest to it (FUN_00b3e1e0: the corpse's own anchor when it
    // fits, else the smallest squared distance). The engine put them next to the army's edge of the field.
    [Fact]
    public void BalorsAppearAtTheCorpse()
    {
        var b = f.Duel("CREATURE_ARCHDEVIL", 3, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(14, 1));
        var imps = Add(b, 0, "CREATURE_IMP", 200, new Cell(9, 7));
        b.Begin();
        imps.Count = 0;
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.BalorSummoning, imps);
        Assert.True(r.Ok, r.Error);
        var balor = b.State.Units.Single(x => x.CreatureId == "CREATURE_BALOR");
        Assert.Equal(new Cell(9, 7), balor.Pos);
    }

    // exe FUN_00b3e1e0: when the wanted anchor is taken, the free anchors at the smallest squared distance, first in
    // row-major order (y outer) without an RNG.
    [Fact]
    public void ExePlacementTakesTheNearestFreeAnchorRowMajor()
    {
        var b = f.Duel("CREATURE_PEASANT", 3, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(14, 1));
        b.Begin();
        var probe = new Unit { Id = -1, Size = 1 };
        Assert.Equal(new Cell(5, 4), b.ExePlacement(probe, new Cell(5, 5), null));   // (5,4), (4,5), (6,5), (5,6) tie: row 4 first
    }

    // exe FUN_0085b6d0: round(MoatMaxDamage · town factor) with FPU rounding (half to even): 170 · 0.75 = 127.5 → 128
    // (the engine truncated to 127).
    [Fact]
    public void MoatDamageIsRoundedNotTruncated()
    {
        var attacker = new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30) } };
        var defender = new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30) } };
        var s = BattleSetup.Create(f.Db, attacker, defender, 3, skipTactics: true, siege: new SiegeSetup
        {
            Town = "TOWN_PRESERVE", FortLevel = 2, TownLevel = 3, TownMaxLevel = 4,
        });
        Assert.Equal(170, f.Db.Moats["TOWN_PRESERVE"].MaxDamage);
        Assert.Equal(128, s.Siege!.MoatDamage);
    }

    // exe FUN_00984a00 common tail 00984cb6 after case 0x34: the hero's vf 0x1cc (FUN_00412b40, constant 0 on the combat
    // hero vtable 00e8499c) is false, so its ATB is set to 0 (vf 0x184 → FUN_00a2d6a0(0)) — the engine's 0.
    [Fact]
    public void EncourageLeavesTheHeroAtZeroAtb()
    {
        var (b, u, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.DivineGuidance);
        b.Begin();
        b.State.Active = Actor.Hero(0);
        h.Atb = 1.1;
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "DIVINE_GUIDANCE") { Side = 0, TargetId = u.Id });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, h.Atb, 6);
    }

    // exe FUN_0089fd90 mode 1 (Manoeuvre, FUN_00a5d790(1) @ 00a62e1b): the nearest cell (walker cost) that touches no
    // enemy, with 2R added for the right army's cells at x ≤ its own — so a centaur struck from behind steps forward,
    // away from the attacker, instead of to the nearest free cell behind it (the engine's old choice).
    [Fact]
    public void ManoeuvrePicksTheNearestCellAwayFromEnemiesOnItsSide()
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_CENTAUR", 10, 1, new Cell(11, 5), new Cell(8, 5));
        b.State.Obstacles.Clear();
        b.Begin();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        var dest = b.ExeFleeDestination(t, a, 1);
        Assert.NotNull(dest);
        Assert.True(Battle.Distance(t, dest!.Value, a, a.Pos) > 1);
        var (dist, _) = b.MoveSearch(t, -1);
        int r2 = 2 * b.SpeedOf(t);
        int Score(Cell c) => dist[c] + (c.X <= t.Pos.X ? r2 : 0);
        var cands = dist.Keys.Where(c => c != t.Pos && dist[c] <= r2 && Battle.Distance(t, c, a, a.Pos) > 1).ToList();
        Assert.Equal(cands.Min(Score), Score(dest.Value));
        Assert.True(dest.Value.X > t.Pos.X);
    }

    // exe FUN_0089fd90 mode 0 (Cowardice, FUN_00a5d790(0) @ 00a62da2): score 2R − cost + 1 — the FARTHEST reachable cell
    // within 2R, cells next to the attacker rejected, next to other enemies +4R, the left army's cells at x ≥ its own +2R.
    [Fact]
    public void CowardiceRunsToTheFarthestCellBehindIt()
    {
        var b = f.Duel("CREATURE_GOBLIN", 30, "CREATURE_PEASANT", 10, 1, new Cell(6, 5), new Cell(7, 5));
        b.State.Obstacles.Clear();
        b.Begin();
        var (t, a) = (b.State.Units[0], b.State.Units[1]);
        var dest = b.ExeFleeDestination(t, a, 0);
        Assert.NotNull(dest);
        var (dist, _) = b.MoveSearch(t, -1);
        int r2 = 2 * b.SpeedOf(t);
        int Score(Cell c) => r2 - dist[c] + 1 + (c.X >= t.Pos.X ? r2 : 0);
        var cands = dist.Keys.Where(c => c != t.Pos && dist[c] <= r2 && Battle.Distance(t, c, a, a.Pos) > 1).ToList();
        Assert.Equal(cands.Min(Score), Score(dest!.Value));
        Assert.True(dest.Value.X < t.Pos.X);
        Assert.True(dist[dest.Value] >= r2 - 1);
    }

    // exe FUN_00978490 → FUN_00c536f0 (goals FUN_00b3d010 ring, FUN_00b5ee10 first smallest cost) → FUN_00a66050: Harm
    // Touch walks next to its target first. Ring of 1×1 anchors around (6,5): (7,5), (7,6), (6,6), (5,6), (5,5), (5,4),
    // (6,4), (7,4). The wraith is 2×2: its ring starts (7,4), (7,5), (7,6), (6,6), (5,6), (4,6), (4,5) …; from (2,5) the
    // cheapest is (4,5) (2 orthogonal steps). The engine required adjacency.
    [Fact]
    public void HarmTouchWalksToTheCheapestRingCellFirst()
    {
        Assert.Equal(new[] { new Cell(7, 5), new Cell(7, 6), new Cell(6, 6), new Cell(5, 6), new Cell(5, 5), new Cell(5, 4), new Cell(6, 4), new Cell(7, 4) },
            Battle.ExeRing(new Cell(6, 5), 1, 1));
        var b = f.Duel("CREATURE_WRAITH", 5, "CREATURE_FOOTMAN", 20, 1, new Cell(2, 5), new Cell(6, 5));
        b.State.Obstacles.Clear();
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        int before = t.TotalHealth;
        var r = Use(b, u, Ab.HarmTouch, t);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { new Cell(7, 4), new Cell(7, 5), new Cell(7, 6), new Cell(6, 6), new Cell(5, 6), new Cell(4, 6), new Cell(4, 5) },
            Battle.ExeRing(new Cell(6, 5), 2, 1).Take(7));
        Assert.Equal(new Cell(4, 5), u.Pos);
        Assert.Equal(before - t.MaxHealthCache, t.TotalHealth);
    }

    // exe FUN_00978490 for Scavenger: the wyvern walks to the corpse's ring before eating it (the engine ate any corpse
    // within its speed in place).
    [Fact]
    public void ScavengerWalksToTheCorpse()
    {
        var b = f.Duel("CREATURE_WYVERN_PAOKAI", 3, "CREATURE_PEASANT", 50, 1, new Cell(2, 2), new Cell(14, 9));
        b.State.Obstacles.Clear();
        var corpse = Add(b, 0, "CREATURE_PEASANT", 30, new Cell(7, 3));
        b.Begin();
        var u = b.State.Units[0];
        corpse.Count = 0;
        var goal = b.ExeWalkGoal(u, corpse);
        Assert.NotNull(goal);
        var r = Use(b, u, Ab.Scavenger, corpse);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(goal, u.Pos);
        Assert.True(Battle.AreAdjacent(u, corpse));
    }

    // exe FUN_00979930: Leap lands on the ring anchor around the target most in line with the jump — the smallest
    // angle between (cell − leaper) and (target − cell) — within move distance 2·Leap_JumpDistanceMax, and refuses a
    // target whose footprint gap is within 2·Leap_JumpDistanceMin (the engine took the nearest attack cell within speed).
    [Fact]
    public void LeapLandsInLineWithTheJump()
    {
        var b = f.Duel("CREATURE_HORNED_LEAPER", 1, "CREATURE_PEASANT", 30, 1, new Cell(1, 1), new Cell(8, 5));
        b.State.Obstacles.Clear();
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        var land = b.ExeLeapLanding(u, t);
        Assert.NotNull(land);
        double Ang(Cell c)
        {
            double a1 = Math.Atan2(c.Y - u.Pos.Y, c.X - u.Pos.X), a2 = Math.Atan2(t.Pos.Y - c.Y, t.Pos.X - c.X);
            double d = Math.Abs(a2 - a1);
            return Math.Min(d, 2 * Math.PI - d);
        }
        var cands = Battle.ExeRing(t.Pos, u.Size, t.Size).Where(c => b.State.InBounds(c) && b.Fits(u, c) && Battle.MoveDistance(u.Pos, c) <= 16).ToList();
        double bestA = cands.Min(Ang);
        Assert.True(Ang(land!.Value) <= bestA + 1e-3);
        Assert.Equal(new Cell(7, 4), land.Value);
        u.Pos = new Cell(5, 5);
        Assert.Null(b.ExeLeapLanding(u, t));   // gap 2 → move distance 4 ≤ 2·3
    }

    // exe FUN_0097ec10 (Rain of Arrows, dispatcher case 0x3d): the strikes, then the imbued spell per victim through
    // FUN_0097ea20 — for SPELL_HYPNOTIZE (0x14) on one victim drawn with the army RNG only. FUN_0097ea20 → hero vf 0x224
    // (FUN_00bbf610) casts nothing when the hero cannot pay and keeps the selection (the engine forgot the spell).
    [Fact]
    public void RainOfArrowsImbuesEveryVictimButHypnotizeOnlyOne()
    {
        int Casts(string spell, int mana)
        {
            var (b, a, t) = Make("CREATURE_PEASANT", 10, "CREATURE_IMP", 10, new Cell(1, 1), new Cell(14, 1));
            var t2 = Add(b, 1, "CREATURE_IMP", 10, new Cell(14, 5));
            var t3 = Add(b, 1, "CREATURE_IMP", 10, new Cell(14, 9));
            var h = b.State.Heroes[0];
            h.Perks.Add(Sk.RainOfArrows);
            h.FavoriteEnemies.Add("CREATURE_IMP");
            h.Spells.Add(spell);
            h.ImbuedSpell = spell;
            h.Mana = h.MaxMana = mana;
            b.Begin();
            b.State.Active = Actor.Hero(0);
            var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "RAIN_OF_ARROWS") { Side = 0 });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(spell, h.ImbuedSpell);
            return r.Events.OfType<SpellCast>().Count(e => e.Spell == spell);
        }
        Assert.Equal(3, Casts("SPELL_CURSE", 200));
        Assert.Equal(1, Casts("SPELL_HYPNOTIZE", 200));
        Assert.Equal(0, Casts("SPELL_CURSE", 0));   // cannot pay: nothing cast, the selection kept
    }

    // exe FUN_00419f00 / FUN_00419ad0: a rune costs its sSpellCost from the owner's resources (Rune of Charge: 1 Wood),
    // refused with COMBAT_NO_ENOUGH_MONEY when it cannot be paid; a Strong Rune re-activation on the same stack costs
    // round(cost · StrongRuneCostConstant) (FUN_009d0d10, ×3 in the stock data). The engine had no resources in combat.
    [Fact]
    public void RunesArePaidFromTheAdventureResources()
    {
        var (b, u, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var h = b.State.Heroes[0];
        h.Skills.Add(new SkillEntry(Sk.Runelore, Mastery.Expert));
        h.Perks.Add(Sk.StrongRune);
        h.Spells.Add("SPELL_RUNE_OF_CHARGE");
        h.Mana = h.MaxMana = 100;
        h.Resources = new[] { 0, 0, 0, 0, 0, 0, 1000 };
        b.Begin();
        b.State.Active = Actor.Hero(0);
        var r = b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_RUNE_OF_CHARGE", u.Id) { Side = 0 });
        Assert.False(r.Ok);
        Assert.Equal("COMBAT_NO_ENOUGH_MONEY", r.Error);
        h.Resources[0] = 4;
        r = b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_RUNE_OF_CHARGE", u.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, h.Resources[0]);
        Assert.Equal(1, h.ResourcesSpent[0]);
        Assert.Equal(new[] { 3, 0, 0, 0, 0, 0, 0 }, b.RuneCost(h, f.Db.Spell("SPELL_RUNE_OF_CHARGE")!, u.Id, fineRune: false));
        Assert.Equal(new[] { 2, 0, 0, 0, 0, 0, 0 }, b.RuneCost(h, f.Db.Spell("SPELL_RUNE_OF_CHARGE")!, u.Id, fineRune: true));
    }

    // exe FUN_004199d0: with Fine Rune a first activation is free when the battle RNG float [0, 1) is below
    // FineRuneProbability (0.5 stock) — over several battles both outcomes occur and nothing else is ever charged.
    [Fact]
    public void FineRuneMakesSomeActivationsFree()
    {
        var seen = new HashSet<int>();
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var (b, u, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10), seed);
            var h = b.State.Heroes[0];
            h.Skills.Add(new SkillEntry(Sk.Runelore, Mastery.Expert));
            h.Perks.Add(Sk.FineRune);
            h.Spells.Add("SPELL_RUNE_OF_CHARGE");
            h.Mana = h.MaxMana = 100;
            h.Resources = new[] { 5, 0, 0, 0, 0, 0, 0 };
            b.Begin();
            b.State.Active = Actor.Hero(0);
            var r = b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_RUNE_OF_CHARGE", u.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            seen.Add(h.ResourcesSpent[0]);
        }
        Assert.Equal(new HashSet<int> { 0, 1 }, seen);
    }
}
