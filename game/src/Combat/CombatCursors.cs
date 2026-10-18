using Godot;

namespace H5Reborn.Combat;

/// <summary>The combat cursor shapes the original uses (hwcursors/*.cur in the install root).</summary>
public enum CombatCursor
{
    Default, Move, Fly, Shoot, ShootPenalty, Cast, CantCast, Ability, Wait, Interact,
    AttackLeft, AttackRight, AttackUp, AttackDown, AttackLeftUp, AttackLeftDown, AttackRightUp, AttackRightDown,
}

/// <summary>
/// Loads the stock Windows cursors from the install's hwcursors folder and applies
/// them as Godot custom mouse cursors. The files are classic .cur icons (palettised
/// or true-colour DIB with an AND mask) and are decoded here.
/// </summary>
public static class CombatCursors
{
    static readonly Dictionary<CombatCursor, string> Files = new()
    {
        [CombatCursor.Default] = "Default.cur",
        [CombatCursor.Move] = "Move.cur",
        [CombatCursor.Fly] = "Move.cur",
        [CombatCursor.Shoot] = "Cursor_Shoot.cur",
        [CombatCursor.ShootPenalty] = "Cursor_Shoot_Penalty.cur",
        [CombatCursor.Cast] = "cast.cur",
        [CombatCursor.CantCast] = "Cursor_CantCast.cur",
        [CombatCursor.Ability] = "Cursor_Ability.cur",
        [CombatCursor.Wait] = "wait.cur",
        [CombatCursor.Interact] = "Interakt.cur",
        [CombatCursor.AttackLeft] = "Attack_Left.cur",
        [CombatCursor.AttackRight] = "Attack_Right.cur",
        [CombatCursor.AttackUp] = "Attack_Up.cur",
        [CombatCursor.AttackDown] = "Attack_Down.cur",
        [CombatCursor.AttackLeftUp] = "Attack_LeftUp.cur",
        [CombatCursor.AttackLeftDown] = "Attack_LeftDown.cur",
        [CombatCursor.AttackRightUp] = "Attack_RightUp.cur",
        [CombatCursor.AttackRightDown] = "Attack_RightDown.cur",
    };

    static readonly Dictionary<CombatCursor, (ImageTexture Tex, Vector2 Hotspot)?> _cache = new();
    static CombatCursor? _current;

    public static void Set(CombatCursor c)
    {
        if (_current == c) return;
        _current = c;
        if (Load(c) is { } cur) Input.SetCustomMouseCursor(cur.Tex, Input.CursorShape.Arrow, cur.Hotspot);
        else Input.SetCustomMouseCursor(null);
    }

    /// <summary>The decoded cursor image and hotspot (for drawing it in-frame).</summary>
    public static (ImageTexture Tex, Vector2 Hotspot)? Texture(CombatCursor c) => Load(c);

    public static void Reset()
    {
        _current = null;
        Input.SetCustomMouseCursor(null);
    }

    static (ImageTexture Tex, Vector2 Hotspot)? Load(CombatCursor c)
    {
        if (_cache.TryGetValue(c, out var cached)) return cached;
        (ImageTexture Tex, Vector2 Hotspot)? result = null;
        var dir = System.IO.Path.Combine(Game.Instance.Install.Root, "hwcursors");
        var path = System.IO.Path.Combine(dir, Files[c]);
        // File names differ in case (.cur/.CUR); Windows is case-insensitive but be safe elsewhere.
        if (!System.IO.File.Exists(path) && System.IO.Directory.Exists(dir))
            path = System.IO.Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                string.Equals(System.IO.Path.GetFileName(f), Files[c], StringComparison.OrdinalIgnoreCase)) ?? path;
        if (System.IO.File.Exists(path))
        {
            try
            {
                var (img, hx, hy) = Decode(System.IO.File.ReadAllBytes(path));
                result = (ImageTexture.CreateFromImage(img), new Vector2(hx, hy));
            }
            catch (Exception e) { GD.PushWarning($"[H5] cursor {path}: {e.Message}"); }
        }
        _cache[c] = result;
        return result;
    }

    /// <summary>Decodes the first image of a .cur file into RGBA plus its hotspot.</summary>
    public static (Image Image, int HotX, int HotY) Decode(byte[] d)
    {
        if (BitConverter.ToUInt16(d, 2) != 2) throw new InvalidDataException("not a cursor");
        int hotX = BitConverter.ToUInt16(d, 6 + 4), hotY = BitConverter.ToUInt16(d, 6 + 6);
        int offset = BitConverter.ToInt32(d, 6 + 12);
        int w = BitConverter.ToInt32(d, offset + 4);
        int h = BitConverter.ToInt32(d, offset + 8) / 2;   // XOR + AND masks
        int bpp = BitConverter.ToUInt16(d, offset + 14);
        int headerSize = BitConverter.ToInt32(d, offset);
        int colors = BitConverter.ToInt32(d, offset + 32);
        if (bpp <= 8 && colors == 0) colors = 1 << bpp;
        int palette = offset + headerSize;
        int xor = palette + (bpp <= 8 ? colors * 4 : 0);
        int xorStride = ((w * bpp + 31) / 32) * 4;
        int and = xor + xorStride * h;
        int andStride = ((w + 31) / 32) * 4;

        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int row = h - 1 - y;    // DIBs are bottom-up
            for (int x = 0; x < w; x++)
            {
                byte r, g, b, a = 255;
                int p = xor + row * xorStride;
                switch (bpp)
                {
                    case 32: b = d[p + x * 4]; g = d[p + x * 4 + 1]; r = d[p + x * 4 + 2]; a = d[p + x * 4 + 3]; break;
                    case 24: b = d[p + x * 3]; g = d[p + x * 3 + 1]; r = d[p + x * 3 + 2]; break;
                    default:
                    {
                        int idx = bpp switch
                        {
                            8 => d[p + x],
                            4 => (d[p + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 0xF,
                            _ => (d[p + x / 8] >> (7 - x % 8)) & 1,
                        };
                        int c = palette + idx * 4;
                        b = d[c]; g = d[c + 1]; r = d[c + 2];
                        break;
                    }
                }
                if (bpp != 32)
                {
                    bool transparent = ((d[and + row * andStride + x / 8] >> (7 - x % 8)) & 1) != 0;
                    a = transparent ? (byte)0 : (byte)255;
                }
                int o = (y * w + x) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }
        return (Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba), hotX, hotY);
    }

    /// <summary>Dev helper: writes every combat cursor to PNGs for inspection.</summary>
    public static void DumpAll(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        foreach (var c in Enum.GetValues<CombatCursor>())
            if (Load(c) is { } cur) cur.Tex.GetImage().SavePng(System.IO.Path.Combine(dir, $"{c}_{cur.Hotspot.X}_{cur.Hotspot.Y}.png"));
    }
}
