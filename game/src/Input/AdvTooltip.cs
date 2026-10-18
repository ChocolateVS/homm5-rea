using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Interaction;

/// <summary>
/// Adventure-map object tooltips (manual p.8: "tips appear as soon as you point the cursor at certain
/// objects … made up of one or two windows. You can cycle through the windows by clicking on the right
/// mouse button"). Timing from UI/Tooltips/Main.(TooltipContext): AppearDelay 300 ms, ToFullTooltipDelay
/// 2000 ms (short window → full window). A right click on an object shows the full window at once.
/// Windows: UIGameRoot ADV_OBJ_TOOLTIP_WND_ID (UI/Tooltips/CommonAdvObjTooltip/MainWnd) and
/// MONSTER_TOOLTIP_WND_ID (UI/Tooltips/MonsterTooltip/MainWnd) — their tiled background and text
/// templates (name/object_info/owned/hero_level/army/courage/danger) are used as shipped; the gathering
/// layout stacks the sections with the windows' own 13 px margins and 16 px line height.
/// Monster amounts use GameMechanics/roughAmounts.xdb (Few … Legion).
/// </summary>
public partial class AdvTooltip : Control
{
    const string Common = "UI/Tooltips/CommonAdvObjTooltip/";
    const string Monster = "UI/Tooltips/MonsterTooltip/";
    const float AppearDelay = 0.3f, FullDelay = 2.0f;

    readonly AdventureScreen _adv;
    UiNode? _frame;
    Control? _content;
    string? _key;
    double _hoverTime;
    bool _forcedFull, _shownFull;
    Vector2 _rightPress;

    public AdvTooltip(AdventureScreen adv)
    {
        _adv = adv;
        Name = "AdvTooltip";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // Right click without dragging (dragging rotates the camera) shows the full window.
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right } rb)
        {
            if (rb.Pressed) _rightPress = rb.Position;
            else if ((rb.Position - _rightPress).Length() < 5 && _key != null) { _forcedFull = true; _hoverTime = FullDelay; }
        }
        else if (e is InputEventMouseButton { Pressed: true }) Hide();
    }

    public override void _Process(double delta)
    {
        var mouse = GetViewport().GetMousePosition();
        string? key = null;
        Func<bool, Control?>? build = null;
        if (_adv.DialogLayer.GetChildCount() == 0 && GetViewport().GuiGetHoveredControl() is not { MouseFilter: MouseFilterEnum.Stop }
            && _adv.World.TileAtRay(_adv.World.Camera.ProjectRayOrigin(mouse), _adv.World.Camera.ProjectRayNormal(mouse)) is { } tile
            && _adv.World.IsExplored(tile))
            (key, build) = Describe(tile);
        if (key != _key)
        {
            _key = key;
            _hoverTime = 0;
            _forcedFull = false;
            _shownFull = false;
            ClearWindow();
            if (key == null) return;
        }
        if (key == null || build == null) return;
        _hoverTime += delta;
        bool full = _forcedFull || _hoverTime >= FullDelay;
        if (_frame == null && _hoverTime >= AppearDelay || full && !_shownFull)
        {
            ClearWindow();
            _shownFull = full;
            Show(build(full), mouse);
        }
        else if (_frame != null) Place(mouse);
    }

    void ClearWindow()
    {
        _frame?.QueueFree();
        _frame = null;
        _content = null;
    }

    new void Hide() { ClearWindow(); _key = null; }

    void Show(Control? content, Vector2 mouse)
    {
        if (content == null) return;
        var root = UiResources.Instance.LoadWindow(Common + "MainWnd.(WindowGathering).xdb");
        _frame = new UiNode(root) { MouseFilter = MouseFilterEnum.Ignore };
        _content = content;
        _frame.AddChild(content);
        AddChild(_frame);
        _frame.Size = content.CustomMinimumSize + new Vector2(26, 24);
        content.Position = new Vector2(13, 12);
        Place(mouse);
    }

    void Place(Vector2 mouse)
    {
        if (_frame == null) return;
        var vp = GetViewportRect().Size;
        var p = mouse + new Vector2(16, 20);
        if (p.X + _frame.Size.X > vp.X) p.X = mouse.X - _frame.Size.X - 4;
        if (p.Y + _frame.Size.Y > vp.Y) p.Y = mouse.Y - _frame.Size.Y - 4;
        _frame.Position = p;
    }

    // ---------------------------------------------------------------- content

    (string?, Func<bool, Control?>?) Describe(Tile tile)
    {
        var s = _adv.Session.S;
        var game = _adv.Session.Game;
        if (s.Heroes.FirstOrDefault(h => h.Alive && !h.Garrisoned && h.Pos == tile) is { } hero)
            return ($"h{hero.Id}", full => HeroContent(hero, full));
        var townAt = game.TownAt(tile) ?? s.Towns.FirstOrDefault(tw => _adv.Session.Map.Objects.FirstOrDefault(o => o.Index == tw.Object) is { } to
            && (to.BlockedTiles.Contains(tile) || to.ActiveTiles.Contains(tile)));
        if (townAt is { } town)
            return ($"t{town.Object}", full => TownContent(town));
        if (game.GuardingMonster(tile) is { } mon && mon.Pos == tile)
            return ($"m{mon.Index}", full => MonsterContent(mon, full));
        var obj = _adv.Session.Map.Objects.FirstOrDefault(o => o.Kind != ObjectKind.Monster && (o.Pos == tile || o.ActiveTiles.Contains(tile) || o.BlockedTiles.Contains(tile))
            && !(s.Objects.TryGetValue(o.Index, out var st) && st.Removed) && o.Shared.Messages.Count > 0);
        if (obj != null) return ($"o{obj.Index}", full => ObjectContent(obj));
        return (null, null);
    }

    static string T(string path) => UiResources.Instance.Text(path) ?? "";

    static string Fill(string template, params (string K, string V)[] values)
    {
        foreach (var (k, v) in values) template = template.Replace($"<value={k}>", v);
        return template;
    }

    static string Esc(string s) => s.Replace('<', '‹').Replace('>', '›');

    sealed partial class Lines : VBoxContainer
    {
        public Lines() { AddThemeConstantOverride("separation", 0); MouseFilter = MouseFilterEnum.Ignore; }
        public void Add(string markup, float height = 16)
        {
            var bt = new BitmapText { Markup = markup, CustomMinimumSize = new Vector2(197, height), MouseFilter = MouseFilterEnum.Ignore };
            AddChild(bt);
        }
        /// <summary>The Name window is 16 px in the data but its tooltip_title font is taller; keep a 20 px row.</summary>
        public void AddTitle(string markup) => Add(markup, 20);
        public void Finish() => CustomMinimumSize = new Vector2(197, GetChildren().OfType<Control>().Sum(c => c.CustomMinimumSize.Y));
    }

    /// <summary>owned.txt with the player's colour (Text/Game/Player/Colors: &lt;c&gt;_color tag + name) and owned_by_player.txt (" Player").</summary>
    string OwnerName(int owner)
    {
        if (owner < 0) return "";
        var colour = _adv.Session.Map.Players.FirstOrDefault(p => p.Index == owner)?.Colour ?? "";
        var n = AdvColors.TextName(colour);
        var who = T($"Text/Game/Player/Colors/{n}_color.txt").Trim() + T($"Text/Game/Player/Colors/{n}.txt").Trim();
        return Fill(T(Common + "owned.txt"), ("who", who)) + T(Common + "owned_by_player.txt");
    }

    Control HeroContent(HeroState h, bool full)
    {
        var l = new Lines();
        l.AddTitle(Fill(T(Common + "name.txt"), ("name", Esc(h.Name) + Fill(T(Common + "hero_level.txt"), ("hero_level", h.Level.ToString())))));
        if (h.Owner >= 0) l.Add(OwnerName(h.Owner));
        if (full)
        {
            l.Add(T(Common + "army.txt"));
            bool mine = h.Owner == _adv.Session.Human.Index;
            foreach (var st in h.Army.Where(x => x is { Count: > 0 }))
                l.Add($"<tooltip_bright>{Esc(Game.Instance.Db.Creatures.GetValueOrDefault(st!.Creature)?.Name ?? st.Creature)}: {(mine ? st.Count.ToString() : Rough(st.Count))}");
        }
        l.Finish();
        return l;
    }

    Control TownContent(TownState t)
    {
        var l = new Lines();
        l.AddTitle(Fill(T(Common + "name.txt"), ("name", Esc(t.Name))));
        if (t.Owner >= 0) l.Add(OwnerName(t.Owner));
        l.Finish();
        return l;
    }

    Control MonsterContent(MapObject m, bool full)
    {
        var s = _adv.Session.S;
        var guards = s.Objects.TryGetValue(m.Index, out var st) ? st.Guards : new List<Stack>();
        int total = guards.Sum(g => g.Count);
        var creature = guards.FirstOrDefault()?.Creature ?? m.Creature ?? "";
        var l = new Lines();
        l.AddTitle(Fill(T(Common + "name.txt"), ("name", Esc(Game.Instance.Db.Creatures.GetValueOrDefault(creature)?.Name ?? creature))));
        l.Add(Fill(T(Common + "object_info.txt"), ("object_info", Rough(total))));
        if (full)
        {
            var courage = m.Courage switch
            {
                "MONSTER_COURAGE_ALWAYS_JOIN" => T(Monster + "always_join.txt"),
                "MONSTER_COURAGE_ALWAYS_FIGHT" => T(Monster + "always_fight.txt"),
                _ => T(Monster + "unknown.txt"),
            };
            l.Add(Fill(T(Monster + "courage.txt"), ("courage", courage)));
        }
        l.Finish();
        return l;
    }

    Control ObjectContent(MapObject o)
    {
        var l = new Lines();
        var name = T(o.Shared.Messages[0]);
        l.AddTitle(Fill(T(Common + "name.txt"), ("name", Esc(name))));
        if (o.Shared.Messages.Count > 1 && T(o.Shared.Messages[1]) is { Length: > 0 } desc)
        {
            var bt = new BitmapText { Markup = Fill(T(Common + "object_info.txt"), ("object_info", Esc(desc))), CustomMinimumSize = new Vector2(197, 16 * Math.Max(1, (desc.Length + 33) / 34)), MouseFilter = MouseFilterEnum.Ignore };
            l.AddChild(bt);
        }
        if (o.Owner >= 0 || (_adv.Session.S.Objects.TryGetValue(o.Index, out var st) && st.Owner >= 0))
            l.Add(OwnerName(_adv.Session.S.Objects.TryGetValue(o.Index, out var st2) && st2.Owner >= 0 ? st2.Owner : o.Owner));
        l.Finish();
        return l;
    }

    static List<(int Max, string Text)>? _rough;

    /// <summary>GameMechanics/roughAmounts.xdb: Few ≤4, Sev. ≤9, Pack ≤19 … Zounds ≤999, Legion above.</summary>
    static string Rough(int count)
    {
        _rough ??= LoadRough();
        foreach (var (max, text) in _rough)
            if (max == 0 || count <= max) return text;
        return count.ToString();
    }

    static List<(int, string)> LoadRough()
    {
        var list = new List<(int, string)>();
        const string path = "GameMechanics/roughAmounts.xdb";
        if (!Game.Instance.Fs.Exists(path)) return list;
        foreach (var item in Game.Instance.Xdb.Load(path).Root?.Element("amounts")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            int.TryParse((string?)item.Element("MaxAmount"), out var max);
            var href = (string?)item.Element("text")?.Attribute("href");
            list.Add((max, UiResources.Instance.Text(H5.Core.Xdb.XdbRef.Parse(href, path).Path)?.Trim() ?? ""));
        }
        return list;
    }
}
