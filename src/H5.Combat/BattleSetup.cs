using H5.Core.Data;

namespace H5.Combat;

/// <summary>Everything needed to start a battle for one side.</summary>
public sealed class ArmySetup
{
    public HeroInstance? Hero;
    public List<ArmySlot> Army = new();
    public static ArmySetup FromHero(HeroInstance h) => new() { Hero = h, Army = h.Army.ToList() };
}

public static class BattleSetup
{
    /// <summary>
    /// Creates a battle between two armies. Side 0 is the attacker (left edge), side 1 the defender.
    /// Obstacles are generated from the DefaultStats obstacle rules unless provided.
    /// </summary>
    public static BattleState Create(GameDatabase db, ArmySetup attacker, ArmySetup defender, ulong seed,
        IEnumerable<Cell>? obstacles = null, bool skipTactics = false)
    {
        var s = new BattleState { Rng = new Rng(seed) };
        var sides = new[] { attacker, defender };
        for (int side = 0; side < 2; side++)
            s.Heroes[side] = MakeHero(db, sides[side].Hero, side);

        s.Obstacles = obstacles?.ToList() ?? GenerateObstacles(db, s);

        // Tactics: a third deployment column, cancelled if both heroes have it (fan manual p.108).
        bool t0 = s.Heroes[0].HasPerk(Sk.Tactics), t1 = s.Heroes[1].HasPerk(Sk.Tactics);
        s.DeployColumns0 = t0 && !t1 ? 3 : 2;
        s.DeployColumns1 = t1 && !t0 ? 3 : 2;

        for (int side = 0; side < 2; side++)
        {
            var h = s.Heroes[side];
            var slots = sides[side].Army.Where(a => a.Count > 0 && db.Creatures.ContainsKey(a.Creature)).Take(7).ToList();
            var placed = DefaultPlacement(db, s, side, slots);
            for (int i = 0; i < slots.Count; i++)
                AddCreature(db, s, side, slots[i].Creature, slots[i].Count, placed[i], summoned: false);
            if (h.Present && sides[side].Hero is { } hi)
            {
                if (hi.Ballista) AddWarMachine(db, s, side, "WAR_MACHINE_BALLISTA");
                if (hi.FirstAidTent) AddWarMachine(db, s, side, "WAR_MACHINE_FIRST_AID_TENT");
                if (hi.AmmoCart) AddWarMachine(db, s, side, "WAR_MACHINE_AMMO_CART");
            }
        }
        if (skipTactics) s.TacticsDone = new[] { true, true };
        return s;
    }

    public static BattleState CreateDuel(GameDatabase db, int presetA, int presetB, ulong seed, bool skipTactics = false) =>
        Create(db, ArmySetup.FromHero(db.DuelPresets[presetA].Hero), ArmySetup.FromHero(db.DuelPresets[presetB].Hero), seed, skipTactics: skipTactics);

    static HeroState MakeHero(GameDatabase db, HeroInstance? hi, int side)
    {
        var h = new HeroState { Side = side };
        if (hi == null) return h;
        h.Present = true;
        h.HeroId = hi.Shared?.Id ?? "";
        h.Name = hi.Shared?.Name is { Length: > 0 } n ? n : hi.DisplayName;
        h.Class = hi.Class;
        h.Town = hi.Town;
        h.Specialization = hi.Shared?.Specialization ?? "";
        h.Level = Math.Max(1, hi.Level);
        h.Attack = hi.Attack; h.Defence = hi.Defence; h.SpellPower = hi.SpellPower; h.Knowledge = hi.Knowledge;
        h.Skills = hi.Skills.Where(sk => sk.SkillId != "HERO_SKILL_NONE").ToList();
        h.Perks = hi.Perks.ToList();
        h.Spells = hi.Spells.ToList();
        h.Artifacts = hi.Artifacts.ToList();
        h.FavoriteEnemies = hi.FavoriteEnemies.ToList();
        foreach (var aid in hi.Artifacts)
            if (db.Artifacts.TryGetValue(aid, out var a))
            {
                h.Attack += a.Attack; h.Defence += a.Defence; h.SpellPower += a.SpellPower; h.Knowledge += a.Knowledge;
                h.Morale += a.Morale; h.Luck += a.Luck;
            }
        // Artifact sets (DefaultStats ArtifactSets / ArtifactsSetsEffectsConsts, set descriptions).
        foreach (var (set, members) in db.ArtifactSets)
        {
            int pieces = members.Count(hi.Artifacts.Contains);
            if (pieces > 0) h.SetPieces[set] = pieces;
        }
        int all = 0;
        int dragon = Specials.Pieces(h, Specials.Dragonish);
        if (dragon >= 2) all += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_2_AllStats", 1);
        if (dragon >= 6) all += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_6_AllStats", 3);
        if (Specials.Pieces(h, Specials.Runic) >= 2) all += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Runic_2_AllStats", 1);
        h.Attack += all; h.Defence += all; h.SpellPower += all; h.Knowledge += all;
        if (Specials.Pieces(h, Specials.Demonic) >= 2) h.Attack += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Demonic_2_Attack", 5);
        if (hi.Shared != null) h.SpecFamily = Specials.FamilyFromDescription(db, hi.Shared.SpecializationDescription);
        // Leadership and Luck skills (fan manual p.118, p.124).
        h.Morale += (int)h.SkillLevel(Sk.Leadership);
        h.Luck += (int)h.SkillLevel(Sk.Luck);
        if (h.HasPerk(Sk.FallenKnight)) h.Morale -= 1;
        h.Attack = Math.Max(0, h.Attack); h.Defence = Math.Max(0, h.Defence);
        h.SpellPower = Math.Max(0, h.SpellPower); h.Knowledge = Math.Max(0, h.Knowledge);
        double manaPerKnowledge = db.Stats.Get("adventure/HeroManaPerKnowledge", 10);
        double intel = h.HasPerk(Sk.Intelligence) || h.HasPerk(Sk.BarbIntelligence) ? db.Stats.Get("adventure/HeroSkills/Intelligence_ManaPointsBonus", 1.5) : 1;
        h.MaxMana = h.Mana = (int)(h.Knowledge * manaPerKnowledge * intel);
        return h;
    }

    public static Unit AddCreature(GameDatabase db, BattleState s, int side, string creatureId, int count, Cell pos, bool summoned)
    {
        var c = db.Creature(creatureId);
        var h = s.Heroes[side];
        var u = new Unit
        {
            Id = s.NextId++, Side = side, Kind = UnitKind.Creature, CreatureId = c.Id, Name = c.Name, Town = c.Town,
            Tier = c.Tier, Level = c.Level, Upgraded = c.Upgrade,
            BaseAttack = c.Attack, BaseDefence = c.Defence, BaseMinDamage = c.MinDamage, BaseMaxDamage = c.MaxDamage,
            BaseHealth = c.Health, BaseSpeed = c.Speed, BaseInitiative = c.Initiative,
            MaxShots = c.Shots, Shots = c.Shots, Flying = c.Flying, Size = c.CombatSize, Power = c.Power, WeeklyGrowth = c.WeeklyGrowth,
            Abilities = c.Abilities.OrderBy(a => a, StringComparer.Ordinal).ToList(),
            Spells = c.KnownSpells.Select(k => new KnownSpell { Spell = k.Spell, Mastery = k.Mastery }).ToList(),
            Mana = c.SpellPoints, MaxMana = c.SpellPoints, Element1 = c.Element1, Element2 = c.Element2,
            Count = count, InitialCount = count, Pos = pos, StartPos = pos, Summoned = summoned,
        };
        if (h.Present)
        {
            // Vitality: +2 HP per creature (fan manual p.112).
            if (h.HasPerk(Sk.Toughness)) u.BaseHealth += 2;
            // Battle Frenzy: +1 min and max damage (p.107).
            if (h.HasPerk(Sk.Frenzy)) { u.BaseMinDamage += 1; u.BaseMaxDamage += 1; }
            // Nature's Wrath: Sylvan creatures +1 max damage (p.107).
            if (h.HasPerk(Sk.NaturesWrath) && u.Town == Towns.Sylvan) u.BaseMaxDamage += 1;
            // March of the Golems: golems +2 speed/initiative (p.122).
            if (h.HasPerk(Sk.MarchOfGolems) && u.Has(Ab.Mechanical)) { u.BaseSpeed += 2; u.BaseInitiative += 2; }
            // Creature specialisations: +1 Attack/Defense per two levels, starting on first level.
            if (h.SpecFamily.Contains(u.CreatureId)) { u.BaseAttack += (h.Level + 1) / 2; u.BaseDefence += (h.Level + 1) / 2; }
            if (h.Specialization == "HERO_SPEC_WYVERN_TAMER" && (u.CreatureId.Contains("WYVERN") || u.CreatureId.Contains("PAOKAI")))
                u.BaseHealth += (int)Specials.LevelFormula(db.Stats, "combat/HeroSpecializations/Barbarian/WyvernTamer_HealthBonus", h.Level, 1, 1, 2);
            if (Specials.Pieces(h, Specials.Ogres) >= 2)
            {
                u.BaseAttack += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Ogres_2_Creature_Attack", 3);
                u.BaseHealth += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Ogres_2_Creature_HitPoints", 2);
            }
            if (Specials.Pieces(h, Specials.Dragonish) >= 4 && u.Tier == (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_4_Creature_Tier", 7))
            {
                u.BaseAttack += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_4_Creature_Attack", 5);
                u.BaseDefence += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_4_Creature_Defence", 5);
                u.BaseHealth += (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_4_Creature_HitPoints", 20);
            }
            if (Specials.Pieces(h, Specials.Magis) >= 2) u.Mana = u.MaxMana = u.MaxMana * (int)db.Stats.Get("ArtifactsSetsEffectsConsts/Magis_2_Casters_SpellpowerMultiplier", 2);
        }
        u.MaxHealthCache = u.BaseHealth;
        u.TopHealth = u.BaseHealth;
        u.RetaliationsLeft = u.Has(Ab.UnlimitedRetaliation) ? int.MaxValue : 1;
        s.Units.Add(u);
        return u;
    }

    public static Unit AddWarMachine(GameDatabase db, BattleState s, int side, string machineId)
    {
        db.WarMachines.TryGetValue(machineId, out var wm);
        var h = s.Heroes[side];
        int m = (int)h.SkillLevel(Sk.WarMachines);
        double perSkill = db.Stats.Get("adventure/HeroSkills/WarMachines_HealthBonusPerSkillTrained", 100);
        double perkMult = db.Stats.Get("adventure/HeroSkills/WarMachines_PerkSpecificHealthMultiplier", 2);
        int hp = (wm?.Health ?? 100) + (int)(perSkill * m);
        string perk = machineId switch
        {
            "WAR_MACHINE_BALLISTA" => Sk.Ballista, "WAR_MACHINE_FIRST_AID_TENT" => Sk.FirstAid, _ => Sk.Catapult,
        };
        if (h.HasPerk(perk)) hp = (int)(hp * perkMult);
        int att = wm != null && wm.Offense.Length > 0 ? wm.Offense[Math.Min(m, wm.Offense.Length - 1)] : 0;
        if (machineId == "WAR_MACHINE_BALLISTA" && h.Specialization == "HERO_SPEC_WAR_MACHINES") att += h.Level;   // +1 Attack per level
        int def = wm != null && wm.Defense.Length > 0 ? wm.Defense[Math.Min(m, wm.Defense.Length - 1)] : 0;
        // Ballista damage is (min..max factor) x (hero Attack + Knowledge) (fan manual p.307).
        double mult = h.Attack + h.Knowledge;
        int minD = wm != null && wm.MinDamageFactor.Length > 0 ? (int)Math.Round(wm.MinDamageFactor[Math.Min(m, wm.MinDamageFactor.Length - 1)] * mult) : 0;
        int maxD = wm != null && wm.MaxDamageFactor.Length > 0 ? (int)Math.Round(wm.MaxDamageFactor[Math.Min(m, wm.MaxDamageFactor.Length - 1)] * mult) : 0;
        double init = wm?.Initiative ?? 10;
        if (h.HasPerk(Sk.RunicMachines)) init += db.Stats.Get("combat/HeroSkills/Runemage/RunicMachines_InitiativeBonus", 3);
        // War machines stand just outside the grid behind their army (column -1 / Width).
        int x = side == 0 ? -1 : s.Width;
        int y = machineId switch { "WAR_MACHINE_BALLISTA" => 1, "WAR_MACHINE_FIRST_AID_TENT" => s.Height - 2, _ => s.Height / 2 };
        var u = new Unit
        {
            Id = s.NextId++, Side = side, Kind = UnitKind.WarMachine, CreatureId = machineId,
            Name = machineId.Replace("WAR_MACHINE_", "").Replace('_', ' '), Town = Towns.Neutral, Tier = 1, Level = 1,
            BaseAttack = att, BaseDefence = def, BaseMinDamage = minD, BaseMaxDamage = maxD, BaseHealth = hp,
            BaseSpeed = 0, BaseInitiative = init, MaxShots = wm?.Shots ?? 0, Shots = wm?.Shots ?? 0, Size = 1, Power = 1,
            Abilities = new List<string> { Ab.Mechanical },
            Count = 1, InitialCount = 1, Pos = new Cell(x, y), StartPos = new Cell(x, y),
        };
        u.MaxHealthCache = u.TopHealth = hp;
        u.RetaliationsLeft = 0;
        s.Units.Add(u);
        return u;
    }

    /// <summary>Spreads stacks along the deployment edge, large creatures on even rows.</summary>
    static List<Cell> DefaultPlacement(GameDatabase db, BattleState s, int side, List<ArmySlot> slots)
    {
        var result = new List<Cell>();
        var taken = new HashSet<Cell>(s.Obstacles);
        int n = slots.Count;
        for (int i = 0; i < n; i++)
        {
            int size = db.Creature(slots[i].Creature).CombatSize;
            // Preferred row: evenly spaced (original duel layout spreads 7 stacks over 10 rows).
            int pref = n == 1 ? s.Height / 2 - 1 : (int)Math.Round(i * (s.Height - size) / (double)Math.Max(1, n - 1));
            Cell? best = null;
            int bestScore = int.MaxValue;
            for (int col = 0; col < 2; col++)
            for (int y = 0; y <= s.Height - size; y++)
            {
                int x = side == 0 ? col : s.Width - size - col;
                var p = new Cell(x, y);
                if (!Fits(s, taken, p, size)) continue;
                int score = Math.Abs(y - pref) * 10 + col;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            var cell = best ?? new Cell(side == 0 ? 0 : s.Width - size, 0);
            for (int dx = 0; dx < size; dx++) for (int dy = 0; dy < size; dy++) taken.Add(new Cell(cell.X + dx, cell.Y + dy));
            result.Add(cell);
        }
        return result;
    }

    static bool Fits(BattleState s, HashSet<Cell> taken, Cell p, int size)
    {
        for (int dx = 0; dx < size; dx++)
        for (int dy = 0; dy < size; dy++)
        {
            var c = new Cell(p.X + dx, p.Y + dy);
            if (!s.InBounds(c) || taken.Contains(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Random obstacles following the DefaultStats "combat/Obstacles" limits. The original game takes obstacle
    /// shapes from the arena's ObstaclesGroup; until arenas are decoded, small = 1x1 and big = 2x2 blocks
    /// placed outside the FreeLines deployment columns.
    /// </summary>
    public static List<Cell> GenerateObstacles(GameDatabase db, BattleState s)
    {
        var rng = s.Rng;
        int free = (int)db.Stats.Get("combat/Obstacles/FreeLines", 2) + 1; // keep the Tactics column clear too
        int maxArea = (int)db.Stats.Get("combat/Obstacles/ObstaclesMaxArea", 21);
        int big = rng.Range((int)db.Stats.Get("combat/Obstacles/MinBigObstacles", 1), (int)db.Stats.Get("combat/Obstacles/MaxBigObstacles", 2));
        int small = rng.Range((int)db.Stats.Get("combat/Obstacles/MinSmallObstacles", 0), (int)db.Stats.Get("combat/Obstacles/MaxSmallObstacles", 6));
        var cells = new HashSet<Cell>();
        var list = new List<Cell>();
        void Try(int size)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int x = rng.Range(free, s.Width - free - size);
                int y = rng.Range(0, s.Height - size);
                var block = new List<Cell>();
                for (int dx = 0; dx < size; dx++) for (int dy = 0; dy < size; dy++) block.Add(new Cell(x + dx, y + dy));
                // Keep obstacles apart so they never seal off a lane.
                if (block.Any(c => Cell.Neighbours8.Any(d => cells.Contains(c + d)) || cells.Contains(c))) continue;
                if (cells.Count + block.Count > maxArea) return;
                foreach (var c in block) { cells.Add(c); list.Add(c); }
                return;
            }
        }
        for (int i = 0; i < big; i++) Try(2);
        for (int i = 0; i < small; i++) Try(1);
        return list;
    }
}
