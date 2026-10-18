using Godot;
using H5.Assets.Terrain;

namespace H5Reborn.Arena;

/// <summary>
/// The combat grid laid on an arena's terrain.
///
/// Placement rule (docs/formats/arena.md): the playable 12×10 grid covers map tiles
/// x ∈ [Mount.x, Mount.x+12) and y ∈ [Mount.y−11, Mount.y−1). The long axis (12) is
/// map +X, along which the two armies face each other; cell (0, y) is the
/// attacker's (left) edge. Cell (cx, cy) is map tile (Mount.x+cx, Mount.y−11+cy).
/// One tile = one world unit. Godot space is Y-up: map (x, y, height) → (x, height, −y).
/// </summary>
public sealed class ArenaGrid
{
    public const int Width = 12, Height = 10;
    /// <summary>
    /// The engine's internal grid is 16×12: fixed obstacle data (ArenaObjects/BoatArena,
    /// AbyssArena) addresses cells x 2..13, y 1..10 symmetrically. Internal (ix, iy) = (cx+2, cy+1).
    /// </summary>
    public const int MarginX = 2, MarginY = 1;

    /// <summary>Converts the engine's internal cell coordinates (fixed obstacles) to playable cells.</summary>
    public static Vector2I FromInternal(int ix, int iy) => new(ix - MarginX, iy - MarginY);

    readonly TerrainData _terrain;
    public readonly int OriginTileX, OriginTileY;
    public readonly float AdditionalHeight;

    public ArenaGrid(ArenaDefinition def) : this(def.Terrain, def.MountX, def.MountY, def.AdditionalHeight) { }

    public ArenaGrid(TerrainData terrain, int mountX, int mountY, float additionalHeight = 0)
    {
        _terrain = terrain;
        OriginTileX = mountX;
        OriginTileY = mountY - 11;
        AdditionalHeight = additionalHeight;
    }

    public static bool InBounds(Vector2I c) => c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;

    /// <summary>Terrain height in world units at a map position (tile units).</summary>
    public float HeightAt(float mapX, float mapY) => _terrain.SampleHeight(mapX, mapY) + AdditionalHeight;

    public static Vector3 MapToWorld(float mapX, float mapY, float height) => new(mapX, height, -mapY);
    public static Vector2 WorldToMap(Vector3 w) => new(w.X, -w.Z);

    /// <summary>World position of a cell's centre on the terrain.</summary>
    public Vector3 CellCenter(Vector2I cell) => CellCenter(cell.X + 0.5f, cell.Y + 0.5f);

    /// <summary>World position of fractional grid coordinates (cell units, origin at the grid corner).</summary>
    public Vector3 CellCenter(float gx, float gy)
    {
        float mx = OriginTileX + gx, my = OriginTileY + gy;
        return MapToWorld(mx, my, HeightAt(mx, my));
    }

    /// <summary>Centre of a creature occupying size×size cells with its lowest-index cell at <paramref name="cell"/>.</summary>
    public Vector3 StackCenter(Vector2I cell, int size) => CellCenter(cell.X + size * 0.5f, cell.Y + size * 0.5f);

    public Vector3 GridCenter => CellCenter(Width * 0.5f, Height * 0.5f);

    public Vector2I? CellAtWorld(Vector3 world)
    {
        var m = WorldToMap(world);
        var c = new Vector2I(Mathf.FloorToInt(m.X - OriginTileX), Mathf.FloorToInt(m.Y - OriginTileY));
        return InBounds(c) ? c : null;
    }

    /// <summary>Intersects a picking ray with the terrain under the grid (march, then bisect).</summary>
    public Vector2I? CellAtRay(Vector3 origin, Vector3 dir)
    {
        if (dir.Y >= -1e-4f) return null;
        dir = dir.Normalized();
        float step = 0.25f, t = 0, maxT = 500;
        float prevT = 0;
        while (t < maxT)
        {
            var p = origin + dir * t;
            var m = WorldToMap(p);
            if (p.Y <= HeightAt(m.X, m.Y))
            {
                float lo = prevT, hi = t;
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var q = origin + dir * mid;
                    var mq = WorldToMap(q);
                    if (q.Y <= HeightAt(mq.X, mq.Y)) hi = mid; else lo = mid;
                }
                return CellAtWorld(origin + dir * hi);
            }
            prevT = t;
            t += step;
        }
        return null;
    }

    /// <summary>
    /// Godot yaw (radians about +Y) facing the enemy: attacker (side 0) faces map +X, defender −X.
    /// Original creature models face source −Y, i.e. Godot +Z; rotating +Z by +90° about Y gives +X.
    /// </summary>
    public static float FacingYaw(int side) => side == 0 ? Mathf.Pi / 2 : -Mathf.Pi / 2;
}
