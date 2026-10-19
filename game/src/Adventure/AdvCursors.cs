using System.Xml.Linq;
using Godot;
using H5Reborn.Combat;

namespace H5Reborn.Adventure;

/// <summary>The adventure cursor shapes (exe cursor enum, names at 00516900 / 005173d0).</summary>
public enum AdvCursor
{
    Default, Move, Attack, Use, Town, MeetHeroes, BoardShip, UnboardShip, ShipMove, Garrison,
    ScrollUp, ScrollDown, ScrollLeft, ScrollRight, ScrollLeftUp, ScrollRightUp, ScrollLeftDown, ScrollRightDown,
    /// <summary>CURSOR_FINGER (hwcursors Move.cur): over the selected hero.</summary>
    Finger,
    /// <summary>CURSOR_CANT_CAST: no route, no hero, outside the map.</summary>
    CantGo,
    /// <summary>CURSOR_WAIT: input locked (another player's turn, hero moving).</summary>
    Wait,
    /// <summary>CURSOR_CAST: aiming an adventure spell.</summary>
    Cast,
}

/// <summary>
/// Adventure cursors as H5_Game.exe picks them: the hover handler 0059e1f0 maps the hover action to an
/// enum value, the day-count families through 0058c500 (six entries per family, days clamped to 1..6,
/// the "_RED" table when the action's red flag is set; attacks always use the red table). The enum value
/// is looked up in UI/RefTables/Cursors.xdb (path string 00e1bbdc), whose FileName is the hardware
/// cursor under the install's hwcursors folder.
/// </summary>
public static class AdvCursors
{
    static readonly Dictionary<(AdvCursor, int, bool), (ImageTexture Tex, Vector2 Hot)?> _cache = new();
    static Dictionary<string, string>? _files;
    static (AdvCursor, int, bool)? _current;

    static bool HasDays(AdvCursor c) => c is AdvCursor.Move or AdvCursor.Attack or AdvCursor.Use or AdvCursor.Town
        or AdvCursor.BoardShip or AdvCursor.UnboardShip or AdvCursor.ShipMove or AdvCursor.Garrison;

    /// <summary>The exe cursor id (Cursors.xdb ID) for a shape, days (1 = today) and red flag.</summary>
    public static string? CursorId(AdvCursor c, int days = 1, bool red = false)
    {
        string? family = c switch
        {
            AdvCursor.Move => "CURSOR_MOVE",
            AdvCursor.Use => "CURSOR_USE",
            // 0058c500 case 7 reads only the table at 00e21d68 (CURSOR_ATTACK_RED…_RED_6).
            AdvCursor.Attack => "CURSOR_ATTACK_RED",
            AdvCursor.Town => "CURSOR_OWN_TOWN",
            AdvCursor.Garrison => "CURSOR_OWN_BUILDING",
            AdvCursor.BoardShip => "CURSOR_BOARD_SHIP",
            AdvCursor.UnboardShip => "CURSOR_UNBOARD_SHIP",
            AdvCursor.ShipMove => "CURSOR_SHIP_MOVE",
            _ => null,
        };
        if (family != null)
        {
            if (red && c != AdvCursor.Attack) family += "_RED";
            int d = Math.Clamp(days, 1, 6);
            return d == 1 ? family : $"{family}_{d}";
        }
        return c switch
        {
            AdvCursor.Default => "CURSOR_DEFAULT",
            AdvCursor.MeetHeroes => "CURSOR_HERO_MEET",
            AdvCursor.Finger => "CURSOR_FINGER",
            AdvCursor.CantGo => "CURSOR_CANT_CAST",
            AdvCursor.Wait => "CURSOR_WAIT",
            AdvCursor.Cast => "CURSOR_CAST",
            AdvCursor.ScrollUp => "CURSOR_SCROLL_UP",
            AdvCursor.ScrollDown => "CURSOR_SCROLL_DOWN",
            AdvCursor.ScrollLeft => "CURSOR_SCROLL_LEFT",
            AdvCursor.ScrollRight => "CURSOR_SCROLL_RIGHT",
            AdvCursor.ScrollLeftUp => "CURSOR_SCROLL_LEFTUP",
            AdvCursor.ScrollRightUp => "CURSOR_SCROLL_RIGHTUP",
            AdvCursor.ScrollLeftDown => "CURSOR_SCROLL_LEFTDOWN",
            AdvCursor.ScrollRightDown => "CURSOR_SCROLL_RIGHTDOWN",
            _ => null,
        };
    }

    /// <summary>ID → hardware cursor file name from UI/RefTables/Cursors.xdb.</summary>
    static Dictionary<string, string> Files()
    {
        if (_files != null) return _files;
        _files = new(StringComparer.Ordinal);
        try
        {
            var doc = Game.Instance.Xdb.Load("/UI/RefTables/Cursors.xdb");
            foreach (var item in doc.Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var id = (string?)item.Element("ID");
                var file = (string?)item.Element("obj")?.Element("FileName");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(file)) continue;
                _files[id] = file.Replace('\\', '/').Split('/')[^1];
            }
        }
        catch (Exception e) { GD.PushWarning($"[H5] Cursors.xdb: {e.Message}"); }
        return _files;
    }

    static (ImageTexture Tex, Vector2 Hot)? Load(AdvCursor c, int days, bool red)
    {
        var key = (c, HasDays(c) ? Math.Clamp(days, 1, 6) : 1, HasDays(c) && red);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        (ImageTexture, Vector2)? result = null;
        var dir = H5.Core.IO.InstallPath.ResolveOrCombine(Game.Instance.Install.Root, "hwcursors");
        if (CursorId(key.Item1, key.Item2, key.Item3) is { } id && Files().TryGetValue(id, out var name))
        {
            var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
            if (file != null)
            {
                try
                {
                    var (img, hx, hy) = CombatCursors.Decode(File.ReadAllBytes(file));
                    result = (ImageTexture.CreateFromImage(img), new Vector2(hx, hy));
                }
                catch (Exception e) { GD.PushWarning($"[H5] cursor {name}: {e.Message}"); }
            }
        }
        return _cache[key] = result;
    }

    /// <summary>The cursor currently shown (for drawing it in-frame in automated screenshots).</summary>
    public static (AdvCursor Cursor, int Days, bool Red) Current => _current ?? (AdvCursor.Default, 1, false);

    public static void Set(AdvCursor c, int days = 1, bool red = false)
    {
        var key = (c, days, red);
        if (_current == key) return;
        _current = key;
        if (Load(c, days, red) is { } cur) Input.SetCustomMouseCursor(cur.Tex, Input.CursorShape.Arrow, cur.Hot);
        else Input.SetCustomMouseCursor(null);
    }

    public static (ImageTexture Tex, Vector2 Hot)? Texture(AdvCursor c, int days = 1, bool red = false) => Load(c, days, red);

    public static void Reset()
    {
        _current = null;
        Input.SetCustomMouseCursor(null);
    }
}
