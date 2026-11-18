using H5.Adventure.Map;
using H5.Core.Numerics;

namespace H5.Adventure.Engine;

/// <summary>
/// The creature-week neutral spawn of H5_Game.exe 009919a0 (the end of the weekly update 00993bf0): the free-cell mask
/// one floor gets (00ce9e40 / 00cea5b0 / 00cec080), the per-cell draw against MonsterProbabilityPerCell and the new
/// monster's facing. Pure functions over the map data; <see cref="Adventure"/> supplies the live objects.
/// </summary>
/// <remarks>
/// The exe's tile record (world vf 0x160 → +0x20, 0x58 per floor, 0x24-byte records; writers 00990800 at the world
/// setup and the object listener 009952d0 / 00993ab0, all through 00bcc600) maps to our data as: +8 the kind
/// (<see cref="MapFloor.Kind"/>), +0 the ground type (<see cref="MapFloor.Ground"/>), +0xc the water cover
/// (<see cref="TerrainPassability.WaterCovered"/>, 00c0f100; the border frame keeps the 00bcc5d0 default 0), +0x10 the
/// object occupancy (1 = a blocked tile, vf 0xb4, 00991650; 2 = an active tile, vf 0xb8, 00990bc0; 3 = a town's active
/// tile). 00bcc600 keeps two bitmaps per floor in step with the record (00bcc440 / 00bcc370): +0x10 (land refused: +0x10
/// = 1, kind 2 / 3 / 5 or ground TT_NONE) and +0x40 (+0x10 = 2).
/// </remarks>
public static class CreatureWeekSpawn
{
    /// <summary>A bitmap {+0 width, +8 rows, +0x10 height} as 00b5f720 sizes it: a set bit refuses the cell.</summary>
    public sealed class Mask
    {
        public readonly int Width, Height;
        readonly bool[] _bits;
        public Mask(int width, int height) { Width = width; Height = height; _bits = new bool[Math.Max(0, width * height)]; }
        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
        /// <summary>00433c40: out-of-range cells are ignored.</summary>
        public void Set(int x, int y) { if (InBounds(x, y)) _bits[y * Width + x] = true; }
        public bool this[int x, int y] => InBounds(x, y) && _bits[y * Width + x];
        /// <summary>Sets the (2r + 1)² square around (x, y).</summary>
        public void Square(int x, int y, int r)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++) Set(x + dx, y + dy);
        }
    }

    /// <summary>
    /// The live objects of one floor the mask builders read: <paramref name="ObjectTiles"/> the blocked and active tiles
    /// of the floor's objects (records +0x10 ≠ 0, bitmaps +0x10 / +0x40, floor list vf 0x38 → vf 0xb4 / vf 0xb8 at vf 0xa4,
    /// 00ce9d20 / 00ce9db0); <paramref name="Monsters"/> the positions of the floor's live neutral stacks (guard radius 1,
    /// 00cea5b0 → 00c27fc0 / 00c27d20 vf 0x1c); <paramref name="Heroes"/> the positions of the floor's heroes (00cec080:
    /// objects with vf 0xec and a vf 0x2c hero, 5×5); <paramref name="TownEntrances"/> every owned town's entrance on any
    /// floor (players 1..8, player vf 0xc4 → town vf 0xc4; 00cec080 reads only x and y of it, 5×5).
    /// </summary>
    public sealed record FloorObjects(IEnumerable<(int X, int Y)> ObjectTiles, IEnumerable<(int X, int Y)> Monsters,
        IEnumerable<(int X, int Y)> Heroes, IEnumerable<(int X, int Y)> TownEntrances);

    /// <summary>The border frame (floor vf 0x68) as <see cref="TerrainPassability.Build"/> clamps it.</summary>
    public static int Border(MapFloor f, int borderSize) =>
        Math.Max(1, Math.Min(Math.Clamp(borderSize, 1, 20), Math.Min(f.Width, f.Height)));

    /// <summary>
    /// The mask of one floor for land monsters (009919a0: 00ce9e40(world, floor, 0, 0, 0, 0, 0, 1) with DL = 1, then
    /// 00cea5b0(floor, 0, 0, 0) and 00cec080(floor, 0)):
    /// <list type="number">
    /// <item>00ce9bd0: the terrain's TilesX × TilesY; with a border above 1 every cell within it;</item>
    /// <item>00ce9d20 (land): bitmap +0x10 — kind Water / Impassable / Edge, ground TT_NONE, an object's blocked tile —
    /// and every floor object's blocked tiles;</item>
    /// <item>00ce9db0: bitmap +0x40 (active tiles) and every floor object's active tiles;</item>
    /// <item>00cea5b0: every live object's guard square (00c27d20: 1 for a neutral stack; 00c27c80's exception for an
    /// army object's active tile only spares cells step 3 already set);</item>
    /// <item>00cec080: the one-cell map edge; an inner cell stays free only when its record has +0x10 = 0, kind Land
    /// (+8 = 1), no water cover (+0xc = 0) and none of its 8 neighbours is water-covered; then 5×5 around every hero
    /// and every owned town's entrance.</item>
    /// </list>
    /// </summary>
    public static Mask BuildMask(MapFloor f, int borderSize, FloorObjects objs)
    {
        int w = f.Width, h = f.Height;
        var m = new Mask(w, h);
        int b = Border(f, borderSize);
        bool[] occupied = new bool[w * h];
        foreach (var (x, y) in objs.ObjectTiles) if (m.InBounds(x, y)) occupied[y * w + x] = true;
        var water = WaterCover(f, b);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                // 00ce9bd0
                if (b > 1 && (x < b || w - b <= x || y < b || h - b <= y)) m.Set(x, y);
                // bitmap +0x10 (00bcc440) and the object tiles of 00ce9d20 / 00ce9db0
                var kind = i < f.Kind.Length ? f.Kind[i] : TileKind.None;
                byte ground = i < f.Ground.Length ? f.Ground[i] : (byte)1;
                if (kind is TileKind.Water or TileKind.Impassable or TileKind.Edge || ground == TerrainPassability.TypeNone || occupied[i])
                    m.Set(x, y);
                // 00cec080
                if (x < 1 || w - 1 <= x || y < 1 || h - 1 <= y) { m.Set(x, y); continue; }
                bool free = !occupied[i] && kind == TileKind.Land && !water[i];
                for (int dy = -1; free && dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && water[(y + dy) * w + x + dx]) { free = false; break; }
                if (!free) m.Set(x, y);
            }
        foreach (var (x, y) in objs.Monsters) m.Square(x, y, 1);          // 00cea5b0
        foreach (var (x, y) in objs.Heroes) m.Square(x, y, 2);            // 00cec080, floor objects
        foreach (var (x, y) in objs.TownEntrances) m.Square(x, y, 2);     // 00cec080, players 1..8
        return m;
    }

    /// <summary>The record byte +0xc of every tile (00990800 → 00c0f100); the border frame keeps the default 0.</summary>
    static bool[] WaterCover(MapFloor f, int b)
    {
        int w = f.Width, h = f.Height;
        var r = new bool[w * h];
        if (f.Data is not { } t) return r;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (!(x < b || y < b || t.TilesX - b <= x || t.TilesY - b <= y))
                    r[y * w + x] = TerrainPassability.WaterCovered(t, f.LayerTypes, x, y);
        return r;
    }

    /// <summary>
    /// 00991ac2..00991af4: u = world RNG (vf 0x94 → vf 0x20) as FILD (+2³² when negative) × 2⁻³² (double 00e0d2d8),
    /// chopped to 24 bits; the cell spawns when that is below MonsterProbabilityPerCell (FCOMP float).
    /// </summary>
    public static float Draw(uint u) => X87.Chop((double)u * 2.3283064365386963E-10);

    /// <summary>One spawned monster: its cell and facing.</summary>
    public readonly record struct Spawn(int X, int Y, float Facing);

    /// <summary>
    /// The spawn loop of 009919a0 over one floor's mask: x outer, y inner; a set cell is skipped without a draw; a free
    /// cell draws once (<see cref="Draw"/>) and spawns when below <paramref name="probability"/>, the facing is taken
    /// (<see cref="Facing"/>) and the 3×3 around it is set.
    /// </summary>
    public static List<Spawn> Run(Mask m, int floor, float probability, Func<uint> rng)
    {
        var r = new List<Spawn>();
        for (int x = 0; x < m.Width; x++)
            for (int y = 0; y < m.Height; y++)
            {
                if (m[x, y]) continue;
                if (!(Draw(rng()) < probability)) continue;
                r.Add(new Spawn(x, y, Facing(m, floor, x, y)));
                m.Square(x, y, 1);                                      // 00991f3c..
            }
        return r;
    }

    /// <summary>
    /// 00991c17..00991e3b: over the 7×7 around (x, y) every set or off-map cell adds (−dx, −dy). A zero sum faces
    /// fmod(floor·7.5 + y·6.5 + x·3.5, 1.5707963705) (floats 00e531c4 / 00e620a0 / 00e57d10, double 00e62098, _CIfmod);
    /// otherwise the angle 004b45e0(Δy, Δx) between the world positions (00420480: 2t + 1 per axis) of the tile and of the
    /// tile + the sum, each coordinate masked to 10 bits as the packed tile keeps it.
    /// </summary>
    public static float Facing(Mask m, int floor, int x, int y)
    {
        int sx = 0, sy = 0;
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (!m.InBounds(cx, cy) || m[cx, cy]) { sx -= dx; sy -= dy; }
            }
        if (sx == 0 && sy == 0)
        {
            float s = X87.Add(X87.Add(X87.Mul(floor, 7.5f), X87.Mul((float)y, 6.5f)), X87.Mul((float)x, 3.5f));
            return X87.Chop(s % 1.5707963705062866);
        }
        float Pos(int t) => X87.Add(X87.Add(X87.Add(t, 0.5f), t), 0.5f);   // 00420480
        int x2 = (x + sx) & 0x3ff, y2 = (y + sy) & 0x3ff;
        float ddx = X87.Sub(Pos(x2), Pos(x)), ddy = X87.Sub(Pos(y2), Pos(y));
        return Angle(ddy, ddx);
    }

    /// <summary>
    /// 004b45e0(dy, dx): 0 for a zero vector; else the length by the table root 0046ed30, the unit vector (FDIVR 1 / len)
    /// and 004b4540(c = dx / len, s = dy / len): 0 when |s² + c² − 1| ≥ 0.001, else π/2 − asin(clamp(c)) (0046eb30,
    /// float 00e0d2d0), 2π − that (00e155a8) when clamp(s) &lt; 0; plus π/2.
    /// </summary>
    public static float Angle(float dy, float dx)
    {
        if (dx == 0f && dy == 0f) return 0f;
        float len = Adventure.TableSqrt(X87.Add(X87.Mul(dx, dx), X87.Mul(dy, dy)));
        float inv = X87.Div(1.0, len);
        float c = X87.Mul(dx, inv), s = X87.Mul(inv, dy);
        float r;
        if (!(Math.Abs(X87.Sub(X87.Add(X87.Mul(c, c), X87.Mul(s, s)), 1f)) < 0.001f)) r = 0f;
        else
        {
            float a = Math.Max(-1f, Math.Min(c, 1f)), bb = Math.Max(-1f, Math.Min(s, 1f));   // 00465610 / 00465640
            r = X87.Sub(1.5707964f, Asin(a));
            if (bb < 0f) r = X87.Sub(6.2831855f, r);
        }
        return X87.Add(r, 1.5707964f);
    }

    static readonly float[] AsinTable = Enumerable.Range(0, 514).Select(i => (float)DetMath.Asin(Math.Min(1.0, i / 512.0))).ToArray();

    /// <summary>
    /// 0046eb30: odd; for x ≥ 0, t = 512x (00e12df8), i = trunc(t − 0.5) (FISTP under chop), i &gt; 0x200 → 0x200, masked
    /// 0x1ff, then (T[i + 1] − T[i])·(t − i) + T[i] over the table 00e125f0 (asin(i / 512)).
    /// </summary>
    public static float Asin(float x)
    {
        if (x < 0f) return -Asin(-x);
        float t = X87.Mul(x, 512f);
        int i = (int)X87.Sub(t, 0.5f);
        float frac = X87.Sub(t, i);
        if (i > 0x200) i = 0x200;
        i &= 0x1ff;
        return X87.Add(X87.Mul(X87.Sub(AsinTable[i + 1], AsinTable[i]), frac), AsinTable[i]);
    }
}
