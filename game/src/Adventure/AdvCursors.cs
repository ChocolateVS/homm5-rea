using Godot;
using H5Reborn.Combat;

namespace H5Reborn.Adventure;

/// <summary>The adventure cursor shapes (install root hwcursors/*.cur).</summary>
public enum AdvCursor
{
    Default, Move, Attack, Use, Town, MeetHeroes, BoardShip, UnboardShip, ShipMove, Garrison,
    ScrollUp, ScrollDown, ScrollLeft, ScrollRight, ScrollLeftUp, ScrollRightUp, ScrollLeftDown, ScrollRightDown,
}

/// <summary>
/// Adventure cursors. Movement-type cursors exist in six variants by the number of days
/// the hero needs to get there (MoveN, MoveN2 … MoveN6; Attack…Attack6; UseN…UseN6;
/// Town…Town6; BoardShip…6), with "_a" companions not used here.
/// Files are decoded with the combat cursor decoder (classic .cur icons).
/// </summary>
public static class AdvCursors
{
    static readonly Dictionary<(AdvCursor, int), (ImageTexture Tex, Vector2 Hot)?> _cache = new();
    static (AdvCursor, int)? _current;

    static string? FileFor(AdvCursor c, int days)
    {
        return c switch
        {
            AdvCursor.Default => "Default.cur",
            AdvCursor.Move => days <= 1 ? "MoveN.cur" : $"MoveN{Math.Min(days, 6)}.cur",
            AdvCursor.Attack => days <= 1 ? "Attack.cur" : $"Attack{Math.Min(days, 6)}.cur",
            AdvCursor.Use => days <= 1 ? "UseN.cur" : $"UseN{Math.Min(days, 6)}.cur",
            AdvCursor.Town => days <= 1 ? "Town.cur" : $"Town{Math.Min(days, 6)}.cur",
            AdvCursor.BoardShip => days <= 1 ? "BoardShip.cur" : $"BoardShip{Math.Min(days, 6)}.cur",
            AdvCursor.UnboardShip => days <= 1 ? "UnboardShip.cur" : $"UnboardShip{Math.Min(days, 6)}.cur",
            AdvCursor.ShipMove => days <= 1 ? "ShipMove.cur" : $"ShipMove{Math.Min(days, 6)}.cur",
            AdvCursor.Garrison => days <= 1 ? "garrison_guarding.cur" : $"garrison_guarding{Math.Min(days, 6)}.cur",
            AdvCursor.MeetHeroes => "MeetHeroes.cur",
            AdvCursor.ScrollUp => "Scroll_Up.cur",
            AdvCursor.ScrollDown => "Scroll_Down.cur",
            AdvCursor.ScrollLeft => "Scroll_Left.cur",
            AdvCursor.ScrollRight => "Scroll_Right.cur",
            AdvCursor.ScrollLeftUp => "Scroll_LeftUp.cur",
            AdvCursor.ScrollRightUp => "Scroll_RightUp.cur",
            AdvCursor.ScrollLeftDown => "Scroll_LeftDown.cur",
            AdvCursor.ScrollRightDown => "Scroll_RightDown.cur",
            _ => null,
        };
    }

    static (ImageTexture Tex, Vector2 Hot)? Load(AdvCursor c, int days)
    {
        var key = (c, c is AdvCursor.Move or AdvCursor.Attack or AdvCursor.Use or AdvCursor.Town or AdvCursor.BoardShip
            or AdvCursor.UnboardShip or AdvCursor.ShipMove or AdvCursor.Garrison ? Math.Clamp(days, 1, 6) : 1);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        (ImageTexture, Vector2)? result = null;
        var dir = Path.Combine(Game.Instance.Install.Root, "hwcursors");
        if (FileFor(key.Item1, key.Item2) is { } name)
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
    public static (AdvCursor Cursor, int Days) Current => _current ?? (AdvCursor.Default, 1);

    public static void Set(AdvCursor c, int days = 1)
    {
        var key = (c, days);
        if (_current == key) return;
        _current = key;
        if (Load(c, days) is { } cur) Input.SetCustomMouseCursor(cur.Tex, Input.CursorShape.Arrow, cur.Hot);
        else Input.SetCustomMouseCursor(null);
    }

    public static (ImageTexture Tex, Vector2 Hot)? Texture(AdvCursor c, int days = 1) => Load(c, days);

    public static void Reset()
    {
        _current = null;
        Input.SetCustomMouseCursor(null);
    }
}
