using H5.Adventure.Map;

namespace H5.Adventure.Engine;

/// <summary>
/// Random towns (AdvMapTownShared Type TOWN_RANDOM_TYPE, MapObjects/RandomTown.xdb) and random dwellings
/// (AdvMapDwellingShared RandomType DWELLING_TYPE_RANDOM_1L..7L / _MILITARY_POST, MapObjects/Random/RandomDwelling1..7)
/// become concrete when the world creates the map's objects (H5_Game.exe 0041d360): towns through 0041b5e0 → 0041b450,
/// dwellings through 0041b9f0.
/// </summary>
public sealed partial class Adventure
{
    const string RandomTownType = "TOWN_RANDOM_TYPE";

    /// <summary>
    /// The exe's draw: FUN_008e7840(0, 8 − 1) on the object's own stream (world vf 0x124 + 008e7860 over the object id),
    /// mapped by 00af10f0 to race idx + 3 (TOWN_HEAVEN .. TOWN_STRONGHOLD, the order of <see cref="PlayableRaces"/>).
    /// TODO(exe): the per-object stream is not reproduced; the game RNG draws in map order instead.
    /// </summary>
    string RandomRace() => PlayableRaces[S.Rng.Next(PlayableRaces.Length)];

    /// <summary>Resolves every random town and dwelling in map order (as 0041d360 creates them) into <see cref="AdvState.RandomObjects"/>.</summary>
    void ResolveRandomObjects()
    {
        foreach (var o in Map.Objects)
            if (o.Kind is ObjectKind.Town or ObjectKind.Dwelling)
            {
                o.PlacedShared ??= o.Shared;
                o.PlacedSharedPath ??= o.SharedPath;
                if (o.Kind == ObjectKind.Town) o.PlacedTownType ??= o.TownType;
            }
        foreach (var o in Map.Objects)
        {
            if (o.Kind == ObjectKind.Town && o.PlacedTownType == RandomTownType) TownRace(o, 0);
            else if (o.Kind == ObjectKind.Dwelling && RandomDwellingType(o) > 0) ResolveDwelling(o);
        }
        ApplyRandomObjects();
    }

    /// <summary>Puts the resolved types and shareds on the map objects (also for an engine built over a saved state).</summary>
    void ApplyRandomObjects()
    {
        foreach (var (index, value) in S.RandomObjects)
        {
            if (index < 0 || index >= Map.Objects.Count) continue;
            var o = Map.Objects[index];
            if (o.Kind == ObjectKind.Town) { o.PlacedTownType ??= o.TownType; o.TownType = value; continue; }
            if (o.Kind != ObjectKind.Dwelling) continue;
            o.PlacedShared ??= o.Shared;
            o.PlacedSharedPath ??= o.SharedPath;
            int bar = value.LastIndexOf('|');
            var path = bar >= 0 ? value[..bar] : value;
            int tier = bar >= 0 && int.TryParse(value[(bar + 1)..], out var t) ? t : 0;
            if (o.SharedPath == path && o.DwellingTier == tier) continue;
            var before = o.BlockedTiles.ToList();
            o.SharedPath = path;
            o.Shared = Db.Catalog.Get(path);
            // The object is created with the resolved shared, so its footprint blocks (00990bc0).
            RefreshBlocking(before.Concat(o.BlockedTiles).Concat(o.ActiveTiles));
            o.DwellingTier = tier;
            // The creature level picks one creature of the post (as the scripts' CreateDwelling does); the map's
            // creaturesEnabled belonged to the random shared's list. TODO(exe): how the dwelling (009a6a00, +0x128)
            // uses the level, and the RandomCreatures flag, are not traced.
            o.CreaturesEnabled = tier >= 4
                ? Enumerable.Range(0, o.Shared.DwellingCreatures.Count).Select(i => i == tier - 4).ToArray()
                : Array.Empty<bool>();
        }
    }

    /// <summary>The race of a player in the game (player vf 0x34), or null when the world has no such player.</summary>
    string? PlayerRace(int index) => index >= 0 ? PlayerByIndex(index)?.Race : null;

    MapObject? LinkedTown(MapObject o) =>
        o.LinkToTownUid is { } uid ? Map.Objects.FirstOrDefault(x => x.Kind == ObjectKind.Town && x.Uid == uid) : null;

    /// <summary>
    /// 0041b450: a town's race. A town whose shared type is not random keeps it. A random one: owned by a player and
    /// that player's MainTown → the player's race; owned otherwise, or unowned, RndSource RND_TOWN → the LinkToTown
    /// town's race (recursively, given up after 100 levels); unowned with RND_PLAYER → the LinkToPlayer player's race;
    /// anything else, or a race still random / none → a random race. The result of a random town is kept so that
    /// towns linked to it agree (the exe gets that from the per-object stream).
    /// </summary>
    string TownRace(MapObject o, int depth)
    {
        var placed = o.PlacedTownType ?? o.TownType ?? "";
        if (placed != RandomTownType) return placed;
        if (S.RandomObjects.TryGetValue(o.Index, out var known)) return known;
        string? race = null;
        if (depth <= 99)
        {
            if (o.Owner < 0)
            {
                if (o.RndSource == 1) race = PlayerRace(o.LinkToPlayer);
                else if (o.RndSource == 2 && LinkedTown(o) is { } l) race = TownRace(l, depth + 1);
            }
            else if (o.Owner < Map.Players.Count && Map.Players[o.Owner].MainTownUid == o.Uid) race = PlayerRace(o.Owner);
            else if (o.RndSource == 2 && LinkedTown(o) is { } l) race = TownRace(l, depth + 1);
        }
        if (race == null || !PlayableRaces.Contains(race)) race = RandomRace();
        if (S.RandomObjects.TryGetValue(o.Index, out known)) return known;   // resolved meanwhile through a link cycle
        S.RandomObjects[o.Index] = race;
        return race;
    }

    /// <summary>The shared's RandomType (enum 009fbe50): 0 specific, 1..7 the creature level, 8 the military post.</summary>
    static int RandomDwellingType(MapObject o) => (o.PlacedShared ?? o.Shared).DwellingRandomType switch
    {
        "DWELLING_TYPE_RANDOM_1L" => 1, "DWELLING_TYPE_RANDOM_2L" => 2, "DWELLING_TYPE_RANDOM_3L" => 3,
        "DWELLING_TYPE_RANDOM_4L" => 4, "DWELLING_TYPE_RANDOM_5L" => 5, "DWELLING_TYPE_RANDOM_6L" => 6,
        "DWELLING_TYPE_RANDOM_7L" => 7, "DWELLING_TYPE_RANDOM_MILITARY_POST" => 8, _ => 0,
    };

    /// <summary>
    /// 0041b9f0: the race from RndSource — RND_NONE the owner's race (unless none), RND_PLAYER the LinkToPlayer
    /// player's, RND_TOWN the LinkToTown town's (0041b450), RND_OWN or a race still random / none a random one; then
    /// the race's DWELLINGS group (0041a890 / 00bcb710) gives link 0 / 1 / 2 for levels 1 / 2 / 3 and link 3 for
    /// levels 4..7 (creature level 4..7) and the military post (level −1). A group too short creates nothing (the
    /// object keeps its random shared here).
    /// </summary>
    void ResolveDwelling(MapObject o)
    {
        if (S.RandomObjects.ContainsKey(o.Index)) return;
        string? race = o.RndSource switch
        {
            0 => PlayerRace(o.Owner),
            1 => PlayerRace(o.LinkToPlayer),
            2 => LinkedTown(o) is { } l ? TownRace(l, 0) : null,
            _ => null,
        };
        if (race == null || !PlayableRaces.Contains(race)) race = RandomRace();
        int type = RandomDwellingType(o);
        var (link, tier) = type switch { 1 => (0, 1), 2 => (1, 2), 3 => (2, 3), 8 => (3, -1), _ => (3, type) };
        var group = Db.DwellingGroup(race);
        if (link >= group.Count) return;
        S.RandomObjects[o.Index] = group[link] + "|" + tier;
    }
}
