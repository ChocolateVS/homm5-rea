using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Town;
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
/// Monster amounts use GameMechanics/roughAmounts.xdb (Few … Legion; 004bc930: the first item whose MaxAmount ≥ count, else
/// the last).
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
        // The dev/test fake mouse (`--adventure-hover`) drives the tooltip like the cursor (AdventureScreen._Process).
        var mouse = AdventureScreen.FakeMouse ?? GetViewport().GetMousePosition();
        string? key = null;
        Func<bool, Control?>? build = null;
        if (_adv.DialogLayer.GetChildCount() == 0 && (AdventureScreen.FakeMouse != null || GetViewport().GuiGetHoveredControl() is not { MouseFilter: MouseFilterEnum.Stop })
            && _adv.World.TileAtRay(_adv.World.Camera.ProjectRayOrigin(mouse), _adv.World.Camera.ProjectRayNormal(mouse)) is { } tile
            && _adv.World.IsExplored(tile))
            (key, build) = Describe(tile, _adv.PickAt(mouse, tile));
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
        _frame = Frame(Common + "MainWnd.(WindowGathering).xdb", content);
        _content = content;
        AddChild(_frame);
        Place(mouse);
    }

    /// <summary>A stock tooltip window (its tiled background) sized around <paramref name="content"/> with the 13/12 px margins.</summary>
    public static UiNode Frame(string windowXdb, Control content)
    {
        var frame = new UiNode(UiResources.Instance.LoadWindow(windowXdb)) { MouseFilter = MouseFilterEnum.Ignore };
        frame.AddChild(content);
        frame.Size = content.CustomMinimumSize + new Vector2(26, 24);
        content.Position = new Vector2(13, 12);
        return frame;
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

    (string?, Func<bool, Control?>?) Describe(Tile tile, MapObject? picked)
    {
        var s = _adv.Session.S;
        var game = _adv.Session.Game;
        if (s.Heroes.FirstOrDefault(h => h.Alive && !h.Garrisoned && h.Pos == tile) is { } hero)
            return ($"h{hero.Id}", full => HeroContent(hero, full));
        // The tooltip follows the same pick as the cursor (model first, then the tile's object).
        if (picked != null && !(s.Objects.TryGetValue(picked.Index, out var ps) && ps.Removed))
        {
            if (picked.Kind == ObjectKind.Town && s.Town(picked.Index) is { } pt) return ($"t{pt.Object}", full => TownContent(pt));
            if (picked.Kind == ObjectKind.Monster) return ($"m{picked.Index}", full => MonsterContent(picked, full));
            if (picked.Shared.Messages.Count > 0) return ($"o{picked.Index}", full => ObjectContent(picked));
        }
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

    /// <summary>A text block of the given width whose height is measured with the stock bitmap fonts.</summary>
    public static BitmapText Text(string markup, float width)
    {
        var size = BitmapTextLayout.Build(markup, null, new Rect2(0, 0, width, 4000)).Size;
        return new BitmapText { Markup = markup, CustomMinimumSize = new Vector2(width, Mathf.Ceil(size.Y) + 2), MouseFilter = MouseFilterEnum.Ignore };
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

    /// <summary>
    /// The monster window (MONSTER_TOOLTIP_WND_ID; builder 005fc420 over SAdvMapMonsterTooltip from 004efc30), in the
    /// order of MonsterTooltip/MainWnd: Info = Name (004eea30: the creature's name for one live stack, RPGRoot
    /// MONSTERS_ARMY_NAME for several) and ObjectInfo (the PlayerColourSchemes text of colour 0, PCOLOR_NEUTRAL
    /// "Neutral"); only in the full window: one stack → MonsterFace + MonstersAmount (roughAmounts.xdb word of the
    /// largest stack, 004eed60; the exact count when a Scouting hero of the player is in reach, 004fcf90), several
    /// stacks → the army (rough or exact per stack, 00b496d0); Courage (the monster's courage — Will join / Will fight
    /// / Unpredictable) only when a Disguise and Reckon hero is in reach; Threat Level for the selected hero (004ea7a0).
    /// There is no join / flee / fight prediction beyond the courage line.
    /// </summary>
    Control MonsterContent(MapObject m, bool full)
    {
        var g = _adv.Session.Game;
        var s = _adv.Session.S;
        var guards = (s.Objects.TryGetValue(m.Index, out var st) ? st.Guards : new List<Stack>()).Where(x => x is { Count: > 0 }).ToList();
        bool several = guards.Count > 1;
        var main = guards.OrderByDescending(x => (long)(Game.Instance.Db.Creatures.GetValueOrDefault(x.Creature)?.Power ?? 0) * x.Count).FirstOrDefault();
        var creature = main?.Creature ?? m.Creature ?? "";
        string CreatureName(string id) => Game.Instance.Db.Creatures.GetValueOrDefault(id)?.Name ?? id;
        var title = several ? AdvTexts.Rpg("MONSTERS_ARMY_NAME") : CreatureName(creature);
        var l = new Lines();
        l.AddTitle(Fill(T(Common + "name.txt"), ("name", Esc(title))));
        l.Add(Fill(T(Common + "object_info.txt"), ("object_info", T("Text/Game/Player/Colors/neutral.txt").Trim())));
        if (full)
        {
            var (exact, courageKnown) = g.MonsterInsight(_adv.Session.Human.Index, m.Pos);
            if (!several)
            {
                if (TownUi.CreatureIcon(creature) is { } icon)
                {
                    var holder = new Control { CustomMinimumSize = new Vector2(197, 63), MouseFilter = MouseFilterEnum.Ignore };
                    holder.AddChild(new TextureRect
                    {
                        Texture = icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        Position = new Vector2((197 - 65) / 2f, 0), Size = new Vector2(65, 63), MouseFilter = MouseFilterEnum.Ignore,
                    });
                    l.AddChild(holder);
                }
                int count = main?.Count ?? 0;
                // The roughAmounts texts have two lines ("Horde" / "50-99").
                l.AddChild(Text(Fill(T("UI/Tooltips/Monster_ShrineTooltip/monsters_amount.txt"), ("monsters_amount", exact ? count.ToString() : Rough(count))), 197));
            }
            else
            {
                l.Add(T(Common + "army.txt"));
                foreach (var x in guards)
                    l.AddChild(Text($"<tooltip_bright>{Esc(CreatureName(x.Creature))}: {(exact ? x.Count.ToString() : string.Join(" ", Rough(x.Count).Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries)))}", 197));
            }
            if (courageKnown)
            {
                var courage = m.Courage switch
                {
                    "MONSTER_COURAGE_ALWAYS_JOIN" => T(Monster + "always_join.txt"),
                    "MONSTER_COURAGE_ALWAYS_FIGHT" => T(Monster + "always_fight.txt"),
                    _ => T(Monster + "unknown.txt"),
                };
                l.Add(Fill(T(Monster + "courage.txt"), ("courage", courage.Trim())));
            }
            if (s.Hero(_adv.SelectedHeroId) is { } sel && sel.Owner == _adv.Session.Human.Index && g.MonsterDanger(sel, guards) is { } danger)
                l.Add(Fill(T(Monster + "danger.txt"), ("danger", T(Monster + $"danger_level_{danger}.txt").Trim())));
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
            l.AddChild(Text(Fill(T(Common + "object_info.txt"), ("object_info", desc)), 197));
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
