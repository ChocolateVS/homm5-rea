using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The stock adventure HUD (UI/AdventureScreen-Heavy): resource bar, minimap, the
/// hero/town list (TownHeroPanel, 8 columns, hero over town), the jog dial with its
/// data-defined messages (JogDialButton = end turn, MB_Next_Hero, MB_Move_Hero,
/// flip_floor, EscMenu, information, SpellBookWindow, adv_dig) and the enemy-turn box.
/// </summary>
public partial class AdvHud : Control
{
    readonly AdventureScreen _screen;
    public UiScreen Screen { get; }

    const int Columns = 8;
    int _heroScroll, _townScroll;
    readonly List<TextureRect> _heroIcons = new(), _townIcons = new();
    readonly List<Control> _mpBars = new(), _manaBars = new();
    TextureRect? _minimap;
    Control? _minimapOverlay;
    Image? _minimapBase;
    int _minimapFloor = -1;

    static readonly string[] ResourceNames = { "Wood", "Ore", "Mercury", "Crystal", "Sulfur", "Gem", "Gold" };

    public AdvHud(AdventureScreen screen)
    {
        _screen = screen;
        Name = "Hud";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;

        Screen = new UiScreen("UI/AdventureScreen-Heavy.(WindowScreen).xdb");
        AddChild(Screen);
        foreach (var hidden in new[] { "EnemyTurnBox", "GhostGuard", "EOTNotify", "HotSeatBlackBg", "FlybyText", "OnJog-DigGrail" })
            Screen.SetVisible(hidden, false);

        var bus = Screen.Bus;
        bus.On("JogDialButton", _screen.EndTurn);
        bus.On("MB_Next_Hero", _screen.NextHero);
        bus.On("MB_Move_Hero", _screen.MoveSelectedHero);
        bus.On("flip_floor", _screen.FlipFloor);
        bus.On("EscMenu", _screen.ShowEscMenu);
        bus.On("information", _screen.ShowScenarioInfo);
        bus.On("SpellBookWindow", () => _screen.Message("Spellbook", "The adventure spellbook is not available yet."));
        bus.On("show_minimap", () => Toggle("Minimap"));
        bus.On("show_res_bar", () => Toggle("resource-bar"));
        bus.On("show_list_bar", () => Toggle("TownHeroPanel"));
        bus.On("hero_click", (cmd, src) => { if (ColumnOf(src) is int c) _screen.SelectHeroAt(_heroScroll + c); });
        bus.On("town_click", (cmd, src) => { if (ColumnOf(src) is int c) _screen.SelectTownAt(_townScroll + c); });
        bus.On("left", () => { _heroScroll = Math.Max(0, _heroScroll - 1); _townScroll = Math.Max(0, _townScroll - 1); Refresh(); });
        bus.On("right", () => { _heroScroll++; _townScroll++; Refresh(); });

        BuildListDecorations();
        BuildMinimap();
    }

    void Toggle(string window)
    {
        if (Screen.Find(window) is { } n) n.Visible = !n.Visible;
    }

    /// <summary>Column (0..7) of a TownHeroPanel button from its Item_NN ancestor.</summary>
    static int? ColumnOf(UiNode? node)
    {
        for (Node? n = node; n != null; n = n.GetParent())
            if (n is UiNode u && u.Window.Name.StartsWith("Item_", StringComparison.Ordinal) && int.TryParse(u.Window.Name[5..], out var i))
                return i - 1;
        return null;
    }

    UiNode? Column(int i) => Screen.Find($"TownHeroPanel/Item_{i + 1:00}");

    void BuildListDecorations()
    {
        var mpTex = Game.Instance.LoadTexture("UI/AdventureScreen-heavy/TownHeroPanel/HeroMP.(Texture).xdb");
        var manaTex = Game.Instance.LoadTexture("UI/AdventureScreen-heavy/TownHeroPanel/HeroMana.(Texture).xdb");
        for (int i = 0; i < Columns; i++)
        {
            var col = Column(i);
            var heroIcon = col?.FindChild("HeroIcon", true, false) as Control;
            var townIcon = col?.FindChild("TownIcon", true, false) as Control;
            var hi = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, ShowBehindParent = true };
            var ti = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, ShowBehindParent = true };
            heroIcon?.AddChild(hi);
            townIcon?.AddChild(ti);
            _heroIcons.Add(hi);
            _townIcons.Add(ti);
            // WindowProgressBar: the Forward texture cropped to the progress fraction.
            _mpBars.Add(Bar(col?.FindChild("MPPanel", true, false)?.FindChild("HeroMP", true, false) as Control, mpTex));
            _manaBars.Add(Bar(col?.FindChild("ManaPanel", true, false)?.FindChild("HeroMana", true, false) as Control, manaTex));
            foreach (var n in new[] { "GhostSelect", "GhostMP" })
                foreach (var g in col?.FindChildren(n, "", true, false) ?? new Godot.Collections.Array<Node>())
                    ((Control)g).Visible = false;
            // The duplicate MP bar under HeroIcon is drawn by MPPanel.
            if (heroIcon?.FindChild("HeroMP", true, false) is Control dup) dup.Visible = false;
        }
    }

    static Control Bar(Control? host, Texture2D? tex)
    {
        var clip = new Control { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        var rect = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        clip.AddChild(rect);
        if (host != null)
        {
            host.AddChild(clip);
            host.Resized += () => { rect.Size = host.Size; clip.Size = new Vector2(clip.Size.X, host.Size.Y); };
            rect.Size = host.Size;
            clip.Size = host.Size;
        }
        return clip;
    }

    static void SetBar(Control bar, float fraction)
    {
        if (bar.GetParent() is not Control host) return;
        bar.Size = new Vector2(host.Size.X * Math.Clamp(fraction, 0, 1), host.Size.Y);
    }

    // ---------------------------------------------------------------- refresh

    public void Refresh()
    {
        var s = _screen.Session.S;
        var me = _screen.Session.Human;
        for (int i = 0; i < 7; i++)
            Screen.Find($"resource-bar/{ResourceNames[i]}")?.SetText($"<center>{me.Purse[i]}");

        var heroes = _screen.MyHeroes();
        var towns = _screen.MyTowns();
        _heroScroll = Math.Clamp(_heroScroll, 0, Math.Max(0, heroes.Count - Columns));
        _townScroll = Math.Clamp(_townScroll, 0, Math.Max(0, towns.Count - Columns));
        for (int i = 0; i < Columns; i++)
        {
            var col = Column(i);
            var hero = _heroScroll + i < heroes.Count ? heroes[_heroScroll + i] : null;
            var town = _townScroll + i < towns.Count ? towns[_townScroll + i] : null;
            _heroIcons[i].Texture = hero != null ? HeroFace(hero) : null;
            _townIcons[i].Texture = town != null ? TownIcon(town) : null;
            SetBar(_mpBars[i], hero != null && hero.MaxMovePoints > 0 ? (float)hero.MovePoints / hero.MaxMovePoints : 0);
            SetBar(_manaBars[i], hero != null ? (float)hero.Mana / Math.Max(1, hero.Knowledge * 10) : 0);
            Show(col, "HeroSelect", hero != null && hero.Id == _screen.SelectedHeroId);
            Show(col, "HeroSleeping", hero == null);
            Show(col, "MPPanel", hero != null);
            Show(col, "ManaPanel", hero != null);
            Show(col, "HeroBtn", hero != null);
            Show(col, "TownBtn", town != null);
            Show(col, "NothingBuild", town == null);
            Show(col, "BuildDone", town != null && town.BuiltToday);
            Show(col, "BuildNoResources", false);
            Show(col, "BuildAllComplete", false);
        }
        UpdateMinimap();
    }

    static void Show(UiNode? col, string name, bool visible)
    {
        if (col?.FindChild(name, true, false) is Control c) c.Visible = visible;
    }

    static readonly Dictionary<string, Texture2D?> _faces = new();

    static Texture2D? HeroFace(HeroState h)
    {
        if (_faces.TryGetValue(h.DefId, out var t)) return t;
        t = null;
        if (Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) && Game.Instance.Fs.Exists(def.SourcePath))
        {
            var root = Game.Instance.Xdb.Load(def.SourcePath).Root!;
            var href = (string?)root.Element("FaceTextureSmall")?.Attribute("href") ?? (string?)root.Element("FaceTexture")?.Attribute("href");
            if (!string.IsNullOrEmpty(href)) t = Game.Instance.LoadTexture(XdbRef.Parse(href, def.SourcePath).Path);
        }
        return _faces[h.DefId] = t;
    }

    static Texture2D? TownIcon(TownState t)
    {
        const string table = "GameMechanics/RefTables/TownTypesInfo.xdb";
        var item = Game.Instance.Xdb.Load(table).Root!.Element("objects")!.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == t.Type);
        var icons = item?.Element("obj")?.Element("kingdomTownIcons")?.Element("textures")?.Elements("Item").ToList();
        if (icons is not { Count: > 0 }) return null;
        int level = Math.Clamp(t.Level("TB_FORT"), 0, icons.Count - 1);
        var href = (string?)icons[level].Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, table).Path);
    }

    // ---------------------------------------------------------------- enemy turn

    public void ShowEnemyTurn(int playerIndex)
    {
        var box = Screen.Find("EnemyTurnBox");
        if (box == null) return;
        box.Visible = playerIndex >= 0;
        if (playerIndex >= 0)
        {
            // EnemyTurn.txt: "Player <value=PlayerID> turn", filled with the colour name from the
            // box's related texts color_1..color_8 (Red, Blue, … in ColourModels order).
            var colour = _screen.Session.S.Players.Find(p => p.Index == playerIndex)?.Colour ?? "";
            int ci = AdvColors.ColourModelIndex(colour);
            var name = UiResources.Instance.Text($"UI/AdventureScreen/EnemyTurnBox/color_{Math.Max(1, ci)}.txt") ?? $"{playerIndex + 1}";
            Screen.Find("EnemyTurnBox/Text")?.SetValue("PlayerID", UiResources.StripMarkup(name).Trim());
        }
    }

    // ---------------------------------------------------------------- minimap

    void BuildMinimap()
    {
        var frame = Screen.Find("Minimap");
        if (frame == null) return;
        var inner = MinimapInnerRect(frame);
        _minimap = new TextureRect
        {
            Name = "MinimapImage", Position = inner.Position, Size = inner.Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = TextureFilterEnum.Linear, MouseFilter = MouseFilterEnum.Stop, ShowBehindParent = true,
        };
        frame.AddChild(_minimap);
        _minimapOverlay = new MinimapOverlay(this) { Name = "View", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        _minimap.AddChild(_minimapOverlay);
        _minimap.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb) JumpTo(mb.Position);
            else if (e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } mm) JumpTo(mm.Position);
        };
    }

    /// <summary>The transparent window of the minimap frame texture, found from its alpha channel.</summary>
    static Rect2 MinimapInnerRect(UiNode frame)
    {
        var size = new Vector2(frame.Window.Rect.W, frame.Window.Rect.H);
        var tex = frame.Window.Background?.DdsPath ?? frame.Window.Background?.TexturePath;
        var img = tex != null ? Game.Instance.LoadTexture(tex)?.GetImage() : null;
        if (img == null) return new Rect2(size * 0.1f, size * 0.8f);
        if (img.IsCompressed()) img.Decompress();
        int w = img.GetWidth(), h = img.GetHeight(), cx = w / 2, cy = h / 2;
        bool Clear(int x, int y) => img.GetPixel(x, y).A < 0.5f;
        int l = cx, r = cx, t = cy, b = cy;
        while (l > 0 && Clear(l - 1, cy)) l--;
        while (r < w - 1 && Clear(r + 1, cy)) r++;
        while (t > 0 && Clear(cx, t - 1)) t--;
        while (b < h - 1 && Clear(cx, b + 1)) b++;
        // BackgroundSimpleTexture draws the texture unscaled from the window's top-left
        // (the 256² frame is cropped to the 219×221 window), so texel = pixel.
        return new Rect2(new Vector2(l, t), new Vector2(Math.Min(r, size.X - 1) - l + 1, Math.Min(b, size.Y - 1) - t + 1));
    }

    void JumpTo(Vector2 local)
    {
        if (_minimap == null) return;
        var map = _screen.Session.Map;
        float mx = local.X / _minimap.Size.X * map.TilesX;
        float my = (1f - local.Y / _minimap.Size.Y) * map.TilesY;
        _screen.World.Camera.CenterOn(new Tile((int)mx, (int)my, _screen.World.Floor));
    }

    public void UpdateMinimap()
    {
        if (_minimap == null) return;
        var session = _screen.Session;
        var map = session.Map;
        int f = _screen.World.Floor;
        if (_minimapFloor != f || _minimapBase == null)
        {
            _minimapFloor = f;
            var floor = map.Floor(f);
            _minimapBase = floor?.Data != null ? AdvTerrain.MinimapImage(floor.Data) : Image.CreateEmpty(map.TilesX, map.TilesY, false, Image.Format.Rgba8);
            // Impassable scenery shows darker than open ground.
            if (floor != null)
                for (int y = 0; y < floor.Height; y++)
                for (int x = 0; x < floor.Width; x++)
                    if (floor.IsBlocked(x, y) && !floor.IsWater(x, y))
                        _minimapBase.SetPixel(x, y, _minimapBase.GetPixel(x, y).Darkened(0.45f));
        }
        var img = (Image)_minimapBase.Duplicate();
        var s = session.S;
        foreach (var o in map.Objects)
        {
            if (o.Pos.Floor != f || o.Kind is ObjectKind.Static or ObjectKind.Hero) continue;
            s.Objects.TryGetValue(o.Index, out var os);
            int owner = s.Town(o.Index)?.Owner ?? os?.Owner ?? o.Owner;
            if (owner < 0 || os?.Removed == true) continue;
            var c = AdvColors.Rgb(s.Players.Find(p => p.Index == owner)?.Colour ?? "");
            foreach (var t in o.BlockedTiles.Append(o.Pos))
                if (t.X >= 0 && t.Y >= 0 && t.X < map.TilesX && t.Y < map.TilesY) img.SetPixel(t.X, t.Y, c);
        }
        foreach (var h in s.Heroes)
        {
            if (!h.Alive || h.Garrisoned || h.Pos.Floor != f) continue;
            var c = AdvColors.Rgb(s.Players.Find(p => p.Index == h.Owner)?.Colour ?? "");
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = h.Pos.X + dx, y = h.Pos.Y + dy;
                if (x >= 0 && y >= 0 && x < map.TilesX && y < map.TilesY) img.SetPixel(x, y, c);
            }
        }
        // Unexplored tiles are black.
        var me = session.Human;
        if (!AdvWorld.RevealAll && f < me.Explored.Count && me.Explored[f] is { } bits)
            for (int y = 0; y < map.TilesY; y++)
            for (int x = 0; x < map.TilesX; x++)
                if (!bits[y * map.TilesX + x]) img.SetPixel(x, y, Colors.Black);
        img.FlipY();   // map y grows upwards on screen
        _minimap.Texture = ImageTexture.CreateFromImage(img);
        _minimapOverlay?.QueueRedraw();
    }

    /// <summary>Draws the camera's view footprint on the minimap.</summary>
    sealed partial class MinimapOverlay(AdvHud hud) : Control
    {
        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            var world = hud._screen.World;
            var cam = world.Camera;
            var map = hud._screen.Session.Map;
            var vp = GetViewport().GetVisibleRect().Size;
            var corners = new[] { Vector2.Zero, new Vector2(vp.X, 0), vp, new Vector2(0, vp.Y) };
            var pts = new List<Vector2>();
            foreach (var c in corners)
            {
                var o = cam.ProjectRayOrigin(c);
                var d = cam.ProjectRayNormal(c);
                float groundY = world.HeightAt(world.Floor, cam.Target.X, cam.Target.Y);
                if (Math.Abs(d.Y) < 1e-4f) return;
                float t = (groundY - o.Y) / d.Y;
                if (t < 0) t = 300;
                var p = AdvWorld.WorldToMap(o + d * t);
                pts.Add(new Vector2(p.X / map.TilesX * Size.X, (1 - p.Y / map.TilesY) * Size.Y));
            }
            pts.Add(pts[0]);
            DrawPolyline(pts.ToArray(), new Color(1, 1, 1, 0.9f), 1f);
        }
    }
}
