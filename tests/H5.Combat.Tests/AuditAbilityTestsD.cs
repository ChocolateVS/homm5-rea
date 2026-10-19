using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Creature abilities, audit part D: the exe's data-driven PatternAttack (FUN_00a62030 / FUN_008aa990 / FUN_00a5b630),
/// Acid Blood, Lightning Breath, the strike helpers (FUN_00a64120 / FUN_00a63e90), Treachery, Eldritch Aura
/// (see docs/rules/audit/combat_abilities_d.md).
/// </summary>
[Collection("game")]
public class AuditAbilityTestsD(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Attack(Battle b, Unit u, Unit t, Cell? from = null)
    {
        b.State.Active = u.Id;
        var r = b.Apply(new AttackCmd(u.Id, t.Id, from) { Side = u.Side });
        Assert.True(r.Ok, r.Error);
        return r;
    }

    static bool Hit(ApplyResult r, Unit u, string? by = null) =>
        r.Events.OfType<Damaged>().Any(d => d.Target == u.Id && (by == null || d.By == by));

    [Fact]
    public void WhirlwindTakesTheHalfCircleWithTheMostEnemies()   // HalfCircleLargeAttack.xdb, exe FUN_00a5b630 (exact mode)
    {
        // Kshatri 2x2 at (5,4) = exe cell (6,5). Target at (7,4) = (1,-1); enemies at (-2,1), (-1,1), (0,1): the 60°
        // rotation {(-2,1),(-1,1),(0,1),(1,1),(1,0),(1,-1)} holds the target and all three.
        var b = f.Duel("CREATURE_RAKSHASA_KSHATRI", 5, "CREATURE_PEASANT", 100, 1, new Cell(5, 4), new Cell(7, 4));
        var e1 = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(4, 6));
        var e2 = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(5, 6));
        var e3 = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(6, 6));
        var far = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(7, 2));   // (1,-3): in no rotation
        b.Begin();
        var r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.True(Hit(r, b.State.Units[1]));
        Assert.True(Hit(r, e1)); Assert.True(Hit(r, e2)); Assert.True(Hit(r, e3));
        Assert.False(Hit(r, far));
    }

    [Fact]
    public void SixHeadedHitsEveryAdjacentEnemyButNoAlly()   // SixHeadedAttack.xdb / Chaos_Hydra.xdb ring, DamageAll = false
    {
        var b = f.Duel("CREATURE_CHAOS_HYDRA", 5, "CREATURE_PEASANT", 100, 1, new Cell(5, 4), new Cell(7, 5));
        var corner = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(4, 3));
        var below = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(6, 6));
        var ally = Add(b, 0, "CREATURE_PEASANT", 50, new Cell(7, 3));
        b.Begin();
        var r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.True(Hit(r, corner)); Assert.True(Hit(r, below));
        Assert.False(Hit(r, ally));
    }

    [Fact]
    public void CrossAttackIsTheFlameMagesShotPatternOnly()   // CrossAttack.xdb, FUN_008aa990 (a shooter's pattern is ranged only)
    {
        var b = f.Duel("CREATURE_FLAME_MAGE", 10, "CREATURE_PEASANT", 100, 1, new Cell(1, 1), new Cell(8, 5));
        var right = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(9, 5));
        var diag = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(9, 6));
        b.Begin();
        var u = b.State.Units[0];
        b.State.Active = u.Id;
        var r = b.Apply(new ShootCmd(u.Id, b.State.Units[1].Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.True(Hit(r, right));
        Assert.False(Hit(r, diag));

        // A 2x2 target at (6,4) is aimed at its exe cell (7,5): the cross is (7,5) ± 1 — (8,5) is hit, (8,4) (next to
        // another of its cells) is not.
        b = f.Duel("CREATURE_FLAME_MAGE", 10, "CREATURE_CAVALIER", 10, 1, new Cell(1, 1), new Cell(6, 4));
        var side5 = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(8, 5));
        var side4 = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(8, 4));
        b.Begin();
        u = b.State.Units[0];
        b.State.Active = u.Id;
        r = b.Apply(new ShootCmd(u.Id, b.State.Units[1].Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.True(Hit(r, side5));
        Assert.False(Hit(r, side4));

        // In melee the shooter's pattern is not used.
        b = f.Duel("CREATURE_FLAME_MAGE", 10, "CREATURE_PEASANT", 100, 1, new Cell(7, 5), new Cell(8, 5));
        right = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(9, 5));
        b.Begin();
        r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.False(Hit(r, right));
    }

    [Fact]
    public void LargeTargetsBlockTheCellBehind()   // Deep_Dragon_Pattern.xdb (1,0),(2,0) from the 2x2 dragon's exe cell
    {
        // Black Dragon at (2,4) = exe cell (3,5); a 2x2 target at (4,4) covers (4,5) and (5,5) itself: nothing behind is hit.
        var b = f.Duel("CREATURE_BLACK_DRAGON", 3, "CREATURE_CAVALIER", 30, 1, new Cell(2, 4), new Cell(4, 4));
        var behind = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(6, 5));
        b.Begin();
        var r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.False(Hit(r, behind));
    }

    [Fact]
    public void AcidBloodSplashesTheLineThroughTheAttackerAndOnRetaliation()   // exe 00a683ff, AcidicHydraPattern (DamageAll)
    {
        // Acidic Hydra 2x2 at (5,4) = exe cell (6,5); the attacker stands at (7,5) = (1,0): the 22° line (1..4,0)
        // runs through (8,5), (9,5), (10,5) — the hydra's own stack at (9,5) is splashed too.
        var b = f.Duel("CREATURE_FOOTMAN", 20, "CREATURE_ACIDIC_HYDRA", 3, 1, new Cell(7, 5), new Cell(5, 4));
        var hydraAlly = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(9, 5));
        var off = Add(b, 0, "CREATURE_PEASANT", 50, new Cell(8, 3));
        b.Begin();
        var footman = b.State.Units[0]; var hydra = b.State.Units[1];
        var r = Attack(b, footman, hydra);
        Assert.True(Hit(r, footman, Ab.AcidBlood));
        Assert.True(Hit(r, hydraAlly, Ab.AcidBlood));
        Assert.False(Hit(r, off));

        // A retaliation strike against the hydra splashes acid as well (the hydra's own No Retaliation removed here).
        b = f.Duel("CREATURE_ACIDIC_HYDRA", 3, "CREATURE_FOOTMAN", 20, 1, new Cell(5, 4), new Cell(7, 5));
        b.State.Units[0].Abilities.Remove(Ab.NoEnemyRetaliation);
        b.Begin();
        r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.Contains(r.Events.OfType<AttackStarted>(), e => e.Kind == AttackKind.Retaliation);
        Assert.True(Hit(r, b.State.Units[1], Ab.AcidBlood));
    }

    [Fact]
    public void AcidBloodIsAQuarterOfTheHydrasDamageTruncated()   // exe 00a68508: (D + E) / 4, CDQ/SAR
    {
        var b = f.Duel("CREATURE_FOOTMAN", 20, "CREATURE_ACIDIC_HYDRA", 3, 1, new Cell(7, 5), new Cell(5, 4));
        b.Begin();
        var footman = b.State.Units[0]; var hydra = b.State.Units[1];
        hydra.BaseMinDamage = hydra.BaseMaxDamage = 13;
        hydra.RetaliationsLeft = 0;
        int expected = b.ComputeDamage(hydra, footman, new Battle.Hit { Kind = AttackKind.Melee }, false) / 4;
        var r = Attack(b, footman, hydra);
        var acid = r.Events.OfType<Damaged>().Single(d => d.Target == footman.Id && d.By == Ab.AcidBlood);
        Assert.Equal(expected, acid.Damage);
    }

    [Fact]
    public void LightningBreathSharesOneRollAmongAllItsTargets()   // exe 00a67b2c–00a67ba5: roll / number of targets
    {
        var b = f.Duel("CREATURE_WYVERN_PAOKAI", 10, "CREATURE_PEASANT", 200, 1, new Cell(5, 5), new Cell(6, 5));
        var other = Add(b, 1, "CREATURE_PEASANT", 200, new Cell(7, 5));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        u.BaseMinDamage = u.BaseMaxDamage = 20;   // roll = 10 × 20 = 200, shared by 2
        t.RetaliationsLeft = 0;
        int share = b.ComputeDamage(u, t, new Battle.Hit { Kind = AttackKind.Melee, TotalRoll = 100 }, false);
        var r = Attack(b, u, t);
        Assert.Equal(share, r.Events.OfType<Damaged>().First(d => d.Target == t.Id).Damage);
        Assert.True(Hit(r, other));
    }

    [Fact]
    public void LizardBiteJoinsAnyAllysStrikeWithoutLuckUnlessDisabled()   // FUN_00a64120 → FUN_00a63e90(0x3a), special 0xb5
    {
        var b = f.Duel("CREATURE_FOOTMAN", 20, "CREATURE_PEASANT", 200, 1, new Cell(5, 5), new Cell(6, 5));
        var raider = Add(b, 0, "CREATURE_BLACK_RIDER", 5, new Cell(6, 3));   // 2x2 at (6..7, 3..4): next to the target
        b.Begin();
        b.State.Units[1].RetaliationsLeft = 0;
        var r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.Contains(r.Events.OfType<AbilityTriggered>(), e => e.UnitId == raider.Id && e.Ability == Ab.LizardBite);

        b = f.Duel("CREATURE_FOOTMAN", 20, "CREATURE_PEASANT", 200, 1, new Cell(5, 5), new Cell(6, 5));
        raider = Add(b, 0, "CREATURE_BLACK_RIDER", 5, new Cell(6, 3));
        b.Begin();
        b.AddEffect(raider, new Effect { Id = "SPELL_BLIND", Remaining = 3, Mastery = Mastery.Basic });
        r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.DoesNotContain(r.Events.OfType<AbilityTriggered>(), e => e.Ability == Ab.LizardBite);   // FUN_004e4a20
    }

    [Fact]
    public void PackHunterAlsoFollowsARetaliation()   // FUN_00a646b0 → FUN_00a64120 (retaliation: Pack Hunter, no Lizard Bite)
    {
        var b = f.Duel("CREATURE_PEASANT", 100, "CREATURE_WOLF", 40, 1, new Cell(5, 5), new Cell(6, 5));
        var wolf2 = Add(b, 1, "CREATURE_WOLF", 20, new Cell(5, 4));   // next to the attacker
        b.Begin();
        var r = Attack(b, b.State.Units[0], b.State.Units[1]);
        Assert.Contains(r.Events.OfType<AttackStarted>(), e => e.Kind == AttackKind.Retaliation);
        Assert.Contains(r.Events.OfType<AbilityTriggered>(), e => e.UnitId == wolf2.Id && e.Ability == Ab.PackHunter);
    }

    [StockFact]
    public void GoblinsDefectAtExactlyThirtyPercent()   // FUN_008a0b60: count/initial < (float)0.3
    {
        var b = f.Duel("CREATURE_PEASANT", 1, "CREATURE_GOBLIN", 10, 1, new Cell(1, 1), new Cell(10, 8));
        b.Begin();
        var g = b.State.Units[1];
        int side = g.Side;
        b.DealDamage(g, 7 * g.MaxHealthCache, -1, AttackKind.Spell, "TEST");
        Assert.Equal(3, g.Count);
        Assert.NotEqual(side, g.Side);
    }

    [Fact]
    public void EldritchAuraBlocksMagiShotsAtStacksBesideAnyCombatMage()   // FUN_008a4b70 flag 0x1000, FUN_00b3b330
    {
        var b = f.Duel("CREATURE_MAGI", 10, "CREATURE_PEASANT", 50, 1, new Cell(1, 1), new Cell(10, 5));
        var enemyMage = Add(b, 1, "CREATURE_COMBAT_MAGE", 5, new Cell(10, 6));   // an ENEMY combat mage protects the peasants
        var lone = Add(b, 1, "CREATURE_PEASANT", 50, new Cell(10, 1));
        b.Begin();
        var u = b.State.Units[0];
        b.State.Active = u.Id;
        Assert.False(b.Apply(new ShootCmd(u.Id, b.State.Units[1].Id) { Side = 0 }).Ok);
        Assert.DoesNotContain(b.State.Units[1].Id, b.GetLegalActions()!.Shoot);
        Assert.Contains(enemyMage.Id, b.GetLegalActions()!.Shoot);   // the aura is not on the mage itself
        Assert.Contains(lone.Id, b.GetLegalActions()!.Shoot);
    }

    [Fact]
    public void BlindingAttackUsesTheDerivedBlindImmunity()   // FUN_00a629b0 → FUN_00a5e440 → FUN_00982010 (HasAbilityExe)
    {
        bool Blinded(string target, bool vampirism)
        {
            for (ulong seed = 1; seed <= 40; seed++)
            {
                var b = f.Duel("CREATURE_WAR_UNICORN", 10, target, 300, seed, new Cell(5, 5), new Cell(6, 5));
                b.Begin();
                var t = b.State.Units[1];
                if (vampirism) b.AddEffect(t, new Effect { Id = "SPELL_VAMPIRISM", Remaining = 5, Positive = true });
                Attack(b, b.State.Units[0], t);
                if (t.HasEffect("SPELL_BLIND")) return true;
            }
            return false;
        }
        Assert.False(Blinded("CREATURE_SKELETON", false));   // undead: derived Immunity to Blind
        Assert.True(Blinded("CREATURE_PEASANT", true));      // Vampirism's dynamic undead does not feed the derived flags
    }
    [StockFact]
    public void MagnetismMovesAShareOfTheOtherVictimsDamageOntoTheGolems()   // FUN_0085e330 (area / chain resolvers)
    {
        var b = f.Duel("CREATURE_OBSIDIAN_GOLEM", 30, "CREATURE_PEASANT", 100, 1, new Cell(1, 1), new Cell(10, 8));
        b.Begin();
        var golem = b.State.Units[0]; var peasant = b.State.Units[1];
        golem.WeeklyGrowth = 6;   // share = 30 / 6 * 5 = 25 %
        var m = typeof(Battle).GetMethod("MagnetismRedistribute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var hits = new List<(Unit V, double D)> { (peasant, 100), (golem, 40) };
        var r = (List<double>)m.Invoke(b, new object[] { hits })!;
        Assert.Equal((int)((double)(float)100 - (double)(float)(100 * 25) * (double)0.01f), (int)r[0]);   // 74: single precision
        Assert.Equal(40 + (int)((double)(float)(25 * 100) * (double)0.01f), (int)r[1]);                  // 65: no healing
    }

    [Fact]
    public void GoblinThrowerCountsTheGoblinFamily()   // FUN_008a4b70: FUN_004babf0 (family = BaseCreature or id) == 0x75
    {
        bool Offered(string helper)
        {
            var b = f.Duel("CREATURE_CYCLOP_UNTAMED", 5, "CREATURE_PEASANT", 50, 1, new Cell(5, 4), new Cell(12, 8));
            Add(b, 0, helper, 30, new Cell(4, 4));
            b.Begin();
            return b.UnitAbilities(b.State.Units[0]).Any(a => a.Id == Ab.GoblinThrower);
        }
        Assert.True(Offered("CREATURE_GOBLIN"));
        Assert.True(Offered("CREATURE_GOBLIN_TRAPPER"));   // BaseCreature CREATURE_GOBLIN
        Assert.False(Offered("CREATURE_ORC_WARRIOR"));
    }

    [Fact]
    public void ChainShotSharesOneRollHalvingDownTheChainAndSkipsInfernoCreatures()   // FUN_00a64ad0, FUN_00a5dbb0
    {
        // Main target an Imp (Inferno is allowed as the main target), then the nearest non-Inferno stacks.
        var b = f.Duel("CREATURE_INFERNAL_SUCCUBUS", 10, "CREATURE_IMP", 100, 1, new Cell(2, 5), new Cell(6, 5));
        var p1 = Add(b, 1, "CREATURE_PEASANT", 200, new Cell(6, 7));
        var imp2 = Add(b, 1, "CREATURE_IMP", 100, new Cell(7, 5));      // nearest to the main target, but Inferno
        var p2 = Add(b, 1, "CREATURE_PEASANT", 200, new Cell(6, 9));
        b.Begin();
        var u = b.State.Units[0]; var main = b.State.Units[1];
        u.BaseMinDamage = u.BaseMaxDamage = 10;   // roll = 100
        main.RetaliationsLeft = 0;
        int d0 = b.ComputeDamage(u, main, new Battle.Hit { Kind = AttackKind.Ranged, TotalRoll = 100 }, true);
        int d1 = b.ComputeDamage(u, p1, new Battle.Hit { Kind = AttackKind.Ranged, TotalRoll = 50 }, true);
        int d2 = b.ComputeDamage(u, p2, new Battle.Hit { Kind = AttackKind.Ranged, TotalRoll = 25 }, true);
        b.State.Active = u.Id;
        var r = b.Apply(new ShootCmd(u.Id, main.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        int Dmg(Unit x) => r.Events.OfType<Damaged>().Where(e => e.Target == x.Id).Sum(e => e.Damage);
        Assert.Equal(d0, Dmg(main));
        Assert.Equal(d1, Dmg(p1));
        Assert.Equal(d2, Dmg(p2));
        Assert.Equal(0, Dmg(imp2));
    }

    [Fact]
    public void BondOfLightSkipsAntiMagicAndTeleportAndKeepsAHigherMastery()   // FUN_00983790 (00983ee3–00984058)
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, 1, new Cell(1, 1), new Cell(10, 8));
        var unicorn = Add(b, 0, "CREATURE_WHITE_UNICORN", 5, new Cell(1, 5));
        b.Begin();
        var t = b.State.Units[0];
        b.AddEffect(t, new Effect { Id = "SPELL_ANTI_MAGIC", Positive = true, Remaining = 3, School = "MAGIC_SCHOOL_LIGHT", SpellLevel = 3, Mastery = Mastery.Expert });
        Assert.False(unicorn.HasEffect("SPELL_ANTI_MAGIC"));
        b.AddEffect(t, new Effect { Id = "SPELL_BLESS", Positive = true, Remaining = 3, School = "MAGIC_SCHOOL_LIGHT", SpellLevel = 1, Mastery = Mastery.Ultimate });
        Assert.Equal(Mastery.Ultimate, unicorn.GetEffect("SPELL_BLESS")?.Mastery);
    }

    [Fact]
    public void DeathCloudIsTheThreeByThreeAroundTheAimedCell()   // FUN_00a5fde0 (00a5fe40), 0097a530 case 0xa9
    {
        // 2x2 target at (6,4) is aimed at its exe cell (7,5): the cloud covers x 6..8, y 4..6.
        var b = f.Duel("CREATURE_LICH", 10, "CREATURE_CAVALIER", 10, 1, new Cell(1, 1), new Cell(6, 4));
        var inside = Add(b, 1, "CREATURE_PEASANT", 200, new Cell(8, 6));
        var outside = Add(b, 1, "CREATURE_PEASANT", 200, new Cell(5, 6));   // next to (6,5) but 2 from (7,5)
        b.Begin();
        var u = b.State.Units[0];
        b.State.Active = u.Id;
        var r = b.Apply(new ShootCmd(u.Id, b.State.Units[1].Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.True(Hit(r, inside));
        Assert.False(Hit(r, outside));
    }
}
