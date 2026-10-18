using H5.Core.Data;

namespace H5.Combat.Tests;

[Collection("game")]
public class SiegeTests(GameDataFixture f)
{
    static readonly string[] TownTypes =
    {
        "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD",
    };

    BattleState Siege(string town, int fort, int townLevel = 10, int maxLevel = 10, ulong seed = 3, bool stoneworks = false,
        bool towersSpec = false, HeroInstance? attackerHero = null)
    {
        var attacker = attackerHero != null ? ArmySetup.FromHero(attackerHero)
            : new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30), new ArmySlot("CREATURE_FOOTMAN", 30) } };
        var defender = new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30), new ArmySlot("CREATURE_FOOTMAN", 30) } };
        return BattleSetup.Create(f.Db, attacker, defender, seed, skipTactics: true, siege: new SiegeSetup
        {
            Town = town, FortLevel = fort, TownLevel = townLevel, TownMaxLevel = maxLevel, Stoneworks = stoneworks, TowersSpecialization = towersSpec,
        });
    }

    [Fact]
    public void EveryTownTypeHasACompleteSiegeLayout()
    {
        foreach (var town in TownTypes)
        {
            Assert.True(f.Db.TownCombat.TryGetValue(town, out var layout), town);
            var types = layout!.Buildings.Select(b => b.Type).ToList();
            Assert.Equal(4, types.Count(t => t == "WALL"));
            foreach (var t in new[] { "GATE", "MOAT", "BIG_TOWER", "LEFT_TOWER", "RIGHT_TOWER" }) Assert.Contains(t, types);
            Assert.NotNull(layout.ArenaDesc);
            Assert.All(layout.Buildings.Where(b => b.Type != "MOAT"), b => Assert.NotEmpty(b.BlockedTiles));
        }
    }

    [Fact]
    public void BuildingHealthMatchesTheManualTable()
    {
        // Fan manual p.308: Walls 200/200/300, Gate 200/300/400, Central Tower -/400/500, Side Towers -/-/400.
        var st = f.Db.ArenaBuildingStats;
        Assert.Equal(new[] { 200, 200, 300 }, Enumerable.Range(1, 3).Select(st["WALL"].HealthAt));
        Assert.Equal(new[] { 200, 300, 400 }, Enumerable.Range(1, 3).Select(st["GATE"].HealthAt));
        Assert.Equal(500, st["BIG_TOWER"].HealthAt(3));
        Assert.Equal(400, st["LEFT_TOWER"].HealthAt(3));
    }

    [Fact]
    public void MoatsComeFromTownData()
    {
        Assert.Equal(170 * 2, f.Db.Moats["TOWN_ACADEMY"].MaxDamage);           // land mines ×2 (p.309)
        Assert.Equal(204, f.Db.Moats["TOWN_HEAVEN"].MaxDamage);                 // ×1.2
        var dungeon = f.Db.Moats["TOWN_DUNGEON"].Spells.Single();
        Assert.Equal(("SPELL_ABILITY_POISONOUS_ATTACK", 28), (dungeon.Spell, dungeon.SpellPower));
        Assert.Equal(6, f.Db.Moats["TOWN_FORTRESS"].Mines.Count);               // six spell/rune pairs (p.323)
    }

    [Fact]
    public void CastleSiegeBuildsTheFortifications()
    {
        var s = Siege("TOWN_HEAVEN", 3);
        var sg = s.Siege!;
        Assert.Equal((14, 14), (s.Width, s.Height));
        Assert.Equal(8, sg.Buildings.Count);                                    // 4 walls, gate, 3 towers (moat is cells)
        Assert.Equal(16, sg.Moat.Count);
        Assert.Equal(300, sg.Buildings.First(b => b.Type == "WALL").MaxHealth);
        Assert.Equal(400, sg.Buildings.Single(b => b.Type == "GATE").MaxHealth);
        Assert.Equal(500, sg.Buildings.Single(b => b.Type == "BIG_TOWER").MaxHealth);
        Assert.Equal(3, s.Units.Count(u => u.CreatureId.StartsWith("SIEGE_")));
        // Gate cells are not static obstacles (defenders pass), walls and towers are.
        var gate = sg.Buildings.Single(b => b.Type == "GATE");
        Assert.DoesNotContain(gate.Blocked[0], s.Obstacles);
        Assert.Contains(sg.Buildings.First(b => b.Type == "WALL").Blocked[0], s.Obstacles);
        // Field cells (internal grid − margin): gate at x 7..8, y 5..8; central tower at x 13.
        Assert.All(gate.Blocked, c => Assert.InRange(c.X, 7, 8));
        Assert.All(sg.Buildings.Single(b => b.Type == "BIG_TOWER").Blocked, c => Assert.Equal(13, c.X));
    }

    [Fact]
    public void FortificationLevelsAddBuildingsAsInTheManual()
    {
        var fort = Siege("TOWN_HEAVEN", 1).Siege!;
        Assert.Equal(new[] { "GATE", "WALL" }, fort.Buildings.Select(b => b.Type).Distinct().Order());
        Assert.Empty(fort.Moat);
        var citadel = Siege("TOWN_HEAVEN", 2).Siege!;
        Assert.Contains(citadel.Buildings, b => b.Type == "BIG_TOWER");
        Assert.DoesNotContain(citadel.Buildings, b => b.IsSideTower);
        Assert.NotEmpty(citadel.Moat);
        var none = Siege("TOWN_HEAVEN", 0);
        Assert.Null(none.Siege);
        Assert.Equal((12, 10), (none.Width, none.Height));
    }

    [Fact]
    public void StoneworksAddsHalfTheHitPoints()
    {
        var s = Siege("TOWN_FORTRESS", 3, stoneworks: true);
        Assert.Equal(450, s.Siege!.Buildings.First(b => b.Type == "WALL").MaxHealth);
        Assert.Equal(600, s.Siege.Buildings.Single(b => b.Type == "GATE").MaxHealth);
    }

    [Fact]
    public void GateOpensForDefendersOnly()
    {
        var s = Siege("TOWN_HEAVEN", 3);
        var b = new Battle(f.Db, s);
        var gateCell = s.Siege!.Gate!.Blocked.First(c => c.Y == 6);
        var attacker = s.Units.First(u => u.Side == 0 && u.Kind == UnitKind.Creature && u.Size == 1);
        var defender = s.Units.First(u => u.Side == 1 && u.Kind == UnitKind.Creature && u.Size == 1);
        Assert.False(b.Fits(attacker, gateCell));
        Assert.True(b.Fits(defender, gateCell));
    }

    [Fact]
    public void InsideTownFollowsTheFortificationLine()
    {
        var b = new Battle(f.Db, Siege("TOWN_HEAVEN", 3));
        Assert.True(b.InsideTown(new Cell(12, 7)));
        Assert.True(b.InsideTown(new Cell(10, 1)));
        Assert.False(b.InsideTown(new Cell(0, 7)));
        Assert.False(b.InsideTown(new Cell(6, 3)));   // moat
    }

    [Fact]
    public void TowerDamageFollowsTheExe()
    {
        // exe 00bfdfb0: 70 central / 50 side, ×1.25 with the towers specialisation, × town level ratio.
        int Dmg(BattleState s, string type) => s.Units.Single(u => u.CreatureId == "SIEGE_" + type).BaseMinDamage;
        var full = Siege("TOWN_HEAVEN", 3);
        Assert.Equal((70, 50), (Dmg(full, "BIG_TOWER"), Dmg(full, "LEFT_TOWER")));
        var half = Siege("TOWN_HEAVEN", 3, townLevel: 18, maxLevel: 36);
        Assert.Equal((35, 25), (Dmg(half, "BIG_TOWER"), Dmg(half, "RIGHT_TOWER")));
        var spec = Siege("TOWN_HEAVEN", 3, towersSpec: true);
        Assert.Equal((87, 62), (Dmg(spec, "BIG_TOWER"), Dmg(spec, "LEFT_TOWER")));
    }

    [Fact]
    public void TowersCannotBeTargetedByCreatures()
    {
        var s = Siege("TOWN_HEAVEN", 3);
        var b = new Battle(f.Db, s);
        b.Begin();
        var archer = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ARCHER");
        s.Active = archer.Id;
        var tower = s.Units.First(u => u.CreatureId == "SIEGE_BIG_TOWER");
        Assert.False(b.Apply(new ShootCmd(archer.Id, tower.Id) { Side = 0 }).Ok);
        Assert.DoesNotContain(tower.Id, b.GetLegalActions()!.Shoot);
    }

    [Fact]
    public void CatapultAccuracyAndTargetsFollowTheExe()
    {
        var hero = f.Db.DuelPresets[1].Hero;   // a Knight preset
        var s = Siege("TOWN_HEAVEN", 3, attackerHero: hero);
        var b = new Battle(f.Db, s);
        var catapult = s.Units.Single(u => u.CreatureId == "WAR_MACHINE_CATAPULT");
        var h = s.Heroes[0];
        int expected = ((int)h.SkillLevel(Sk.WarMachines) + 2) * 10 + (h.Specialization == "HERO_SPEC_WAR_MACHINES" ? (h.Level + 2) / 3 * 2 : 0);
        Assert.Equal(expected, b.CatapultHitChance(catapult));
        // The central tower is not a target while a side tower stands.
        var targets = b.CatapultTargets();
        Assert.DoesNotContain(targets, t => t.Type == "BIG_TOWER");
        Assert.Contains(targets, t => t.Type == "GATE");
    }

    [Fact]
    public void AutomaticCatapultDamagesFortificationsDuringPlay()
    {
        var s = Siege("TOWN_HEAVEN", 3, attackerHero: f.Db.DuelPresets[0].Hero, seed: 11);
        s.AiControlled[0] = s.AiControlled[1] = true;
        var b = new Battle(f.Db, s);
        var events = new List<BattleEvent>(b.Begin());
        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        for (int i = 0; i < 400 && s.Phase != BattlePhase.Finished; i++)
        {
            int side = b.SideToAct ?? 0;
            var r = b.Apply(ai.Decide(b, side));
            if (r.Ok) events.AddRange(r.Events);
            else b.Apply(new WaitCmd(s.Active ?? Actor.Hero(side)) { Side = side });
        }
        Assert.Contains(events, e => e is BuildingDamaged { By: "CATAPULT" });
        Assert.Contains(events, e => e is AttackStarted { Ability: "TOWER" });
        Assert.All(events.OfType<BuildingDamaged>(), e => Assert.True(e.Damage > 0));
    }

    [Fact]
    public void EarthquakeDamagesEveryStructureInStepsOfFifty()
    {
        var s = Siege("TOWN_HEAVEN", 3, attackerHero: f.Db.DuelPresets[0].Hero);
        var h = s.Heroes[0];
        h.Spells.Add("SPELL_EARTHQUAKE");
        h.Mana = h.MaxMana = 100;
        var b = new Battle(f.Db, s);
        b.Begin();
        s.Active = Actor.Hero(0);
        var r = b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_EARTHQUAKE") { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var hits = r.Events.OfType<BuildingDamaged>().ToList();
        Assert.All(hits, e => Assert.Equal(0, e.Damage % 50));
        var m = h.SkillLevel(Sk.Summoning);
        int max = (int)f.Db.Spell("SPELL_EARTHQUAKE")!.DamageAt(m).Eval(0);
        Assert.All(hits, e => Assert.InRange(e.Damage, 0, max));
    }

    [Fact]
    public void WallsHalveShotsFromOutsideTheTown()
    {
        var s = Siege("TOWN_HEAVEN", 1);
        var b = new Battle(f.Db, s);
        var shooter = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ARCHER");
        var target = s.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_ARCHER");
        shooter.Pos = new Cell(1, 3);
        target.Pos = new Cell(12, 3);
        Assert.True(b.WallsHalveShot(shooter, target));
        foreach (var w in s.Siege!.Buildings) { w.Destroyed = true; }
        Assert.False(b.WallsHalveShot(shooter, target));
        Assert.False(b.WallsHalveShot(target, shooter));   // shooting out of the town is never penalised
    }

    [Fact]
    public void WalkersStopInTheMoatAndTakeItsDamage()
    {
        var s = Siege("TOWN_DUNGEON", 2);
        var b = new Battle(f.Db, s);
        b.Begin();
        var walker = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_FOOTMAN");
        walker.Pos = new Cell(4, 3);
        s.Active = walker.Id;
        var reach = b.Reachable(walker).Keys.Where(c => c.X >= 7).ToList();
        Assert.NotEmpty(reach);
        var dest = reach.OrderBy(c => c.X).First(c => c.X == 7 || c.X == 6);
        int hpBefore = walker.TotalHealth;
        var r = b.Apply(new MoveCmd(walker.Id, new Cell(7, 3)) { Side = 0 });
        if (!r.Ok) r = b.Apply(new MoveCmd(walker.Id, dest) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(walker.Pos, s.Siege!.Moat);
        Assert.Contains(r.Events, e => e is AbilityTriggered { Ability: "MOAT" });
        Assert.True(walker.TotalHealth < hpBefore);
        Assert.True(walker.HasEffect(H5.Combat.Fx.Poison));                    // Dungeon moat poisons (28 power)
    }

    [Fact]
    public void SiegeStateSurvivesSerialisation()
    {
        var s = Siege("TOWN_STRONGHOLD", 3);
        var copy = BattleState.Deserialize(s.Serialize());
        Assert.Equal(s.ComputeHash(), copy.ComputeHash());
        Assert.Equal(s.Siege!.Buildings.Count, copy.Siege!.Buildings.Count);
    }

    [Fact]
    public void LayHandsHasNoPerCombatLimit()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_PALADIN", 5), new ArmySlot("CREATURE_FOOTMAN", 20) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 10) } },
            5, obstacles: Array.Empty<Cell>(), skipTactics: true);
        var b = new Battle(f.Db, s);
        b.Begin();
        var paladin = s.Units[0]; var footman = s.Units[1];
        for (int i = 0; i < 2; i++)
        {
            footman.TopHealth = 1;
            s.Active = paladin.Id;
            var r = b.Apply(new UseAbilityCmd(paladin.Id, Ab.LayHands, footman.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(footman.MaxHealthCache, footman.TopHealth);
        }
    }

    [Fact]
    public void ConsumeArtifactHealsByLevelAndEffects()
    {
        var hero = f.Db.DuelPresets[0].Hero;
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = hero, Army = { new ArmySlot("CREATURE_FOOTMAN", 200) { ArtifactEffects = 2 } } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 10) } },
            5, obstacles: Array.Empty<Cell>(), skipTactics: true);
        var h = s.Heroes[0];
        h.Perks.Add("HERO_SKILL_MELT_ARTIFACT");
        var b = new Battle(f.Db, s);
        b.Begin();
        var footman = s.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
        Assert.Equal(2, footman.ArtifactEffects);
        footman.Count = 10; footman.TopHealth = footman.MaxHealthCache;
        int before = footman.TotalHealth;
        s.Active = Actor.Hero(0);
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "CONSUME_ARTIFACT", footman.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(before + 10 * h.Level * 3, footman.TotalHealth);   // 10 × level × (1 + 2 effects), resurrecting
        Assert.Equal(-1, footman.ArtifactEffects);
        Assert.Contains(r.Events, e => e is AtbChanged a && a.Actor == Actor.Hero(0) && Math.Abs(a.Atb - 0.75) < 1e-9);   // 0.25 turn spent
    }
}
