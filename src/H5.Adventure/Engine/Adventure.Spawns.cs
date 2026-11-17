using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    /// <summary>Set on the AI's simulation copies: they share <see cref="Map"/>, so they never add map objects.</summary>
    bool _simulation;

    /// <summary>
    /// The creature-week spawn, H5_Game.exe 009919a0 at the end of the weekly update 00993bf0 (the new week 00993cd0 →
    /// 00993bf0(1) and a week bought for days 2..7, 009949d0 → 00993bf0(0)): only in a creature week (00bcd640 of world
    /// +0x58 ≠ 0); RPGStats adventure/Respawn/MonsterProbabilityPerCell (00bcb3f0 +0xa4); per floor (world +0x30, in
    /// order) the mask of <see cref="CreatureWeekSpawn.BuildMask"/> and the loop of <see cref="CreatureWeekSpawn.Run"/>.
    /// Each spawn is a CAdvMapMonster (0x180 bytes, 004f06f0) of the week's creature with one stack of 2 × its WeeklyGrowth
    /// (0089fbf0 +0xa8, custom amount: 004ee750(creature, 1, amount, 0, 0) draws nothing), courage 2 and mood 1 for every
    /// player (004f06f0 → vf 0x20(player, +0x20, +0x1c) = the per-player courage / mood setter of the script command
    /// 00429fb0), placed by 0041aad0 with the CountCoef scaling (0041aa40 → 004ef500, <see cref="ApplyNeutralCountCoef"/>).
    /// </summary>
    public void CreatureWeekSpawns()
    {
        if (_simulation) return;
        int wc = MoonCalendar.CreatureOf(CurrentMoonWeekIndex);
        if (wc == 0 || Db.EnumName("CreatureType", wc) is not { } creature) return;
        if (!Core.Creatures.TryGetValue(creature, out var cd) || cd.MonsterSharedPath == null) return;   // GAP: no monster shared
        float p = (float)Db.Stats.Get("adventure/Respawn/MonsterProbabilityPerCell", 0);
        var towns = S.Towns.Where(t => t.Owner is >= 1 and <= 8).Select(t => EntranceOf(t)).Select(e => (e.X, e.Y)).ToList();
        var added = new List<int>();
        for (int fi = 0; fi < Map.Floors.Length; fi++)
        {
            if (Map.Floors[fi] is not { } f || f.Kind.Length == 0) continue;
            var live = Map.Objects.Where(o => o.Pos.Floor == fi && o.Kind != ObjectKind.Hero
                && !(S.Objects.TryGetValue(o.Index, out var os) && os.Removed)).ToList();
            var objs = new CreatureWeekSpawn.FloorObjects(
                live.SelectMany(o => o.BlockedTiles.Concat(o.ActiveTiles)).Where(t => t.Floor == fi).Select(t => (t.X, t.Y)).ToList(),
                live.Where(o => o.Kind == ObjectKind.Monster).Select(o => (o.Pos.X, o.Pos.Y)).ToList(),
                S.Heroes.Where(h => h.Alive && h.Pos.Floor == fi).Select(h => (h.Pos.X, h.Pos.Y)).ToList(),
                towns);
            var mask = CreatureWeekSpawn.BuildMask(f, Map.BorderSize, objs);
            foreach (var sp in CreatureWeekSpawn.Run(mask, fi, p, NextRand32))
            {
                var rec = new SpawnedMonster { Index = Map.Objects.Count, X = sp.X, Y = sp.Y, Floor = fi, Rot = sp.Facing, Creature = creature };
                S.SpawnedMonsters.Add(rec);
                var o = AddSpawnedMonster(rec);
                var st = S.Obj(o.Index);
                st.Creature = creature;
                st.Guards.Add(new Stack(creature, 2 * cd.WeeklyGrowth));
                ApplyNeutralCountCoef(o, st);
                added.Add(o.Index);
            }
        }
        // 0043cfa0(1) → 00a7d2a0 once when at least one spawned (the exe's world-changed notification; its listeners are
        // not traced): our UI builds the new models.
        if (added.Count > 0) Emit(new MonstersSpawned(added));
    }

    MapObject AddSpawnedMonster(SpawnedMonster rec)
    {
        var cd = Core.Creatures[rec.Creature];
        var o = new MapObject
        {
            Index = Map.Objects.Count, Kind = ObjectKind.Monster, Tag = "AdvMapMonster",
            Pos = new Tile(rec.X, rec.Y, rec.Floor), Rot = rec.Rot,
            SharedPath = cd.MonsterSharedPath!, Shared = Db.Catalog.Get(cd.MonsterSharedPath!),
            Creature = rec.Creature, Amount = 2 * cd.WeeklyGrowth, CustomAmount = true,
            Mood = "MONSTER_MOOD_AGGRESSIVE", Courage = "MONSTER_COURAGE_CAN_FLEE_JOIN",
        };
        Map.Objects.Add(o);
        RefreshBlocking(o.BlockedTiles);
        _activeIndex = null;
        _actionIndex = null;
        return o;
    }

    /// <summary>
    /// Rebuilds the map objects of <see cref="AdvState.SpawnedMonsters"/> whose index is the next free one (a loaded game:
    /// at construction and around every script-created object the script host re-adds, so both keep their indexes).
    /// </summary>
    void RestoreSpawnedMonsters()
    {
        foreach (var rec in S.SpawnedMonsters)
            if (rec.Index == Map.Objects.Count && Core.Creatures.ContainsKey(rec.Creature)) AddSpawnedMonster(rec);
    }
}
