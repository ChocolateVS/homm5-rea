using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Ships and sea travel. Sources:
//  * Ships are AdvMapBuilding objects of type BUILDING_SHIP_GALEON (MapObjects/Ship_Galley, Ship_Galeon,
//    Ship_Junk). H5_Game.exe picks the model by race (FUN_0045a0e0 → FUN_008dd470): Haven, Preserve,
//    Academy, Fortress → Galleon; Dungeon, Necropolis, Inferno, Stronghold → Junk; otherwise Galley.
//  * Shipyards (map AdvMapShipyard and town TB_SHIPYARD) build a ship on their slipway (<ShipTile>, an
//    offset from the object's position, not rotated: 57/64 stock slipways land on water that way).
//    Cost 1000 gold + 10 wood (Text/Interface/Shipyard/description_text). States from the stock texts:
//    deserted (the slipway is not water) and blocked (a ship or hero is on the slipway).
//  * Boarding and landing (fan manual p.123, p.301): take the rest of the day's movement; with Snatch
//    boarding costs nothing and landing a standard step (100 or 141).
//  * Vessel of Shalassa / Summon Boat (exe FUN_008656e0 case 0x31, FUN_008652f0, FUN_00864f00,
//    FUN_00865230, FUN_00864920): the hero must be on land with a free water tile next to him; the
//    nearest empty ship on the same floor (squared distance, later ships win ties) is moved there.
//  * Whirlpools (exe FUN_0042e0f0 / FUN_0045abf0): lose (n+1)/2 of the lowest stack, (n+1)/4 with
//    HERO_SKILL_FORTUNATE_ADVENTURER (skill id 0x21); the lowest stack compares creature tier then
//    upgrade, the first slot wins ties; a lone stack of one creature is spared.

public sealed record BuildShipCmd(int Object, int HeroId = -1) : AdvCommand;

public sealed record BoatCreated(int BoatId, Tile Pos, int Model, int ByObject) : AdvEvent;
public sealed record HeroEmbarked(int HeroId, int BoatId, Tile From, Tile To, int MovePointsLeft) : AdvEvent;
public sealed record HeroDisembarked(int HeroId, int BoatId, Tile From, Tile To, int MovePointsLeft) : AdvEvent;
public sealed record BoatMoved(int BoatId, Tile From, Tile To) : AdvEvent;

public sealed partial class Adventure
{
    public const string ShipBuildingType = "BUILDING_SHIP_GALEON";
    public const int ShipCostGold = 1000, ShipCostWood = 10;
    const string SnatchPerk = "HERO_SKILL_SNATCH";
    const string FortunateAdventurerPerk = "HERO_SKILL_FORTUNATE_ADVENTURER";

    public BoatState? BoatAt(Tile t) => S.Boats.Find(b => b.Pos == t);
    public BoatState? BoatOf(HeroState h) => h.BoatId >= 0 ? S.Boats.Find(b => b.Id == h.BoatId) : null;

    public bool IsWaterTile(Tile t) => Map.Floor(t.Floor) is { } f && f.InBounds(t.X, t.Y) && f.IsWater(t.X, t.Y);

    /// <summary>Ship model index for a race (exe FUN_0045a0e0 over the town enum).</summary>
    public static int ShipModelFor(string? town) => town switch
    {
        "TOWN_HEAVEN" or "TOWN_PRESERVE" or "TOWN_ACADEMY" or "TOWN_FORTRESS" => 1,
        "TOWN_DUNGEON" or "TOWN_NECROMANCY" or "TOWN_INFERNO" or "TOWN_STRONGHOLD" => 2,
        _ => 0,
    };

    static int ShipModelFromShared(string sharedPath)
    {
        for (int i = 0; i < BoatState.ModelShared.Length; i++)
            if (sharedPath.EndsWith(BoatState.ModelShared[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 1;
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Editor-placed ships become boats (and stop being visitable objects); heroes the editor put on
    /// water board the ship under them, or get one of their race's model.
    /// </summary>
    void SetupBoats()
    {
        foreach (var o in Map.Objects.Where(o => o.Shared.Type == ShipBuildingType))
        {
            S.Obj(o.Index).Removed = true;
            if (BoatAt(o.Pos) != null) continue;
            S.Boats.Add(new BoatState { Id = S.NextBoatId++, Pos = o.Pos, Model = ShipModelFromShared(o.SharedPath), MapObject = o.Index, Rot = o.Rot });
        }
        foreach (var h in S.Heroes.Where(h => h.Alive && h.InTown < 0 && IsWaterTile(h.Pos)))
        {
            var b = BoatAt(h.Pos);
            if (b == null)
            {
                b = new BoatState { Id = S.NextBoatId++, Pos = h.Pos, Model = ShipModelFor(h.Town) };
                S.Boats.Add(b);
            }
            Board(h, b);
        }
    }

    void Board(HeroState h, BoatState b)
    {
        b.HeroId = h.Id;
        h.BoatId = b.Id;
        h.InBoat = true;
        h.MaxMovePoints = ComputeMaxMovePoints(h);
    }

    void LeaveBoat(HeroState h, BoatState b)
    {
        b.HeroId = -1;
        h.BoatId = -1;
        h.InBoat = false;
        h.MaxMovePoints = ComputeMaxMovePoints(h);
    }

    /// <summary>Keeps a sailing hero's ship under him after any position change (moves, teleports, spells).</summary>
    void SyncBoat(HeroState h)
    {
        if (!h.InBoat || BoatOf(h) is not { } b || b.Pos == h.Pos) return;
        var from = b.Pos;
        b.Pos = h.Pos;
        b.Rot = MathF.Atan2(h.Pos.Y - from.Y, h.Pos.X - from.X);
    }

    // ------------------------------------------------------------------ moving between land and sea

    /// <summary>
    /// Whether a hero may end a move on <paramref name="t"/> when it lies across the shore: land
    /// heroes may only step onto water to board an empty ship or to attack a sailing hero; sailing
    /// heroes may land on free land (or attack/visit what stands there).
    /// </summary>
    bool CanCrossShoreTo(HeroState h, Tile t)
    {
        if (!h.InBoat)
            return BoatAt(t) is { HeroId: -1 } || (HeroAt(t, h.Id) is { InBoat: true });
        return true;
    }

    /// <summary>Handles the final step of a move across the shore. Returns true if the step was consumed.</summary>
    bool TryShoreStep(HeroState h, Tile next, List<Tile> walked, int stepCost)
    {
        bool water = IsWaterTile(next);
        if (!h.InBoat && water && BoatAt(next) is { HeroId: -1 } boat)
        {
            var from = h.Pos;
            MoveStep(h, walked);
            // Boarding takes the rest of the day (p.301); with Snatch it costs nothing (p.123).
            if (!h.HasPerk(SnatchPerk)) h.MovePoints = 0;
            h.Pos = next;
            h.InTown = -1;
            Board(h, boat);
            Emit(new HeroEmbarked(h.Id, boat.Id, from, next, h.MovePoints));
            RevealAround(h);
            return true;
        }
        if (h.InBoat && !water && HeroAt(next, h.Id) == null && MonsterAt(next) == null)
        {
            var from = h.Pos;
            var b = BoatOf(h);
            MoveStep(h, walked);
            // Landing takes the rest of the day; with Snatch a standard step (100 or 141).
            h.MovePoints = h.HasPerk(SnatchPerk) ? Math.Max(0, h.MovePoints - stepCost) : 0;
            h.Pos = next;
            if (b != null) LeaveBoat(h, b);
            Emit(new HeroDisembarked(h.Id, b?.Id ?? -1, from, next, h.MovePoints));
            RevealAround(h);
            VisitTile(h, next);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ shipyards

    /// <summary>The slipway of a shipyard or town (object position + &lt;ShipTile&gt;), if it has one.</summary>
    public Tile? SlipwayOf(int objectIndex)
    {
        var o = O(objectIndex);
        if (o.ShipTile is not { } st) return null;
        if (o.Kind == ObjectKind.Town && st == (0, 0)) return null;
        return o.Pos.Offset(st.X, st.Y);
    }

    /// <summary>"ok", "deserted" (no water at the slipway) or "blocked" (a ship or hero on it).</summary>
    public string ShipyardStatus(int objectIndex)
    {
        if (SlipwayOf(objectIndex) is not { } t || !IsWaterTile(t)) return "deserted";
        var f = Map.Floor(t.Floor)!;
        if (f.IsBlocked(t.X, t.Y) || BoatAt(t) != null || HeroAt(t) != null || ObjectsAt(t).Any()) return "blocked";
        return "ok";
    }

    /// <summary>
    /// A map shipyard visit opens the shipyard box whatever the slipway's state (CBuildShipDialogLogEntry →
    /// exe 006a7840, which itself picks the deserted / blocked description and the buttons).
    /// </summary>
    void VisitShipyard(HeroState h, MapObject o)
    {
        S.Choice = new PendingChoice { Kind = "shipyard", Hero = h.Id, Object = o.Index, Gold = ShipCostGold };
        Emit(new ChoiceOffered("shipyard", h.Id, o.Index, ShipCostGold, 0));
    }

    string? DoBuildShip(BuildShipCmd c)
    {
        var o = c.Object >= 0 && c.Object < Map.Objects.Count ? O(c.Object) : null;
        if (o == null) return "no such object";
        if (o.Kind == ObjectKind.Town)
        {
            var town = S.Town(o.Index);
            if (town == null || town.Owner != Current.Index) return "not your town";
            if (town.Level("TB_SHIPYARD") <= 0) return "the town has no shipyard";
        }
        else if (o.Kind == ObjectKind.Shipyard)
        {
            if (S.Obj(o.Index).Owner != Current.Index) return "not your shipyard";
        }
        else return "not a shipyard";
        return BuildShipAt(o, Current);
    }

    string? BuildShipAt(MapObject o, PlayerState p)
    {
        var status = ShipyardStatus(o.Index);
        if (status != "ok") return status == "blocked" ? "a boat is blocking the slipway" : "the shipyard is deserted";
        if (p.Purse[Purse.Gold] < ShipCostGold || p.Purse[Purse.Wood] < ShipCostWood) return "not enough resources";
        var t = SlipwayOf(o.Index)!.Value;
        var delta = new int[7];
        delta[Purse.Gold] = -ShipCostGold;
        delta[Purse.Wood] = -ShipCostWood;
        p.Purse[Purse.Gold] -= ShipCostGold;
        p.Purse[Purse.Wood] -= ShipCostWood;
        Emit(new ResourcesChanged(p.Index, delta, p.Purse.R.ToArray()));
        string? race = o.Kind == ObjectKind.Town ? S.Town(o.Index)?.Type : p.Race;
        var b = new BoatState { Id = S.NextBoatId++, Pos = t, Model = ShipModelFor(race), Rot = o.Rot };
        S.Boats.Add(b);
        Emit(new BoatCreated(b.Id, t, b.Model, o.Index));
        return null;
    }

    // ------------------------------------------------------------------ Vessel of Shalassa

    /// <summary>
    /// The water tile a summoned ship would come to: the first free water tile next to the hero
    /// (exe FUN_00864f00 walks the neighbourhood and takes the first free water tile).
    /// </summary>
    public Tile? SummonBoatTarget(HeroState h)
    {
        if (h.InBoat) return null;
        foreach (var (dx, dy) in Dirs)
        {
            var n = h.Pos.Offset(dx, dy);
            var f = Map.Floor(n.Floor);
            if (f == null || !f.InBounds(n.X, n.Y) || !f.IsWater(n.X, n.Y) || f.IsBlocked(n.X, n.Y)) continue;
            if (BoatAt(n) != null || HeroAt(n) != null || ObjectsAt(n).Any()) continue;
            return n;
        }
        return null;
    }

    /// <summary>The nearest empty ship on the hero's floor (squared distance; later ships win ties, as in the exe).</summary>
    public BoatState? NearestEmptyBoat(HeroState h)
    {
        BoatState? best = null;
        long bestD = 0xffffff;
        foreach (var b in S.Boats)
        {
            if (b.HeroId >= 0 || b.Pos.Floor != h.Pos.Floor) continue;
            long dx = b.Pos.X - h.Pos.X, dy = b.Pos.Y - h.Pos.Y, d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = b; }
        }
        return best;
    }

    string? CastSummonBoat(HeroState h, int mana)
    {
        if (h.Mana < mana) return "not enough mana";
        if (h.InBoat) return "cannot cast at sea";
        if (SummonBoatTarget(h) is not { } to) return "the hero must stand on a shore";
        h.Mana -= mana;
        var b = NearestEmptyBoat(h);
        if (b == null)
        {
            Emit(new AdventureSpellCast(h.Id, SpellSummonBoat, h.Pos, h.Pos));
            Emit(new Message("ADVENTURE_SUMMON_BOAT_FAILED", "Can't summon boat"));
            return null;
        }
        var from = b.Pos;
        b.Pos = to;
        Emit(new AdventureSpellCast(h.Id, SpellSummonBoat, from, to));
        Emit(new BoatMoved(b.Id, from, to));
        return null;
    }

    // ------------------------------------------------------------------ whirlpools

    /// <summary>
    /// The stack a whirlpool drowns part of, and how many: (n+1)/2 of the lowest stack, or (n+1)/4 with
    /// Fortunate Adventurer. Lowest = smallest tier, then base before upgrade, first slot on ties.
    /// Returns null when nothing is lost (no army, or a single stack of one creature).
    /// </summary>
    public (int Slot, int Lost)? WhirlpoolLoss(HeroState h)
    {
        int slot = -1, bestTier = 0, bestUpg = 0, stacks = 0;
        for (int i = 0; i < h.Army.Length; i++)
        {
            if (h.Army[i] is not { Count: > 0 } s) continue;
            stacks++;
            int tier = Core.Creatures.TryGetValue(s.Creature, out var cd) ? cd.Tier : 0;
            int upg = cd is { Upgrade: true } ? 1 : 0;
            if (slot < 0 || tier < bestTier || (tier == bestTier && upg < bestUpg)) { slot = i; bestTier = tier; bestUpg = upg; }
        }
        if (slot < 0) return null;
        if (stacks == 1 && h.Army[slot]!.Count == 1) return null;
        int lost = (h.Army[slot]!.Count + 1) / (h.HasPerk(FortunateAdventurerPerk) ? 4 : 2);
        return lost > 0 ? (slot, lost) : null;
    }

    void ApplyWhirlpoolLoss(HeroState h)
    {
        if (WhirlpoolLoss(h) is not { } loss)
        {
            Emit(new Message("ADVENTURE_WHIRLPOOL_SAFE_TRAVEL", "The whirlpool carries your ship away without loss."));
            return;
        }
        var s = h.Army[loss.Slot]!;
        s.Count -= Math.Min(loss.Lost, s.Count);
        if (s.Count <= 0) h.Army[loss.Slot] = null;
        Emit(new ArmyChanged(h.Id, -1));
        Emit(new Message("ADVENTURE_WHIRLPOOL_UNSAFE_TRAVEL", $"The whirlpool swallows {loss.Lost} of your troops."));
    }
}

public sealed partial class Adventure
{
    /// <summary>
    /// Battles fought at sea (a sailing attacker against something on water) use the water terrain's
    /// arenas in CombatArenaTypes (terrain 6: Scenes/CombatArenas/Boat_Arena). Other battles keep the
    /// combat engine's default arena choice. TODO(exe): shore battles (boat vs land) and the land
    /// terrain → arena mapping are not traced.
    /// </summary>
    string? SeaBattleArena(HeroState attacker, PendingBattle pb, ulong seed)
    {
        if (!attacker.InBoat || !IsWaterTile(pb.Where)) return null;
        return H5.Combat.BattleSetup.PickArena(Core, new H5.Combat.Rng(seed ^ 0x5EA5EA5EUL), H5.Core.Data.CombatArenaTable.TerrainWater);
    }
}

public sealed partial class Adventure
{
    /// <summary>
    /// Shipwrecked Footman (data MinResource 1) / Peasant (0), rolled at map start as the exe's treasure
    /// roller does (FUN_00a82720, case TREASURE_SHIPWRECK) with r = 1..99:
    /// Footman: r&lt;20 → 2000 gold; 20–49 minor, 50–94 major, 95–99 relic artifact.
    /// Peasant: r&lt;30 → 500 gold; 30–59 → 1000 gold; 60–94 minor, 95–99 relic artifact.
    /// </summary>
    void RollShipwreck(ObjState os, bool footman)
    {
        int r = S.Rng.Range(1, 99);
        string? cls;
        if (footman)
        {
            os.Amount = r < 20 ? 2000 : 0;
            cls = r < 20 ? null : r < 50 ? "ARTF_CLASS_MINOR" : r < 95 ? "ARTF_CLASS_MAJOR" : "ARTF_CLASS_RELIC";
        }
        else
        {
            os.Amount = r < 30 ? 500 : r < 60 ? 1000 : 0;
            cls = r < 60 ? null : r < 95 ? "ARTF_CLASS_MINOR" : "ARTF_CLASS_RELIC";
        }
        if (cls != null) os.ArtifactId = RandomArtifact(cls);
    }
}
