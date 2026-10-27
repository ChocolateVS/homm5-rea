using H5.Adventure.Map;

namespace H5.Rmg;

/// <summary>An object placed by the generator, in generator coordinates.</summary>
public sealed class RmgObject
{
    public required string Shared;
    public required SharedDef Def;
    public int X, Y, Floor;
    public int Quarter;              // quarter turn of the angle (exe 0x00b0bbd0)
    public int Owner = -1;
    public int Zone;
    public string? Creature;
    public int Amount;
    public string? Note;             // what placed it (mine, guard, shop...)
    /// <summary>Further guard stacks of a two-stack army template (written as AdditionalStacks, exe 0x00914cc0).</summary>
    public List<(string Creature, int Count)> AdditionalStacks = new();
    /// <summary>The guard came from a two-stack army template: 0x00914cc0 writes Mood = 2 (HOSTILE); 0x00914b40 writes none.</summary>
    public bool TwoStack;
    /// <summary>A neutral town's garrison (armySlots), 0x00ba6b10.</summary>
    public List<(string Creature, int Count)> Garrison = new();
    /// <summary>A teleport's GroupID (0x00914980): min zone id x 100 + max zone id; the adventure engine pairs teleports by it.</summary>
    public int? GroupId;
    /// <summary>A town's Specialization (0x00923e50), a TownSpecialization path.</summary>
    public string? Specialization;
    public float? Angle;             // the angle the exe passed in radians
    public float Rot => Angle ?? Quarter * MathF.PI / 2;
}

public sealed partial class RmgGenerator
{
    /// <summary>0x00ff9b24: "/MapObjects/RandomTown.xdb#xpointer(/AdvMapTownShared)".</summary>
    const string RandomTownShared = "MapObjects/RandomTown.xdb";

    SharedCatalog? _catalog;
    SharedCatalog Catalog => _catalog ??= new SharedCatalog(_db.Xdb);

    /// <summary>
    /// The screen's Resources / Experience combos (InitialParams.ResourceMultiplier = generator +0xa8,
    /// ExpMultiplier = +0xb0; RESOURCE_/EXP_MISERABLE, LITTLE, NORMAL, LOTS, MUCH = 0..4) select these factors
    /// (switches at 0x00905eac / 0x00905fc6 and 0x00907150): +0xa8 scales TreasureDensity, +0xb0
    /// TreasureChestDensity and UpgBuildingsDensity.
    /// </summary>
    static readonly float[] Multipliers = { 0.2f, 0.5f, 1f, 2f, 4f };
    static float Multiplier(int level) => level is >= 0 and <= 4 ? Multipliers[level] : 1f;

    /// <summary>Monster strength coefficients by monster level (exe 0x00ba7690: 0.4, 0.9, 1.7, 4, 12).</summary>
    static readonly float[] MonsterCoef = { 0.4f, 0.9f, 1.7f, 4f, 12f };

    // Neighbour tables of the map builder (pairs: first index, second index).
    static readonly (int Dx, int Dy)[] Ortho4 = { (0, -1), (1, 0), (0, 1), (-1, 0) };                                   // 0x00f467d8
    static readonly (int Dx, int Dy)[] Diag4 = { (1, -1), (1, 1), (-1, 1), (-1, -1) };                                  // 0x00f467f8
    static readonly (int Dx, int Dy)[] Around8 = { (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1) }; // 0x00f46798, 0x00f76b68

    // ------------------------------------------------------------------ placement primitives

    /// <summary>
    /// The placers' loop (0x0091c4a0, 0x00920630, 0x0091e910, 0x0091f140, 0x00920370 ... 0x009221d0, 0x0091f8a0):
    /// while candidates remain, rand(n) picks one and rand(4) a quarter turn; 0x00919be0 tests the footprint (a failure
    /// erases the candidate); then 0x009145b0 creates the object and 0x0091a420 stamps it.
    /// </summary>
    RmgObject? PlaceRandom(RmgResult r, RmgZone z, string shared, List<(int X, int Y)> candidates, string note,
        List<(float X, float Y)>? list = null)
    {
        var d = Catalog.Get(shared);
        var pool = candidates.ToList();
        while (pool.Count > 0)
        {
            int i = Rng.Next(pool.Count);
            var (x, y) = pool[i];
            float a = QuarterAngle(Rng.Next(4));
            if (FootprintFits(z, x, y, a, d))
            {
                var o = CreateObject(r, z, shared, x, y, a, note);
                Stamp(z, x, y, a, d, list);
                return o;
            }
            pool.RemoveAt(i);
        }
        r.Warnings.Add($"Can't place {note} {shared} at zone #{z.Id}, floor {z.Floor}");
        return null;
    }

    /// <summary>
    /// exe 0x0091bb10 (prisons, cartographers, shrines, resource/treasury/luck-morale buildings, shops, obelisks,
    /// Redwood Observatory, Den of Thieves, Graal): 0x009191a0(4), its maximum M over the zone's tiles (0x00919710),
    /// then every zone tile with distance &gt; 2M/3 and border distance &gt; 0. No fallback.
    /// </summary>
    List<(int X, int Y)> FarCandidates(RmgZone z)
    {
        DistanceField(z, 4);
        var f = Floors[z.Floor];
        int max = MaxDist(z, z.Tiles);
        return z.Tiles.Where(t => f.Dist[f.At(t.X, t.Y)] > max * 2 / 3 && f.BorderDist[f.At(t.X, t.Y)] > 0).ToList();
    }

    /// <summary>0x009191a0(4) and 0x00919710 over <paramref name="list"/>, then the list's tiles whose distance exceeds num/den of the maximum.</summary>
    List<(int X, int Y)> SpreadCandidates(RmgZone z, List<(int X, int Y)> list, int num, int den)
    {
        DistanceField(z, 4);
        var f = Floors[z.Floor];
        int threshold = MaxDist(z, list) * num / den;
        return list.Where(t => f.Dist[f.At(t.X, t.Y)] > threshold).ToList();
    }

    // ------------------------------------------------------------------ guards (exe 0x00ba7690)

    /// <summary>
    /// Guard army for a guard power (exe 0x00ba7690). Power below 100 places nothing (no draws). The value is
    /// round(power x MonsterCoef[level]); then Float(0,1) &lt; 0.6 tries a two-stack army template with
    /// round(value x 0.9) (0x00ba71b0), and otherwise, or when no template fits, one stack (0x00ba6910).
    /// Returns the stacks (main stack first) and whether they came from a template, or null.
    /// </summary>
    (List<(RmgCreature Creature, int Count)> Stacks, bool Template)? PickGuard(int power)
    {
        if (power < 100) return null;
        int value = (int)Math.Round((double)power * MonsterCoef[Math.Clamp(MonsterLevel, 0, 4)], MidpointRounding.ToEven);
        if (Rng.Float(0, 1) < 0.6f)
        {
            var two = PickArmyTemplate((int)Math.Round((double)value * 0.9f, MidpointRounding.ToEven));
            if (two != null) return (two, true);
        }
        var one = PickSingleStack(value);
        return one == null ? null : (new List<(RmgCreature, int)> { one.Value }, false);
    }

    RmgCreature? CreatureAt(int index) => index >= 0 && index < _db.Creatures.Count && _db.Creatures[index].Index == index
        ? _db.Creatures[index] : _db.Creatures.FirstOrDefault(c => c.Index == index);

    /// <summary>
    /// exe 0x00ba71b0: templates of the group whose [MinPower, MaxPower] contains the value, one picked with
    /// rand(n); each stack gets coef x (value / sum(coef x power)), the last one also the remainder / its power.
    /// Accepted (0x00ba7690) when the first stack has a creature and a count.
    /// </summary>
    List<(RmgCreature Creature, int Count)>? PickArmyTemplate(int value)
    {
        if (value < 100) return null;
        var fits = new List<int>();
        for (int i = 0; i < _db.ArmyTemplates.Count; i++)
            if (value >= _db.ArmyTemplates[i].MinPower && value <= _db.ArmyTemplates[i].MaxPower) fits.Add(i);
        if (fits.Count == 0) return null;
        var t = _db.ArmyTemplates[fits[Rng.Next(fits.Count)]];
        var stacks = new List<(RmgCreature? C, int Count)>();
        int sum = 0;
        foreach (var (cr, coef) in t.Stacks)
        {
            var c = CreatureAt(cr);
            stacks.Add((c, coef));
            sum += coef * (c?.Power ?? 0);
        }
        if (stacks.Count == 0 || sum <= 0) return null;
        int mult = value / sum, left = value;
        for (int i = 0; i < stacks.Count; i++)
        {
            int pw = stacks[i].C?.Power ?? 0;
            int count = stacks[i].Count * mult;
            left -= count * pw;
            if (i == stacks.Count - 1 && left > 0 && pw > 0) { count += left / pw; left -= left / pw * pw; }
            stacks[i] = (stacks[i].C, count);
        }
        if (stacks[0].C is not { Index: not 0 } || stacks[0].Count == 0) return null;
        return stacks.Where(st => st.C != null).Select(st => (st.C!, st.Count)).ToList();
    }

    /// <summary>
    /// exe 0x00ba6910: r = rand(30); every creature except ids 0, 0x59, 0x72 whose Power x (r+10) lies within
    /// a factor k of the value is appended (k = 1.1, x1.2 per pass; the list is not cleared between passes)
    /// until it holds at least 10 entries; one is picked with rand(n); count = trunc(value / Power).
    /// </summary>
    (RmgCreature Creature, int Count)? PickSingleStack(int value)
    {
        if (value < 100) return null;
        int r = Rng.Next(30);
        var cands = new List<RmgCreature>();
        float k = 1.1f;
        for (int pass = 0; cands.Count < 10 && pass < 200; pass++)
        {
            foreach (var c in _db.Creatures)
            {
                if (c.Index == 0 || c.Index == 0x59 || c.Index == 0x72) continue;
                // x87 at double precision; k is stored as a float after each pass (FSTP at 0x00ba6a7d).
                double lo = (double)(r + 10) / k * c.Power, hi = (double)(r + 10) * k * c.Power;
                if (lo <= value && value <= hi) cands.Add(c);
            }
            k *= 1.2f;
        }
        if (cands.Count == 0) return null;
        var pick = cands[Rng.Next(cands.Count)];
        return (pick, (int)((float)value / pick.Power));
    }

    /// <summary>
    /// exe 0x009145b0, the map-object creator every placer calls once its footprint test passed (and the guard
    /// creators 0x00914b40 / 0x00914cc0, the treasure blocks' 0x00915010): the object's name gets the number
    /// (Next(0xffff) &lt;&lt; 16) + Next(0xffff), two draws.
    /// </summary>
    void CreateObjectDraws()
    {
        Rng.Next(0xffff);
        Rng.Next(0xffff);
    }

    /// <summary>
    /// A guard (0x00ba7690 → 0x00ba6f00 → 0x00914b40 / 0x00914cc0) at (x, y) turned by <paramref name="angle"/>. The
    /// guard creators write no tile flags; the callers do (mines, upgrade buildings) or do not (connections).
    /// </summary>
    RmgObject? PlaceGuard(RmgResult r, RmgZone z, int x, int y, int power, string note, float angle)
    {
        var f = Floors[z.Floor];
        if (!f.In(x, y)) return null;
        var g = PickGuard(power);
        if (g == null || g.Value.Stacks[0].Creature.MonsterShared == null) return null;
        var (c, count) = g.Value.Stacks[0];
        var d = Catalog.Get(c.MonsterShared!);
        CreateObjectDraws();
        var o = new RmgObject
        {
            Shared = c.MonsterShared!, Def = d, X = x, Y = y, Floor = z.Floor, Zone = z.Id, Angle = angle, Quarter = ExeQuarter(angle),
            Creature = c.Id, Amount = count, Note = note, TwoStack = g.Value.Template,
        };
        o.AdditionalStacks.AddRange(g.Value.Stacks.Skip(1).Select(st => (st.Creature.Id, st.Count)));
        r.Objects.Add(o);
        return o;
    }

    int GuardPower(int level) => _db.Params.BasicLevelGuardPower * level;

    // ------------------------------------------------------------------ terrain

    void PaintTerrain(RmgResult r)
    {
        foreach (var f in Floors)
        {
            var tiles = new string[W * H];
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    int id = f.Zone[f.At(x, y)];
                    string tile = f.Index == 0 ? _db.Params.DefaultSurfaceTile : _db.Params.DefaultSubterraTile;
                    if (id != -1 && f.Zones.TryGetValue(id, out var z) && _db.Race(z.Preset).DefaultTile is { Length: > 0 } t) tile = t;   // 0x00918270: zone +0x20
                    tiles[y * W + x] = tile;
                }
            r.TileLayers.Add(tiles);
        }
    }

    // ------------------------------------------------------------------ towns (exe 0x009058e0)

    void PlaceTowns(RmgResult r)
    {
        foreach (var z in Zones.OrderBy(z => z.Def.Index))
        {
            var f = Floors[z.Floor];
            if (!z.Def.Town)
            {
                // exe 0x00919810: zones without a town anchor their objects at the tile centroid.
                if (z.Tiles.Count > 0)
                    z.TownPos = ((int)MathF.Round(z.Tiles.Sum(t => (float)t.X) / z.Tiles.Count), (int)MathF.Round(z.Tiles.Sum(t => (float)t.Y) / z.Tiles.Count));
                z.AnchorOnly = true;
                continue;
            }
            var race = _db.Race(z.Race);          // 0x00923e50: TownProto of the +0x1c preset
            var objects = _db.Race(z.Preset);     // 0x009228e0: OverTownCenterObjects of the +0x20 preset
            // 0x00923e50: with RandomTowns (third argument, generator +0xa5) the shared is 0x00ff9b24 (init 0x00dc8890).
            string? townShared = _o.RandomTowns ? RandomTownShared : race.TownProto;
            if (townShared == null) continue;
            var d = Catalog.Get(townShared);
            // exe 0x00923e50: candidates are zone tiles deeper than R/2; each try draws a tile and a
            // quarter turn; the town's anchor (its first active tile) must be at least 2R/3 from the
            // border (relaxed after 100 tries); footprint failures (0x00919be0) drop the candidate.
            var cands = z.Tiles.Where(t => f.BorderDist[f.At(t.X, t.Y)] > z.Radius / 2).ToList();
            RmgObject? town = null;
            int px = 0, py = 0;
            for (int tries = 0; cands.Count > 0 && town == null; tries++)
            {
                int i = Rng.Next(cands.Count);
                var (cx, cy) = cands[i];
                float a = QuarterAngle(Rng.Next(4));
                var (ax, ay) = VisitTile(d, cx, cy, a);
                if (ay < 1 || ay >= H - 1 || ax < 1 || ax >= W - 1) continue;
                if (f.BorderDist[f.At(ax, ay)] < z.Radius * 2 / 3 && tries < 100) continue;
                if (!FootprintFits(z, cx, cy, a, d)) { cands.RemoveAt(i); continue; }
                town = CreateObject(r, z, townShared, cx, cy, a, "town");
                Stamp(z, cx, cy, a, d, null);
                (px, py) = (cx, cy);
                z.TownPos = (ax, ay);
                z.TownPlaced = true;
            }
            if (town == null) { r.Warnings.Add($"no room for town in zone {z.Id}"); continue; }
            if (z.Player > 0)
            {
                town.Owner = z.Player - 1;
                r.StartTowns[z.Player - 1] = town;
            }
            else
                // 0x00918b60 → 0x00ba6b10: a neutral town's guard is its garrison (TownGuardStrenght x BasicLevelGuardPower).
                TownGarrison(town, GuardPower(z.Def.TownGuardStrength), z.Race is >= 3 and <= 10 ? z.Race : 1);
            // 0x009228e0 (no random towns): one of the preset's OverTownCenterObjects at the town's position.
            if (objects.OverTownCenterObjects.Count > 0)
            {
                float a = QuarterAngle(Rng.Next(4));
                var shared = objects.OverTownCenterObjects[Rng.Next(objects.OverTownCenterObjects.Count)];
                CreateObject(r, z, shared, px, py, a, "over town centre");
            }
            // 0x00923e50 after 0x009228e0: the specializations of RMG/TownRandomSpecGroup (0x00926510, link +0x44) whose
            // TownType (+0x6c) is the town's (+0xfc) and RandomTown (+0x70) TOWN_RANDOM; Next(n) picks the town's
            // "Specialization". None: no draw, and 0x00923e50 returns false.
            var specs = _db.TownSpecs.Where(sp => sp.TownType == town.Def.TownType && sp.RandomTown is "" or "TOWN_RANDOM").ToList();
            if (specs.Count == 0) continue;
            town.Specialization = specs[Rng.Next(specs.Count)].Path;
            // The underground classes' vf 0x20 (0x00bac3d0 / 0x0092abe0 / 0x00928a00) call 0x00923e50 and, when it returns
            // true, the town's point lights (0x00babbf0 / 0x0092a400 / 0x00928220): Next(3), then Next(10).
            // TODO(exe): the lights' values (pointLights) are not emitted.
            if (z.Class is RmgZoneClass.SubInferno or RmgZoneClass.Subterra or RmgZoneClass.Dwarven)   // CGameWaterBorderedZone's vf 0x20 is 0x00923e50 itself
            {
                Rng.Next(3);
                Rng.Next(10);
            }
        }
    }

    /// <summary>
    /// exe 0x00ba6b10 (power, town, race): below 100 nothing; monster level 0 scales the power by 0.7, level 2 by 1.5
    /// (0x00e15698, 0x00e3a3ac, chopped). Tier 1..7: count = (Next(20) + 20) x (9 − tier) / 2 (shift); the creatures of
    /// the tier and the town's race (ids 0, 0x59, 0x72 excluded), none → stop; one by Next(n); a count worth more than
    /// what is left is cut to left / Power and ends the garrison; armySlots[tier − 1] = (creature, count).
    /// </summary>
    void TownGarrison(RmgObject town, int power, int race)
    {
        if (power < 100) return;
        int left = power;
        if (MonsterLevel == 0) left = (int)(power * 0.7f);
        else if (MonsterLevel == 2) left = (int)(power * 1.5f);
        bool stop = false;
        for (int tier = 1; tier < 8 && !stop; tier++)
        {
            int count = (Rng.Next(20) + 20) * (9 - tier) >> 1;
            var cands = _db.Creatures.Where(c => c.Index != 0 && c.Index != 0x59 && c.Index != 0x72 && c.Tier == tier && TownIndex(c.Town) == race).ToList();
            if (cands.Count == 0) return;
            var pick = cands[Rng.Next(cands.Count)];
            if (left < pick.Power * count) { count = pick.Power > 0 ? left / pick.Power : 0; stop = true; }
            town.Garrison.Add((pick.Id, count));
            left -= pick.Power * count;
        }
    }

    /// <summary>The town enum (TOWN_HEAVEN = 3 … TOWN_STRONGHOLD = 10), the same numbers as the race presets.</summary>
    static int TownIndex(string town) => town switch
    {
        "TOWN_HEAVEN" => 3, "TOWN_PRESERVE" => 4, "TOWN_ACADEMY" => 5, "TOWN_DUNGEON" => 6, "TOWN_NECROMANCY" => 7,
        "TOWN_INFERNO" => 8, "TOWN_FORTRESS" => 9, "TOWN_STRONGHOLD" => 10, _ => 0,
    };

    // ------------------------------------------------------------------ main objects (exe 0x00907150)

    void PlaceMainObjects(RmgResult r)
    {
        // exe 0x00907150: with the Graal option one position in the template's zone list is drawn up front
        // (Next(template zones)), otherwise one draw is spent. Zones are then visited in template list order.
        int grailZone = -1;
        if (_o.Grail) grailZone = Rng.Next(_t.Zones.Count); else Rng.Advance();
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z == null) continue;
            // The placers read the zone's +0x20 preset (abandoned mines 0x00920630, upgrades 0x0091f140, 0x00920ec0 …
            // 0x009221d0); the dwellings 0x0091e910 the +0x1c preset (the zone race). The mines 0x0091c4a0 read no
            // preset (TODO(exe): their table is not traced; ours: the zone race's Mines, the same objects in every preset).
            var race = _db.Race(z.Preset);
            var own = _db.Race(z.Race);
            PlaceMines(r, z, own);
            PlaceAbandonedMines(r, z, race, z.Def.AbandonedMines);
            // 0x0091c3a0 (hero): the player record's StartHero (PlayersInfo +8) is never set by our screen, so no hero is created.
            PlaceDwellings(r, z, own);
            if (_o.Grail) PlaceGrailAndObelisks(r, z, zi == grailZone);
            // Upgrade buildings: density x the +0xb0 factor, rounded to nearest (0x00907150 -> 0x0091f140).
            PlaceUpgrades(r, z, race.Upgrades,
                z.Tiles.Count * (int)MathF.Round(z.Def.UpgBuildingsDensity * Multiplier(_o.ExpLevel), MidpointRounding.ToEven) / 10000);
            // 0x00920370: Prisons; each with the far candidates (0x0091bb10).
            for (int i = 0; i < z.Def.Prisons; i++)
                PlaceRandom(r, z, "MapObjects/Prison.(AdvMapPrisonShared).xdb", FarCandidates(z), "prison");
            // 0x00920c50: LandCartographer cartographers; stops at the first empty candidate list.
            for (int i = 0; i < z.Def.LandCartographer; i++)
            {
                var c = FarCandidates(z);
                if (c.Count == 0) break;
                PlaceRandom(r, z, "MapObjects/Cartographer.(AdvMapCartographerShared).xdb", c, "cartographer");
            }
            PlaceShrines(r, z, z.Def.ShrinePoints);
            // Resource buildings: tiles x density / 10000; luck/morale: tiles x (density + 40) / 10000; treasuries
            // and shops: the template's points.
            SpendBudget(r, z, race.ResourceGivers, z.Tiles.Count * z.Def.ResourceBuildingsDensity / 10000, "resource building");
            SpendBudget(r, z, race.Treasuries, z.Def.TreasureBuildingPoints, "treasury building");
            SpendBudget(r, z, race.LuckMorale, z.Tiles.Count * (z.Def.LuckMoralBuildingsDensity + 40) / 10000, "luck/morale building");
            SpendBudget(r, z, race.Shops, z.Def.ShopPoints, "shop");
            // BuffPoints (+0x70) go to 0x00918b30, an empty function: the stock engine places no buff buildings.
            // 0x009219a0: tiles / 4000 + 1 Redwood Observatories, and in zones without a player one Den of
            // Thieves when rand(10) < 2 (the template's DenOfThieves / RedwoodObservatoryDensity are not read).
            for (int i = z.Tiles.Count / 4000; i >= 0; i--)
                PlaceBuilding(r, z, "MapObjects/Redwood_Observatory.(AdvMapBuildingShared).xdb", "redwood observatory");
            if (z.Player == 0 && Rng.Next(10) < 2)
                PlaceBuilding(r, z, "MapObjects/Den_Of_Thieves.(AdvMapBuildingShared).xdb", "den of thieves");
            // 0x00905e60 here for surface zones only (zone floor +0xf4 == 0); underground zones get theirs after
            // the statics (0x00906380, PlaceUndergroundTreasures).
            if (z.Floor == 0) PlaceZoneTreasures(r, z);
            ConnectObjectNodes(z);
        }
    }

    /// <summary>exe 0x00905e60: treasures (TreasureDensity x the +0xa8 factor), then chests (TreasureChestDensity x the +0xb0 factor), truncated.</summary>
    void PlaceZoneTreasures(RmgResult r, RmgZone z)
    {
        PlaceTreasures(r, z, (int)(z.Def.TreasureDensity * Multiplier(_o.ResourceLevel)), chests: false);
        PlaceTreasures(r, z, (int)(z.Def.TreasureChestDensity * Multiplier(_o.ExpLevel)), chests: true);
    }

    /// <summary>
    /// exe 0x00906380 ("additional objects set", after the statics 0x00906130): every template zone, in template
    /// list order, whose generated zone lies underground (+0xf4 != 0) gets its treasures and chests (0x00905e60).
    /// </summary>
    void PlaceUndergroundTreasures(RmgResult r)
    {
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z != null && z.Floor != 0) PlaceZoneTreasures(r, z);
        }
    }

    /// <summary>
    /// Graal option, per zone after the dwellings (exe 0x00907150): the drawn zone gets the Graal artifact
    /// (RMG/Params Grail) by 0x00921a50 — the far candidates (0x0091bb10), at most 100 attempts of a random candidate
    /// and quarter turn tested by 0x00919be0, created by 0x00914a60 and flagged 4; then every zone gets
    /// (zone tiles · k) / (W · H) + 2 "obelisks" (RMG/Params Obelisk, the stock data's Learning Stone) through
    /// 0x00921a10 → 0x009216f0, k = 10 / 18 / 26 for size index 0 / 1 / larger.
    /// The Graal tile joins the zone's node list +0x68 (0x00a68e00 with ECX = zone + 0x68) and gets flag 4.
    /// </summary>
    void PlaceGrailAndObelisks(RmgResult r, RmgZone z, bool grail)
    {
        if (grail && _db.Params.Grail is { } g)
        {
            var d = Catalog.Get(g);
            var pool = FarCandidates(z);
            bool placed = false;
            for (int tries = 0; tries < 100 && pool.Count > 0 && !placed; tries++)
            {
                int i = Rng.Next(pool.Count);
                var (x, y) = pool[i];
                float a = QuarterAngle(Rng.Next(4));
                if (FootprintFits(z, x, y, a, d))
                {
                    CreateObject(r, z, g, x, y, a, "graal");
                    z.RoadNodes.Add((x, y));
                    Floors[z.Floor].Flags[Floors[z.Floor].At(x, y)] = TileFlags.Active;
                    placed = true;
                }
                else pool.RemoveAt(i);
            }
            if (!placed) r.Warnings.Add($"Can't place building {g} at zone #{z.Id}, floor {z.Floor}");
        }
        if (_db.Params.Obelisk is not { } ob) return;
        int k = SizeIndex == 0 ? 10 : SizeIndex == 1 ? 18 : 26;
        int n = z.Tiles.Count * k / (W * H) + 1;
        for (int i = 0; i <= n; i++) PlaceBuilding(r, z, ob, "obelisk");
    }

    /// <summary>
    /// exe 0x009216f0: candidates of 0x0091bb10, then at most 100 attempts of a random candidate (rand(n)) and a
    /// quarter turn (rand(4)); a footprint failure drops the candidate.
    /// </summary>
    RmgObject? PlaceBuilding(RmgResult r, RmgZone z, string shared, string note)
    {
        if (!_db.Xdb.FileSystem.Exists(shared)) return null;
        var pool = FarCandidates(z);
        if (pool.Count == 0) return null;
        var d = Catalog.Get(shared);
        for (int tries = 0; tries < 100; tries++)
        {
            if (pool.Count == 0) break;
            int i = Rng.Next(pool.Count);
            var (x, y) = pool[i];
            float a = QuarterAngle(Rng.Next(4));
            if (FootprintFits(z, x, y, a, d))
            {
                var o = CreateObject(r, z, shared, x, y, a, note);
                Stamp(z, x, y, a, d, null);
                return o;
            }
            pool.RemoveAt(i);
        }
        r.Warnings.Add($"Can't place building {shared} at zone #{z.Id}, floor {z.Floor}");
        return null;
    }

    /// <summary>Resource piles (exe 0x00ff9e08, filled at 0x00dc8a50: Wood, Ore, Mercury, Crystal, Sulfur, Gems, Gold): the mine decorations by mine type.</summary>
    static readonly string[] ResourcePiles =
    {
        "MapObjects/Wood.(AdvMapTreasureShared).xdb", "MapObjects/Ore.(AdvMapTreasureShared).xdb",
        "MapObjects/Mercury.(AdvMapTreasureShared).xdb", "MapObjects/Crystal.(AdvMapTreasureShared).xdb",
        "MapObjects/Sulfur.(AdvMapTreasureShared).xdb", "MapObjects/Gems.(AdvMapTreasureShared).xdb",
        "MapObjects/Gold.(AdvMapTreasureShared).xdb",
    };

    (int X, int Y) _lastMineGuard;   // 0x0091c4a0's guard tile, kept across mines like the exe's stack slot

    /// <summary>
    /// exe 0x0091c4a0. Candidate lists over the floor (x major) with the zone's tiles of border distance &gt; 1: with a
    /// placed town, wood/ore (types 0, 1) within (Mine1LevelMinRadius, Mine1LevelMaxRadius) of it and the others within
    /// the Mine2 radii; without, every such tile. Per mine: 0x009191a0(4), candidates with distance &gt; 2M/5 (M of
    /// 0x00919710 over the list), the placer loop; a placed mine is stamped with the mine list +0x11c. Its guard: from
    /// k = chop(angle x 4/π + 0.5) (type 2: angle + π), the first of the 4 orthogonal neighbours (k &amp; 3 …) of the
    /// mine's last visit tile whose flags carry a road bit or are 0 gets the guard (BasicLevelGuardPower x Mine1 /
    /// Mine2 / gold level, turned by the angle), flag 4, and joins the guard points +0x98. Then up to two decorations:
    /// neighbours k+2 … k+9 (&amp; 7) of the 8-table with such flags, closer than 2 to the guard tile, Float(0,1) &lt; 0.8,
    /// get the type's resource pile (no footprint test) and flag 2.
    /// </summary>
    void PlaceMines(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        var f = Floors[z.Floor];
        var p = _db.Params;
        var near = new List<(int, int)>();
        var far = new List<(int, int)>();
        foreach (var (x, y) in z.Tiles)
        {
            if (f.BorderDist[f.At(x, y)] <= 1) continue;
            if (!z.TownPlaced || z.TownPos is not { } tp) { near.Add((x, y)); far.Add((x, y)); continue; }
            float dx = tp.X - x, dy = tp.Y - y;
            float d = MathF.Sqrt(dy * dy + dx * dx);
            if (d < p.Mine2LevelMaxRadius && d > p.Mine2LevelMinRadius) far.Add((x, y));
            if (d < p.Mine1LevelMaxRadius && d > p.Mine1LevelMinRadius) near.Add((x, y));
        }
        for (int type = 0; type < 7 && type < race.Mines.Count; type++)
        {
            for (int n = 0; n < z.Def.Mines[type]; n++)
            {
                var cands = SpreadCandidates(z, type < 2 ? near : far, 2, 5);
                var d = Catalog.Get(race.Mines[type]);
                RmgObject? mine = null;
                float angle = 0;
                int mx = 0, my = 0;
                while (cands.Count > 0)
                {
                    int i = Rng.Next(cands.Count);
                    (mx, my) = cands[i];
                    angle = QuarterAngle(Rng.Next(4));
                    if (FootprintFits(z, mx, my, angle, d)) { mine = CreateObject(r, z, race.Mines[type], mx, my, angle, "mine"); break; }
                    cands.RemoveAt(i);
                }
                if (mine == null)
                {
                    if (type >= 2) r.Warnings.Add($"cant place mine {race.Mines[type]} at zone {z.Id}");
                    continue;
                }
                Stamp(z, mx, my, angle, d, z.MinePoints);
                var last = z.MinePoints[^1];
                int px = (int)last.X, py = (int)last.Y;
                if (type == 2) angle += 3.1415927f;                                       // 0x00e0d614
                int k = (int)(angle * 1.2732395f + 0.5f);                                  // 0x00e57dd0, 0x00e0ab70
                int level = type < 2 ? p.Mine1LevelGuardLevel : type == 6 ? p.MineGoldGuardLevel : p.Mine2LevelGuardLevel;
                for (int u = k; u < k + 4; u++)
                {
                    var (dx, dy) = Ortho4[u & 3];
                    int gx = px + dx, gy = py + dy;
                    if (!f.In(gx, gy) || !GuardTileFree(f.Flags[f.At(gx, gy)])) continue;
                    PlaceGuard(r, z, gx, gy, GuardPower(level), "mine guard", angle);
                    f.Flags[f.At(gx, gy)] = TileFlags.Active;
                    z.GuardPoints.Add((gx, gy));
                    _lastMineGuard = (gx, gy);
                    break;
                }
                int placed = 0;
                for (int u = k + 2; u < k + 10 && placed <= 1; u++)
                {
                    var (dx, dy) = Around8[u & 7];
                    int ex = px + dx, ey = py + dy;
                    if (!f.In(ex, ey) || !GuardTileFree(f.Flags[f.At(ex, ey)])) continue;
                    float gdx = ex - _lastMineGuard.X, gdy = ey - _lastMineGuard.Y;
                    if (!(MathF.Sqrt(gdy * gdy + gdx * gdx) < 2f)) continue;                 // 0x00e15a94
                    if (!(Rng.Float(0, 1) < 0.8f)) continue;                                    // 0x00e1857c
                    CreateObject(r, z, ResourcePiles[type], ex, ey, angle, "mine decoration");
                    f.Flags[f.At(ex, ey)] = TileFlags.Blocked;
                    placed++;
                }
            }
        }
    }

    /// <summary>
    /// exe 0x00920630: candidates are the zone's tiles inside 0x009157b0 (1 ≤ x, y &lt; size − 1) with border distance
    /// &gt; 1 and, with a placed town, within (Mine3LevelMinRadius, Mine3LevelMaxRadius) of it; per mine 0x009191a0(4)
    /// and distance &gt; 4M/5, the placer loop, 0x009151c0 creates it and 0x0091a420 stamps it with the mine list +0x11c.
    /// No guard (the abandoned mine carries its own GuardsVariants).
    /// </summary>
    void PlaceAbandonedMines(RmgResult r, RmgZone z, RmgRacePreset race, int count)
    {
        if (race.AbandonedMine == null) return;
        var f = Floors[z.Floor];
        var p = _db.Params;
        var list = new List<(int, int)>();
        foreach (var (x, y) in z.Tiles)
        {
            if (!(y >= 1 && y < H - 1 && x >= 1 && x < W - 1) || f.BorderDist[f.At(x, y)] <= 1) continue;
            if (z.TownPlaced && z.TownPos is { } tp)
            {
                float dx = tp.X - x, dy = tp.Y - y;
                float d = MathF.Sqrt(dy * dy + dx * dx);
                if (p.Mine3LevelMaxRadius <= d || d <= p.Mine3LevelMinRadius) continue;
            }
            list.Add((x, y));
        }
        for (int i = 0; i < count; i++)
            PlaceRandom(r, z, race.AbandonedMine, SpreadCandidates(z, list, 4, 5), "abandoned mine", z.MinePoints);
    }

    /// <summary>
    /// exe 0x0091e910: per tier and count, 0x009191a0(4) and the zone tiles with distance &gt; 2M/3; the placer loop;
    /// an exhausted list logs "Can't place dwelling" and ends all dwellings of the zone. No guard.
    /// The shared is the +0x1c preset's Dwellings[tier] (list +0x28, 0x004a6430), with RandomTowns (generator +0xa5) the
    /// tier's entry of the RandomDwelling table 0x00ff9b48 (init 0x00dc88b0: RandomDwelling1..7).
    /// </summary>
    void PlaceDwellings(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        for (int tier = 0; tier < z.Def.Dwellings.Length; tier++)
            for (int n = 0; n < z.Def.Dwellings[tier]; n++)
            {
                string shared;
                if (_o.RandomTowns) shared = $"MapObjects/Random/RandomDwelling{tier + 1}.xdb";
                else if (tier >= race.Dwellings.Count) return;
                else shared = race.Dwellings[tier];
                if (PlaceRandom(r, z, shared, SpreadCandidates(z, z.Tiles, 2, 3), "dwelling") == null) return;
            }
    }

    /// <summary>
    /// exe 0x0091f140: points = tiles x density / 10000; while the first entry fits, n = leading entries that fit,
    /// rand(n); 0x009191a0(4) and the zone tiles with distance &gt; 2M/3; the placer loop (an exhausted list ends the
    /// zone's upgrades); 0x0091a420, then 0x00ba79f0 with the entry's GuardStrenght.
    /// </summary>
    void PlaceUpgrades(RmgResult r, RmgZone z, List<RmgValuedObject> pool, int budget)
    {
        if (pool.Count == 0) return;
        int spent = 0;
        while (pool[0].Value + spent <= budget)
        {
            int n = 0;
            while (n < pool.Count && pool[n].Value + spent <= budget) n++;
            var pick = pool[Rng.Next(n)];
            var o = PlaceRandom(r, z, pick.Shared, SpreadCandidates(z, z.Tiles, 2, 3), "upgrade building");
            if (o == null) return;
            UpgradeGuard(r, z, o, GuardPower(pick.GuardStrength));
            spent += pick.Value;
        }
    }

    /// <summary>
    /// exe 0x00ba79f0 (pos, angle, activeTiles, level, guard points): from the first rotated active tile, k = chop(angle
    /// x 4/π + 0.5); the first of the 8 neighbours (0x00f76b68, k … k+7 &amp; 7) whose flags carry a road bit or are 0 gets
    /// the guard (BasicLevelGuardPower x level, turned by the angle), flag 4, and joins the guard points +0x98.
    /// </summary>
    void UpgradeGuard(RmgResult r, RmgZone z, RmgObject o, int power)
    {
        var f = Floors[z.Floor];
        float angle = o.Angle ?? 0;
        var (ax, ay) = VisitTile(o.Def, o.X, o.Y, angle);
        int k = (int)(angle * 1.2732395f + 0.5f);
        for (int u = k; u < k + 8; u++)
        {
            var (dx, dy) = Around8[u & 7];
            int gx = ax + dx, gy = ay + dy;
            if (!f.In(gx, gy) || !GuardTileFree(f.Flags[f.At(gx, gy)])) continue;
            PlaceGuard(r, z, gx, gy, power, "upgrade building guard", angle);
            f.Flags[f.At(gx, gy)] = TileFlags.Active;
            z.GuardPoints.Add((gx, gy));
            break;
        }
    }

    /// <summary>
    /// The budget loop shared by 0x00920ec0 (resource buildings), 0x00921d70 (luck/morale), 0x00921360 (treasuries)
    /// and 0x009221d0 (shops): while the pool's first entry still fits, n = the number of leading entries (preset order)
    /// that fit, pick rand(n), stop if it does not fit or 0x0091bb10 finds no candidates, place it (an exhausted list
    /// ends the loop) and add its value.
    /// </summary>
    void SpendBudget(RmgResult r, RmgZone z, List<RmgValuedObject> pool, int budget, string note)
    {
        if (pool.Count == 0) return;
        int spent = 0;
        while (pool[0].Value + spent <= budget)
        {
            int n = 0;
            while (n < pool.Count && pool[n].Value + spent <= budget) n++;
            var pick = pool[Rng.Next(n)];
            if (pick.Value + spent > budget) break;
            var cands = FarCandidates(z);
            if (cands.Count == 0) break;
            if (PlaceRandom(r, z, pick.Shared, cands, note) == null) break;
            spent += pick.Value;
        }
    }

    /// <summary>The shrine table (exe 0x00ffa068, filled at 0x00dc8bd0) and its point costs (0x00e57d74).</summary>
    static readonly (string Shared, int Cost)[] ShrineTable =
    {
        ("MapObjects/Shrine_Of_Magic_1.(AdvMapShrineShared).xdb", 6),
        ("MapObjects/Shrine_Of_Magic_2.(AdvMapShrineShared).xdb", 10),
        ("MapObjects/Shrine_Of_Magic_3.(AdvMapShrineShared).xdb", 12),
    };

    /// <summary>
    /// exe 0x00920000: ShrinePoints buy Shrines of Magic 1/2/3 (6/10/12 points) from the fixed table, not the race
    /// preset's NewShrines. Needs more than 5 points; n = leading costs that fit, rand(n); stops without candidates;
    /// continues while 6 more points fit.
    /// </summary>
    void PlaceShrines(RmgResult r, RmgZone z, int points)
    {
        if (points <= 5) return;
        int spent = 0;
        do
        {
            int n = 0;
            while (n < ShrineTable.Length && ShrineTable[n].Cost + spent <= points) n++;
            var pick = ShrineTable[Rng.Next(n)];
            var cands = FarCandidates(z);
            if (cands.Count == 0) break;
            if (PlaceRandom(r, z, pick.Shared, cands, "shrine") == null) break;
            spent += pick.Cost;
        } while (spent + 6 <= points);
    }

    /// <summary>The loose treasure table (exe 0x00ff9ee8, 32-byte records filled at 0x00dc8ad0): rand(9) picks one
    /// for a treasure, index 1 (Chest) is used for chests.</summary>
    static readonly string[] TreasureTable =
    {
        "MapObjects/Campfire.(AdvMapTreasureShared).xdb", "MapObjects/Chest.(AdvMapTreasureShared).xdb",
        "MapObjects/Crystal.(AdvMapTreasureShared).xdb", "MapObjects/Gems.(AdvMapTreasureShared).xdb",
        "MapObjects/Gold.(AdvMapTreasureShared).xdb", "MapObjects/Mercury.(AdvMapTreasureShared).xdb",
        "MapObjects/Ore.(AdvMapTreasureShared).xdb", "MapObjects/Sulfur.(AdvMapTreasureShared).xdb",
        "MapObjects/Wood.(AdvMapTreasureShared).xdb",
    };

    /// <summary>
    /// exe 0x0091f8a0: count = tiles x density / 10000; per treasure 0x009191a0(4), M of 0x00919710 over the zone's
    /// tiles with border distance &gt; 0, candidates among them with distance &gt; 2M/3 and flags != 2; the type is drawn
    /// next (rand(9), chests fixed); the placer loop. An exhausted list logs "Can't place treasure" and ends the zone's treasures.
    /// </summary>
    void PlaceTreasures(RmgResult r, RmgZone z, int density, bool chests)
    {
        var f = Floors[z.Floor];
        int count = z.Tiles.Count * density / 10000;
        var inner = z.Tiles.Where(t => f.BorderDist[f.At(t.X, t.Y)] > 0).ToList();
        for (int i = 0; i < count; i++)
        {
            DistanceField(z, 4);
            int max = MaxDist(z, inner);
            var cands = inner.Where(t => { int k = f.At(t.X, t.Y); return f.Dist[k] > max * 2 / 3 && f.BorderDist[k] > 0 && f.Flags[k] != TileFlags.Blocked; }).ToList();
            string shared = TreasureTable[chests ? 1 : Rng.Next(TreasureTable.Length)];
            if (PlaceRandom(r, z, shared, cands, chests ? "chest" : "treasure") == null) break;
        }
    }

    // ------------------------------------------------------------------ finish (exe 0x00904ad0)

    /// <summary>
    /// Our check, not the exe's: every object with a visit tile should be reachable from its zone's town visit tile
    /// (or, without a town, the start of its road network, else its free tile nearest the zone centre) over tiles that no object's footprint blocks
    /// (monsters are fought through, so they block nothing). Counts the ones that are not; the water option's water
    /// treasures lie on the sea and are not counted.
    /// </summary>
    void Finish(RmgResult r)
    {
        foreach (var f in Floors)
        {
            var blocked = new bool[W * H];
            foreach (var o in r.Objects.Where(o => o.Floor == f.Index && o.Def.Tag != "AdvMapMonsterShared"))
            {
                int q = ExeQuarter(o.Rot);
                var act = new HashSet<(int, int)>(o.Def.Active.Select(a => { var t = Rotate(a, q); return (o.X + t.X, o.Y + t.Y); }));
                foreach (var b in o.Def.Blocked)
                {
                    var t = Rotate(b, q);
                    int x = o.X + t.X, y = o.Y + t.Y;
                    if (f.In(x, y) && !act.Contains((x, y))) blocked[f.At(x, y)] = true;
                }
            }
            foreach (var z in f.Zones.Values)
            {
                (int X, int Y)? hub = null;
                var town = r.Objects.FirstOrDefault(o => o.Note == "town" && o.Zone == z.Id && o.Floor == f.Index);
                if (town != null && r.Access.TryGetValue(town, out var ta)) hub = ta;
                else if (z.Network.Count > 0) hub = ((int)z.Network[0].X, (int)z.Network[0].Y);   // the road network's start (first connection point)
                hub ??= z.Tiles.Where(t => !blocked[f.At(t.X, t.Y)])
                    .OrderBy(t => (t.X - z.CenterX) * (t.X - z.CenterX) + (t.Y - z.CenterY) * (t.Y - z.CenterY))
                    .Select(t => ((int, int)?)t).FirstOrDefault();
                if (hub == null) continue;
                var seen = new HashSet<(int, int)> { hub.Value };
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue(hub.Value);
                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    foreach (var (dx, dy) in Around8)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (!f.In(nx, ny) || seen.Contains((nx, ny))) continue;
                        int i = f.At(nx, ny);
                        if (f.Zone[i] != z.Id || blocked[i]) continue;
                        seen.Add((nx, ny));
                        queue.Enqueue((nx, ny));
                    }
                }
                foreach (var kv in r.Access.Where(kv => kv.Key.Zone == z.Id && kv.Key.Floor == f.Index && kv.Key.Def.Tag != "AdvMapStaticShared"
                                                      && kv.Key.Note != "water treasure"))   // on the sea (0x00bad280): reached by boat
                    if (!seen.Contains(kv.Value))
                    {
                        r.Unreachable++;
                        r.Warnings.Add($"unreachable {kv.Key.Note} {kv.Key.Shared} at ({kv.Key.X},{kv.Key.Y}) floor {f.Index}, visit tile {kv.Value}, zone {z.Id}");
                    }
            }
        }
    }
}
