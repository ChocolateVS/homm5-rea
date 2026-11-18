using Godot;
using H5.Combat;

namespace H5Reborn.Combat;

/// <summary>Dev hooks for the UI end-to-end runner (<see cref="CombatE2E"/>, `--combat-e2e=`); unused otherwise.</summary>
public partial class CombatScreen
{
    /// <summary>Every command the screen submits (player clicks, HUD buttons).</summary>
    internal static Action<BattleCommand>? DevSubmitted;
    /// <summary>Every event as it is played.</summary>
    internal static Action<BattleEvent>? DevEvent;
    internal bool DevIdle => !_playing && _queue.Count == 0 && Hud != null;
    internal int? DevSideToAct => MySideToAct();
    /// <summary>The armed targeting mode: "normal", "spell:ID" or "ability:ID".</summary>
    internal string DevArmed => _mode switch
    {
        InputMode.Spell => "spell:" + _pendingSpell?.Spell,
        InputMode.Ability => "ability:" + _pendingAbility?.Ability,
        _ => "normal",
    };
    /// <summary>What a left click at <paramref name="mouse"/> over <paramref name="cell"/> would submit, and the cursor shown.</summary>
    internal (BattleCommand? Cmd, CombatCursor Cursor) DevResolve(Vector2I cell, Vector2 mouse)
    {
        var a = Resolve(cell, mouse, out var cursor, out _);
        _lastHover = null;
        return (a?.Command, cursor);
    }
    internal void DevSubmitPass(BattleCommand cmd) => Submit(cmd);
    /// <summary>Draws the cursor at <see cref="CombatLauncher.FakeMouse"/> in screenshots.</summary>
    internal void DevFakeCursor()
    {
        if (_fakeCursor == null) { _fakeCursor = new TextureRect { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 }; AddChild(_fakeCursor); }
        _lastHover = null;
    }
}

public partial class CombatHud
{
    internal SpellBook3D? DevSpellBook => _spellPanel is SpellBook3D b && IsInstanceValid(b) ? b : null;
}

public partial class SpellBook3D
{
    /// <summary>
    /// E2E runner: the viewport point to click next to reach <paramref name="spell"/> (its slot, else its bookmark, else
    /// Next / Prev); null while a page turns or when the book does not hold it (<paramref name="what"/> says which).
    /// </summary>
    internal Vector2? DevNextClick(string spell, out string what)
    {
        what = "";
        if (_turning) { what = "turning"; return null; }
        foreach (var p in SpellParts)
            if (_slots.TryGetValue(p, out var s) && s.E?.Spell == spell && ScreenRect(p) is { } r)
            {
                var hit = PartAt(r.GetCenter(), SpellParts);
                what = $"spell part {p} rect {r}, a click there hits part {hit?.ToString() ?? "none"}"
                    + (hit is int hp && _slots.TryGetValue(hp, out var hs) ? " = " + (hs.E?.Spell ?? "empty") : "") + $", book size {Size}, idle {IdleKind} has {_book.Has(IdleKind)} playing '{_book.Player.CurrentAnimation}' anims {string.Join(" ", _book.Player.GetAnimationList())}";
                return ToVp(r.GetCenter());
            }
        int bm = Enumerable.Range(0, 7).Where(b => BookmarkPart(b) >= 0 && OnBookmark(b, spell)).DefaultIfEmpty(-1).First();
        if (bm < 0) { what = "absent"; return null; }
        if (bm != _bookmark) { what = "bookmark " + bm; return ScreenRect(BookmarkPart(bm)) is { } br ? ToVp(br.GetCenter()) : null; }
        int idx = SchoolSpells().FindIndex(e => e.Spell == spell);
        if (idx < 0 || _orcs) { what = "absent on bookmark " + bm; return null; }
        bool next = idx / 6 > _page;
        what = next ? "next" : "prev";
        return ScreenRect(next ? _d.Next : _d.Prev) is { } nr ? ToVp(nr.GetCenter()) : null;
    }

    Vector2 ToVp(Vector2 local) => GetGlobalTransformWithCanvas() * local;
}
