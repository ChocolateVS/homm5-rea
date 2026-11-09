using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// The hero's movement-cost settings (H5_Game.exe 00ca9c50 fills the defaults, hero vf 0x10c = 00b4ffe0 the rest):
/// +0 Pathfinding (vf 0x174(0x13) ≠ 0, halves the penalty), +1 Boots of the Open Road (WAYFARER_BOOTS, worn,
/// 00ca6680(0x1a): no penalty), +2 Angel Wings (worn 0x1e; not read by the step cost), +0xc the native ground
/// (00b4c2a0 over the hero's race +0x88), +0x10 Snatch (vf 0x174(0xa8) &gt; 0; not read by the step cost), +0x14 the
/// penalty reduction percent of HERO_SPEC_RASH (speciality 0x48): chop(level · Rash_MovementPenaltyReducePerLevel
/// (RPGStats +0x958) · 100), clamped to 0..100.
/// </summary>
public readonly record struct MoveCostSettings(bool HalfPenalty, bool NoPenalty, int NativeGround, int PenaltyReducePercent);

public sealed partial class Adventure
{
    const string PathfindingPerk = "HERO_SKILL_PATHFINDING";

    /// <summary>
    /// The race → native ground table 00b4c2a0 (race ids of 00875550: 1 Haven … 8 Stronghold; 0 → TT_NONE, other → grass).
    /// Ground ids are <see cref="TerrainPassability.TypeNames"/>.
    /// </summary>
    public static int NativeGround(string town) => town switch
    {
        "TOWN_HEAVEN" or "TOWN_PRESERVE" => 1,   // TT_GRASS
        "TOWN_ACADEMY" => 2,                     // TT_SAND
        "TOWN_INFERNO" => 4,                     // TT_LAVA
        "TOWN_NECROMANCY" => 0,                  // TT_DIRT
        "TOWN_DUNGEON" => 5,                     // TT_SUBTERRANEAN
        "TOWN_FORTRESS" => 3,                    // TT_SNOW
        "TOWN_STRONGHOLD" => 13,                 // TT_TAIGA
        _ => TerrainPassability.TypeNone,        // race 0
    };

    /// <summary>The settings of hero vf 0x10c (00b4ffe0); see <see cref="MoveCostSettings"/>.</summary>
    public MoveCostSettings MoveCostSettingsFor(HeroState h)
    {
        bool half = h.HasPerk(PathfindingPerk) || EffectiveMastery(h, PathfindingPerk) != 0;
        bool none = h.Artifacts.Contains("WAYFARER_BOOTS");
        int pct = 0;
        if (h.Specialization == "HERO_SPEC_RASH")
        {
            // 00b500a0..00b500c2: FILD level, × the float field, × 100.0f, FISTP with the chop control word.
            float f = (float)Core.Stats.Get("combat/HeroSpecializations/Barbarian/Rash_MovementPenaltyReducePerLevel", 0.02);
            int v = (int)Math.Truncate(h.Level * (double)f * 100.0);
            pct = v > 99 ? 100 : Math.Max(0, v);
        }
        return new MoveCostSettings(half, none, NativeGround(h.Town), pct);
    }

    /// <summary>
    /// The step cost, H5_Game.exe 00ca9a70 (tile record of the step's destination, diagonal, movement left before the step,
    /// settings, mode). Mode 0 (land): a road (tile +4 TT_DIRT_ROAD / TT_GRAVEL_ROAD / TT_COBBLESTONE_ROAD) costs 75;
    /// otherwise 100, plus — unless the ground (+0) is the native one (+0xc) — the ground penalty: 25 for dirt, lava,
    /// subterranean, taiga and wasteland, 50 for sand and snow, 0 for the rest; Pathfinding chop(× 0.5), the boots 0,
    /// then chop((1 − 0.01 · percent) · penalty). Mode 3 (00ca9a20) and every other mode (1 ship, 2, 4) cost 100. A
    /// diagonal step costs d = chop(cost · 1.4142135f), except when cost ≤ left &lt; d: then it costs cost. Every float
    /// conversion uses the chop control word (00ca9a3e, 00ca9b1c, 00ca9b56).
    /// </summary>
    public static int ExeStepCost(int ground, int road, bool diagonal, int left, in MoveCostSettings s, int mode)
    {
        int cost = 100;
        if (mode == 0)
        {
            if (road is 6 or 7 or 8) cost = 75;
            else if (ground != s.NativeGround)
            {
                int p = ground switch { 0 or 4 or 5 or 13 or 14 => 25, 2 or 3 => 50, _ => 0 };
                if (s.HalfPenalty) p = (int)Math.Truncate(p * (double)0.5f);
                if (s.NoPenalty) p = 0;
                if (s.PenaltyReducePercent > 0)
                    p = (int)Math.Truncate((1.0 - s.PenaltyReducePercent * (double)0.01f) * p);
                cost = p + 100;
            }
        }
        if (diagonal)
        {
            int d = (int)Math.Truncate(cost * (double)1.4142135f);
            if (left < cost || d <= left) return d;
        }
        return cost;
    }

    /// <summary>
    /// The ground and road type of a tile (passability tile +0 / +4, <see cref="MapFloor.Ground"/> / <see cref="MapFloor.Road"/>).
    /// A floor built without terrain data (tests) takes them from its <see cref="TerrainClass"/>.
    /// </summary>
    static (int Ground, int Road) GroundAndRoad(MapFloor f, int x, int y)
    {
        int i = y * f.Width + x;
        if (f.Ground.Length == f.Width * f.Height && f.Road.Length == f.Ground.Length) return (f.Ground[i], f.Road[i]);
        return f.At(x, y) switch
        {
            TerrainClass.Dirt => (0, 9),
            TerrainClass.Grass => (1, 9),
            TerrainClass.Sand => (2, 9),
            TerrainClass.Snow => (3, 9),
            TerrainClass.Lava => (4, 9),
            TerrainClass.Subterranean => (5, 9),
            TerrainClass.Road => (1, 6),
            TerrainClass.River => (10, 9),
            TerrainClass.Water => (11, 9),
            TerrainClass.DwarvenSubterranean => (12, 9),
            TerrainClass.Taiga => (13, 9),
            _ => (9, 9),
        };
    }

    /// <summary>
    /// Cost of stepping onto <paramref name="to"/> with <paramref name="left"/> movement points left before the step:
    /// hero vf 0x110 (00b500f0) → <see cref="ExeStepCost"/> on the destination's tile record, diagonal when both x and y
    /// change, mode 1 (ship) when sailing, else 0. The default <paramref name="left"/> gives the full diagonal cost.
    /// </summary>
    public int StepCost(HeroState h, Tile from, Tile to, int left = int.MaxValue)
    {
        var f = Map.Floor(to.Floor);
        if (f == null) return int.MaxValue;
        var (ground, road) = GroundAndRoad(f, to.X, to.Y);
        var s = MoveCostSettingsFor(h);
        return ExeStepCost(ground, road, from.X != to.X && from.Y != to.Y, left, s, h.InBoat ? 1 : 0);
    }
}
