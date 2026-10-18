using H5.Core.Data;

namespace H5.Combat;

// Town sieges. Layouts, hit points and moats come from game data (H5.Core.Data.Siege); the rules below
// follow H5_Game.exe where it was located (addresses in docs/rules/EXE_FINDINGS.md, "Sieges"):
//   * field: 14x14 when the town has a Fort (internal grid 18x16 = field + 2-col / 1-row margins);
//   * buildings by fortification: Fort = walls + gate, Citadel = + moat + central tower, Castle = + side towers;
//   * catapult accuracy and miss retargeting (00b3b7d0 / 00b3b560), tower damage (00bfdfb0),
//     wall shooting penalty (00a5c5a0 → 00b3de90 / 00b3e920), Earthquake and Tremors (0097d7b0).

/// <summary>Town information for a siege battle, supplied by the adventure layer.</summary>
public sealed class SiegeSetup
{
    /// <summary>Town type (TOWN_HEAVEN, …) selecting the fortification layout and moat.</summary>
    public string Town = Towns.Haven;
    /// <summary>0 = no fortifications (plain 12x10 field), 1 = Fort, 2 = Citadel, 3 = Castle.</summary>
    public int FortLevel;
    /// <summary>Town level (buildings built) and its maximum, scaling tower and moat damage.</summary>
    public int TownLevel = 1, TownMaxLevel = 1;
    /// <summary>Fortress Stoneworks: walls, gate and towers get +50% hit points (fan manual p.308).</summary>
    public bool Stoneworks;
    /// <summary>Town defence specialisation (SPELL_TOWN_WALLS): +50 hit points per structure (p.308).</summary>
    public bool WallsSpecialization;
    /// <summary>Town towers specialisation (SPELL_TOWN_TOWERS): tower damage ×1.25 (exe 00bfdfb0).</summary>
    public bool TowersSpecialization;
    /// <summary>Fortress Guard Post: number of Defenders added to the garrison (5 × town level × month, p.308).</summary>
    public int GuardPostDefenders;
}

/// <summary>One fortification piece on the battlefield.</summary>
public sealed class SiegeBuilding
{
    public int Id;
    /// <summary>WALL, GATE, LEFT_TOWER, BIG_TOWER, RIGHT_TOWER or MOAT.</summary>
    public string Type = "";
    /// <summary>Index into <see cref="TownCombatDef.Buildings"/> (models, effects, sounds for the client).</summary>
    public int DefIndex;
    /// <summary>Model anchor in field coordinates (internal siege grid minus the margin; may lie outside the field).</summary>
    public Cell Anchor;
    public List<Cell> Blocked = new();
    public List<Cell> BlockedDestroyed = new();
    public int Health, MaxHealth;
    public bool Destroyed;
    /// <summary>Unit acting for a tower (shoots each turn), or -1.</summary>
    public int TowerUnit = -1;

    public bool IsTower => Type is "LEFT_TOWER" or "BIG_TOWER" or "RIGHT_TOWER";
    public bool IsSideTower => Type is "LEFT_TOWER" or "RIGHT_TOWER";
    /// <summary>Solid structures that block passage and shots (the moat is neither).</summary>
    public bool IsSolid => Type is "WALL" or "GATE" or "LEFT_TOWER" or "BIG_TOWER" or "RIGHT_TOWER";
    public IEnumerable<Cell> CurrentlyBlocked => Destroyed ? BlockedDestroyed : Blocked;
}

/// <summary>Siege-specific battle state (null for field battles).</summary>
public sealed class SiegeState
{
    public string Town = "";
    public int FortLevel;
    public double TownLevelRatio = 1;
    /// <summary>Offset from the engine's internal siege grid to field cells (field = internal − offset).</summary>
    public int OffsetX = 2, OffsetY = 1;
    public string? ArenaDesc;
    public string? Scenery;
    public List<SiegeBuilding> Buildings = new();
    public List<Cell> Moat = new();
    public int MoatDamage;
    /// <summary>Academy moat: cells whose land mine has already exploded.</summary>
    public List<Cell> SpentMines = new();
    /// <summary>Per row, the easternmost fortification column; cells east of it are inside the town.</summary>
    public int[] FortLine = Array.Empty<int>();
    public int NextBuildingId;

    public SiegeBuilding? Building(int id) => Buildings.Find(b => b.Id == id);
    public SiegeBuilding? BuildingAt(Cell c) => Buildings.Find(b => b.IsSolid && b.CurrentlyBlocked.Contains(c));
    public SiegeBuilding? Gate => Buildings.Find(b => b.Type == "GATE");
}

public static class SiegeBuilder
{
    /// <summary>
    /// Turns a battle state created on a field into a siege: resizes the field, places the fortifications
    /// of the town's layout for its fortification level, adds tower units and the attacker's catapult.
    /// Must run before the armies are placed.
    /// </summary>
    public static void Prepare(GameDatabase db, BattleState s, SiegeSetup setup)
    {
        if (setup.FortLevel <= 0 || !db.TownCombat.TryGetValue(setup.Town, out var layout)) return;
        var sg = new SiegeState
        {
            Town = setup.Town,
            FortLevel = Math.Clamp(setup.FortLevel, 1, 3),
            TownLevelRatio = setup.TownMaxLevel > 0 ? Math.Clamp((double)setup.TownLevel / setup.TownMaxLevel, 0, 1) : 1,
            ArenaDesc = layout.ArenaDesc,
            Scenery = layout.Scenery,
        };
        s.Width = 14;
        s.Height = 14;
        s.Siege = sg;
        s.Arena = null;
        s.ObstaclePlacements.Clear();
        s.Obstacles.Clear();

        Cell F((int X, int Y) t) => new(t.X - sg.OffsetX, t.Y - sg.OffsetY);
        for (int i = 0; i < layout.Buildings.Count; i++)
        {
            var def = layout.Buildings[i];
            if (!Present(def.Type, sg.FortLevel)) continue;
            if (def.Type == "MOAT")
            {
                sg.Moat = def.BlockedTiles.Select(F).Where(s.InBounds).Distinct().ToList();
                if (db.Moats.TryGetValue(setup.Town, out var moat))
                    sg.MoatDamage = (int)(moat.MaxDamage * sg.TownLevelRatio);
                continue;
            }
            int hp = db.ArenaBuildingStats.TryGetValue(def.Type, out var st) ? st.HealthAt(sg.FortLevel) : 200;
            if (setup.Stoneworks) hp = (int)(hp * 1.5);
            if (setup.WallsSpecialization) hp += 50;
            var b = new SiegeBuilding
            {
                Id = sg.NextBuildingId++, Type = def.Type, DefIndex = i,
                Anchor = new Cell(def.PosX - sg.OffsetX, def.PosY - sg.OffsetY),
                Blocked = def.BlockedTiles.Select(F).Where(s.InBounds).Distinct().ToList(),
                BlockedDestroyed = def.BlockedTilesForDestroyed.Select(F).Where(s.InBounds).Distinct().ToList(),
                Health = hp, MaxHealth = hp,
            };
            sg.Buildings.Add(b);
        }

        // Fortification line used by "inside the town" (walls, gate and side towers; not the central tower or moat).
        sg.FortLine = new int[s.Height];
        for (int y = 0; y < s.Height; y++)
        {
            var xs = sg.Buildings.Where(b => b.Type is "WALL" or "GATE" or "LEFT_TOWER" or "RIGHT_TOWER")
                .SelectMany(b => b.Blocked).Where(c => c.Y == y).Select(c => c.X).ToList();
            sg.FortLine[y] = xs.Count > 0 ? xs.Max() : -1;
        }
        for (int y = 0; y < s.Height; y++)
            if (sg.FortLine[y] < 0)
            {
                // Rows without a fortification tile take the nearest row's line.
                for (int d = 1; d < s.Height; d++)
                {
                    if (y - d >= 0 && sg.FortLine[y - d] >= 0) { sg.FortLine[y] = sg.FortLine[y - d]; break; }
                    if (y + d < s.Height && sg.FortLine[y + d] >= 0) { sg.FortLine[y] = sg.FortLine[y + d]; break; }
                }
            }

        RebuildObstacles(s);

        // Towers shoot as autonomous defender actors (Initiative 10, like war machines), placed off the field.
        foreach (var b in sg.Buildings.Where(b => b.IsTower))
        {
            int baseDamage = b.Type == "BIG_TOWER" ? 70 : 50;
            if (setup.TowersSpecialization) baseDamage = (int)(baseDamage * 1.25);
            int damage = (int)(sg.TownLevelRatio * baseDamage);
            var u = new Unit
            {
                Id = s.NextId++, Side = 1, Kind = UnitKind.Obstacle, CreatureId = "SIEGE_" + b.Type,
                Name = b.Type switch { "BIG_TOWER" => "Central Tower", "LEFT_TOWER" => "Left Tower", _ => "Right Tower" },
                Town = setup.Town, Tier = 1, Level = 1, BaseHealth = b.MaxHealth, BaseInitiative = 10,
                BaseMinDamage = damage, BaseMaxDamage = damage, Size = 1, Count = 1, InitialCount = 1,
                Abilities = new List<string> { Ab.Mechanical },
                Pos = new Cell(-100 - b.Id, -100), StartPos = new Cell(-100 - b.Id, -100),
            };
            u.MaxHealthCache = u.TopHealth = b.MaxHealth;
            u.RetaliationsLeft = 0;
            s.Units.Add(u);
            b.TowerUnit = u.Id;
        }
    }

    /// <summary>Which building types exist at a fortification level (fan manual p.308 table).</summary>
    public static bool Present(string type, int fortLevel) => type switch
    {
        "WALL" or "GATE" => fortLevel >= 1,
        "MOAT" or "BIG_TOWER" => fortLevel >= 2,
        "LEFT_TOWER" or "RIGHT_TOWER" => fortLevel >= 3,
        _ => false,
    };

    /// <summary>Static obstacles = solid buildings except the gate, whose passability depends on the mover.</summary>
    internal static void RebuildObstacles(BattleState s)
    {
        if (s.Siege == null) return;
        s.Obstacles = s.Siege.Buildings
            .Where(b => b.IsSolid && (b.Type != "GATE" || b.Destroyed))
            .SelectMany(b => b.CurrentlyBlocked).Distinct().ToList();
    }

    /// <summary>Places the Fortress Guard Post garrison (DefaultStats Fortress_GuardPostStats).</summary>
    public static void AddGuardPost(GameDatabase db, BattleState s, SiegeSetup setup, Func<Unit, Cell, Cell?> nearestFree)
    {
        if (setup.GuardPostDefenders <= 0 || setup.Town != Towns.Fortress) return;
        string creature = db.Stats.Str("adventure/TownBuildingBonuses/Fortress_GuardPostStats/BaseCreatureToPlace", "CREATURE_DEFENDER");
        if (!db.Creatures.ContainsKey(creature)) return;
        string node = s.Siege != null ? "CellToPlaceWithFort" : "CellToPlaceNoFort";
        int ix = (int)db.Stats.Get($"adventure/TownBuildingBonuses/Fortress_GuardPostStats/{node}/x", s.Siege != null ? 12 : 8);
        int iy = (int)db.Stats.Get($"adventure/TownBuildingBonuses/Fortress_GuardPostStats/{node}/y", s.Siege != null ? 8 : 6);
        var want = new Cell(ix - 2, iy - 1);   // internal grid → field cell (2-column / 1-row margin)
        var probe = new Unit { Size = db.Creature(creature).CombatSize };
        var at = nearestFree(probe, want);
        if (at != null) BattleSetup.AddCreature(db, s, 1, creature, setup.GuardPostDefenders, at.Value, summoned: false);
    }
}

public sealed partial class Battle
{
    public SiegeState? Siege => S.Siege;

    static bool IsSiegeTower(Unit u) => u.Kind == UnitKind.Obstacle && u.CreatureId.StartsWith("SIEGE_", StringComparison.Ordinal);

    /// <summary>The intact gate blocks the attackers; the defenders pass through it (it opens for them).</summary>
    bool GateBlocks(Cell c, Unit mover) =>
        S.Siege?.Gate is { Destroyed: false } g && g.Blocked.Contains(c) && mover.Side == 0;

    /// <summary>
    /// Inside the town: on the defenders' side of the fortification line (exe 00b3de90, which ignores the
    /// central tower and moat), within the field's inner rows.
    /// </summary>
    public bool InsideTown(Cell c) =>
        S.Siege is { } sg && c.Y >= 0 && c.Y < sg.FortLine.Length && c.X > sg.FortLine[c.Y];

    public bool InsideTown(Unit u) => u.Cells().Any(InsideTown);

    /// <summary>Whether a shot from <paramref name="a"/> at <paramref name="t"/> is halved by the town walls.</summary>
    public bool WallsHalveShot(Unit a, Unit t) => ShotCrossesWalls(a, t);

    /// <summary>
    /// Shooting across intact town walls halves damage (exe 00a5c5a0): the shooter is outside, the target inside,
    /// and the line between them crosses an intact fortification (00b3e920 / 00b3e720).
    /// </summary>
    bool ShotCrossesWalls(Unit a, Unit t)
    {
        if (S.Siege is not { } sg || InsideTown(a) || !InsideTown(t)) return false;
        var solid = new HashSet<Cell>(sg.Buildings.Where(b => b.IsSolid && !b.Destroyed).SelectMany(b => b.Blocked));
        if (solid.Count == 0) return false;
        var (ax, ay) = a.Center; var (tx, ty) = t.Center;
        foreach (var c in Supercover(ax, ay, tx, ty))
            if (solid.Contains(c) && !a.Occupies(c) && !t.Occupies(c)) return true;
        return false;
    }

    /// <summary>Cells touched by the segment between two cell-centre points.</summary>
    static IEnumerable<Cell> Supercover(double x0, double y0, double x1, double y1)
    {
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) * 4) + 1;
        var seen = new HashSet<Cell>();
        for (int i = 0; i <= steps; i++)
        {
            double f = (double)i / steps;
            var c = new Cell((int)Math.Floor(x0 + (x1 - x0) * f + 0.5), (int)Math.Floor(y0 + (y1 - y0) * f + 0.5));
            if (seen.Add(c)) yield return c;
        }
    }

    // ------------------------------------------------------------------ buildings

    void DamageBuilding(SiegeBuilding b, int damage, string by)
    {
        if (b.Destroyed || damage <= 0) return;
        b.Health = Math.Max(0, b.Health - damage);
        bool destroyed = b.Health <= 0;
        if (destroyed)
        {
            b.Destroyed = true;
            SiegeBuilder.RebuildObstacles(S);
            if (S.Unit(b.TowerUnit) is { Alive: true } tower)
            {
                tower.Count = 0;
                tower.TopHealth = 0;
                Emit(new UnitDied(tower.Id));
            }
        }
        Emit(new BuildingDamaged(b.Id, damage, b.Health, destroyed, by));
    }

    /// <summary>Buildings the catapult may aim at (exe 00b3b560 with includeDestroyed = false).</summary>
    public List<SiegeBuilding> CatapultTargets() => CatapultCandidates(includeDestroyed: false, exclude: null);

    /// <summary>
    /// Targetable buildings: walls, gate (while closed) and side towers; the central tower only once no side
    /// tower is standing. The moat is never a target.
    /// </summary>
    List<SiegeBuilding> CatapultCandidates(bool includeDestroyed, SiegeBuilding? exclude)
    {
        var list = new List<SiegeBuilding>();
        if (S.Siege is not { } sg) return list;
        bool sideTowerStands = sg.Buildings.Any(b => b.IsSideTower && !b.Destroyed);
        foreach (var b in sg.Buildings)
        {
            if (!b.IsSolid || b == exclude) continue;
            if (b.Destroyed && !includeDestroyed) continue;
            if (b.Type == "BIG_TOWER" && sideTowerStands) continue;
            list.Add(b);
        }
        return list;
    }

    /// <summary>Hit chance in percent (exe 00b3b7d0): 20 without hero, else (War Machines mastery + 2) × 10,
    /// plus 2 per 3 levels (rounded up) for the War Machines specialisation.</summary>
    public int CatapultHitChance(Unit catapult)
    {
        var h = HeroOf(catapult);
        if (!h.Present) return 20;
        int chance = ((int)h.SkillLevel(Sk.WarMachines) + 2) * 10;
        if (h.Specialization == "HERO_SPEC_WAR_MACHINES") chance += (h.Level + 2) / 3 * 2;
        return chance;
    }


    void CatapultShot(Unit u, SiegeBuilding aimed)
    {
        var target = aimed;
        var others = CatapultCandidates(includeDestroyed: true, exclude: aimed);
        if (others.Count > 0 && S.Rng.Range(1, 100) > CatapultHitChance(u))
            target = others[S.Rng.Range(0, others.Count - 1)];
        // Damage range by War Machines mastery (WarMachines.xdb Min/MaxDamageFactor), luck applies (vcall +0x1d0).
        var h = HeroOf(u);
        int m = (int)h.SkillLevel(Sk.WarMachines);
        Db.WarMachines.TryGetValue("WAR_MACHINE_CATAPULT", out var wm);
        int min = wm != null && wm.MinDamageFactor.Length > 0 ? (int)wm.MinDamageFactor[Math.Min(m, wm.MinDamageFactor.Length - 1)] : 150;
        int max = wm != null && wm.MaxDamageFactor.Length > 0 ? (int)wm.MaxDamageFactor[Math.Min(m, wm.MaxDamageFactor.Length - 1)] : 200;
        double luck = CatapultLuck(u);
        int damage = (int)Math.Round(S.Rng.Range(min, Math.Max(min, max)) * luck);
        Emit(new AbilityTriggered(u.Id, "CATAPULT_SHOT", null));
        DamageBuilding(target, damage, "CATAPULT");
    }

    double CatapultLuck(Unit u)
    {
        int luck = Math.Clamp(LuckOf(u), -5, 5);
        if (luck > 0 && RollPercent(luck * 10)) { Emit(new LuckProc(u.Id, true)); return Stats.Get("combat/LuckAndMorale/GoodLuck_DamageMultiplier", 2); }
        if (luck < 0 && RollPercent(-luck * 10)) { Emit(new LuckProc(u.Id, false)); return Stats.Get("combat/LuckAndMorale/BadLuck_DamageMultiplier", 0.5); }
        return 1;
    }

    /// <summary>Automatic catapult turn (no Catapult perk, or AI-controlled): shoots at random valid targets.</summary>
    bool CatapultTurn(Unit u)
    {
        var h = HeroOf(u);
        if (S.Siege == null) { FinishTurn(u, 0, allowMorale: false); return false; }
        if (h.HasPerk(Sk.Catapult) && !S.AiControlled[u.Side] && h.Present && CatapultTargets().Count > 0) return true;
        // One action: the aimed building is chosen once and receives all shots (exe FUN_00b3bb50 loop);
        // every shot rolls its own hit chance and may stray (FUN_00b3b7d0).
        var targets = CatapultTargets();
        if (targets.Count > 0)
        {
            var aimed = targets[S.Rng.Range(0, targets.Count - 1)];
            for (int i = 0; i < WarMachineShotsPerAction(u) && u.Alive; i++) CatapultShot(u, aimed);
        }
        FinishTurn(u, 0, allowMorale: false);
        return false;
    }

    /// <summary>Manual catapult shot (Catapult perk): the target cell selects the building.</summary>
    string? DoCatapultShot(Unit u, UseAbilityCmd c)
    {
        if (c.Cell is not Cell cell) return "needs a target cell";
        var b = CatapultTargets().Find(t => t.Blocked.Contains(cell));
        if (b == null) return "not a valid catapult target";
        // One player action fires every shot at the chosen building (exe FUN_00b3bb50).
        for (int i = 0; i < WarMachineShotsPerAction(u) && u.Alive; i++) CatapultShot(u, b);
        EndAction(u.Id, 0, allowMorale: false);
        return null;
    }

    // ------------------------------------------------------------------ towers

    void TowerTurn(Unit tower)
    {
        // Same choice as an automatic Ballista: the most valuable visible attacking stack.
        var target = S.Living.Where(o => o.Side != tower.Side && o.Kind == UnitKind.Creature && !o.HasEffect(H5.Combat.Fx.Invisible))
            .OrderByDescending(o => (double)o.Count * o.Power).ThenBy(o => o.Id).FirstOrDefault();
        if (target != null)
        {
            Emit(new AttackStarted(tower.Id, target.Id, AttackKind.Ranged, "TOWER"));
            // Tower damage ignores Defence and Evasion (fan manual p.308).
            DealDamage(target, tower.BaseMinDamage, tower.Id, AttackKind.Other, "TOWER");
            CheckEnd();
        }
        if (S.Phase != BattlePhase.Finished) FinishTurn(tower, 0, allowMorale: false);
    }

    // ------------------------------------------------------------------ moat

    /// <summary>Walking attackers stop in the first moat cell of their path (exe: the moat ends movement).</summary>
    List<Cell> StopAtMoat(Unit u, List<Cell> path)
    {
        if (S.Siege is not { Moat.Count: > 0 } sg || u.Flying || u.Side != 0 || path.Count < 2) return path;
        for (int i = 1; i < path.Count; i++)
            if (u.CellsAt(path[i]).Any(sg.Moat.Contains))
                return path.Take(i + 1).ToList();
        return path;
    }

    /// <summary>Applies the moat to an attacking stack that entered or landed in it.</summary>
    void EnterMoat(Unit u, IEnumerable<Cell> touched)
    {
        if (S.Siege is not { Moat.Count: > 0 } sg || u.Side != 0 || !u.Alive) return;
        var cells = touched.Where(sg.Moat.Contains).ToList();
        if (cells.Count == 0) return;
        Db.Moats.TryGetValue(sg.Town, out var moat);
        var defender = S.LivingOf(1).Where(o => o.Kind == UnitKind.Creature).ToList();

        if (moat is { Mines.Count: > 0 } && sg.Town == Towns.Academy)
        {
            // Academy land mines: each moat cell explodes once (blinds the victim, p.309).
            var fresh = cells.Where(c => !sg.SpentMines.Contains(c)).ToList();
            if (fresh.Count == 0) return;
            sg.SpentMines.Add(fresh[0]);
            Emit(new AbilityTriggered(u.Id, "MOAT_MINE", null));
            DealDamage(u, sg.MoatDamage, -1, AttackKind.Other, "MOAT");
            if (u.Alive) MoatSpell(u, moat.Mines[0].SpellToCast);
            return;
        }

        Emit(new AbilityTriggered(u.Id, "MOAT", null));
        DealDamage(u, sg.MoatDamage, -1, AttackKind.Other, "MOAT");
        if (!u.Alive || moat == null) return;
        foreach (var sp in moat.Spells) MoatSpell(u, sp);
        if (moat.Mines.Count > 0)
        {
            // Fortress: one of six offensive effect / defensive rune pairs (p.323).
            var pair = moat.Mines[S.Rng.Range(0, moat.Mines.Count - 1)];
            MoatSpell(u, pair.SpellToCast);
            if (defender.Count > 0) MoatFriendSpell(defender[S.Rng.Range(0, defender.Count - 1)], pair.SpellToCastOnFriend);
        }
    }

    void MoatSpell(Unit u, MoatSpellDef sp)
    {
        if (!u.Alive || sp.Spell is "SPELL_NONE" or "" || !RollPercent(sp.Chance)) return;
        switch (sp.Spell)
        {
            case "SPELL_ABILITY_ENTANGLING_ROOTS":
                AddEffect(u, new Effect { Id = H5.Combat.Fx.MoatRoots, Remaining = Math.Max(1, sp.SpellPower), SourceSide = 1 });
                break;
            case "SPELL_ABILITY_POISONOUS_ATTACK":
                AddEffect(u, new Effect { Id = H5.Combat.Fx.Poison, Value = sp.SpellPower, Actions = 3, SourceSide = 1 });
                break;
            case "SPELL_EFFECT_MASTER_OF_LIGHTNING":
                SetAtb(u.Id, GetAtb(u.Id) * (1 - Stats.Get("combat/HeroSkills/EmpoweredLightningATBDrop", 0.3)));
                break;
            case "SPELL_EFFECT_FROZEN":
                AddEffect(u, new Effect { Id = H5.Combat.Fx.Frozen, Remaining = Stats.Get("combat/HeroSkills/EmpoweredIceFreezeDuration", 0.3), SourceSide = 1 });
                break;
            case "SPELL_EFFECT_MARK_OF_FIRE":
                AddEffect(u, new Effect { Id = H5.Combat.Fx.MarkOfFire, Remaining = Stats.Get("combat/Creatures/Fortress/MarkOfFire_EffectDurationInRounds", 2), SourceSide = 1 });
                break;
            case "SPELL_EFFECT_FIRE_DAMAGE":
                AddEffect(u, new Effect { Id = H5.Combat.Fx.ArmorCrushed, Remaining = 1, SourceSide = 1 });
                break;
            default:
                if (Db.Spell(sp.Spell) is { } def)
                {
                    // Fortress' Slow is cast with Advanced mastery and Spellpower 3 when the data gives none (p.323).
                    var m = sp.Mastery == Mastery.None && sp.Spell == "SPELL_SLOW" ? Mastery.Advanced : sp.Mastery;
                    int power = sp.SpellPower == 0 && sp.Spell == "SPELL_SLOW" ? 3 : sp.SpellPower;
                    ApplyEffectSpell(-1, 1, def, m, power, u, 0, null);
                }
                break;
        }
    }

    void MoatFriendSpell(Unit t, MoatSpellDef sp)
    {
        if (!t.Alive || sp.Spell is "SPELL_NONE" or "") return;
        if (sp.Spell == "SPELL_ENCOURAGE") { AddAtb(t.Id, Stats.Get("combat/HeroSkills/Knight/Encourage_ATBIncrease", 0.33)); return; }
        if (!Rune(sp.Spell, t) && Db.Spell(sp.Spell) is { } def) ApplyEffectSpell(-1, 1, def, sp.Mastery, sp.SpellPower, t, 0, null);
    }

    // ------------------------------------------------------------------ Earthquake and Tremors

    /// <summary>
    /// Earthquake (exe 0097d7b0): every standing structure takes 50 × Range(min/50, max/50), where max is the
    /// spell's damage value and min its duration value for the mastery. With Tremors (HERO_SKILL_SHAKE_GROUND)
    /// every creature inside the walls also takes round(ShakeGroundDamage + ShakeGroundDamagePerPower × SP)
    /// and loses ShakeGroundATBReduce ATB.
    /// </summary>
    void SiegeEarthquake(int side, HeroState? hero, SpellDef sp, Mastery m, double power)
    {
        if (S.Siege is not { } sg) return;
        int max = Math.Max(0, (int)Math.Round(sp.DamageAt(m).Eval(power)) / 50);
        int min = Math.Max(0, (int)Math.Round(sp.DurationAt(m).Eval(power)) / 50);
        foreach (var b in sg.Buildings.Where(b => b.IsSolid && !b.Destroyed).ToList())
            DamageBuilding(b, 50 * S.Rng.Range(Math.Min(min, max), max), "SPELL_EARTHQUAKE");
        if (hero == null || !hero.HasPerk("HERO_SKILL_SHAKE_GROUND")) return;
        int dmg = (int)Math.Round(power * Stats.Get("combat/HeroSkills/Warlock/ShakeGroundDamagePerPower", 5) + Stats.Get("combat/HeroSkills/Warlock/ShakeGroundDamage", 10));
        double atb = Stats.Get("combat/HeroSkills/Warlock/ShakeGroundATBReduce", 0.1);
        foreach (var u in S.Living.Where(o => o.Kind == UnitKind.Creature && InsideTown(o)).ToList())
        {
            DealDamage(u, dmg, Actor.Hero(side), AttackKind.Spell, "TREMORS");
            if (u.Alive) SetAtb(u.Id, GetAtb(u.Id) - atb);
        }
    }

    // ------------------------------------------------------------------ Consume Artifact

    /// <summary>
    /// Consume Artifact (HERO_SKILL_MELT_ARTIFACT): heals and resurrects a friendly stack carrying a creature
    /// artifact by MeltArtifact_HealPerArtifactEffectPerHeroLevel × level × (1 + crafted effects); the hero spends
    /// MeltArtifact_ATBUsage of a turn (DefaultStats; fan manual p.137).
    /// </summary>
    void ConsumeArtifact(HeroState h, Unit t, ref double reset)
    {
        int heal = (int)(Stats.Get("combat/HeroSkills/Wizard/MeltArtifact_HealPerArtifactEffectPerHeroLevel", 10) * h.Level * (1 + Math.Max(0, t.ArtifactEffects)));
        t.ArtifactEffects = -1;
        Emit(new AbilityTriggered(Actor.Hero(h.Side), "CONSUME_ARTIFACT", t.Id));
        Heal(t, heal, resurrect: true, "CONSUME_ARTIFACT");
        reset = 1 - Stats.Get("combat/HeroSkills/Wizard/MeltArtifact_ATBUsage", 0.25);
    }
}
