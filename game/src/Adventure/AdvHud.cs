using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5Reborn.Ui;
using TownBuildState = H5.Adventure.Engine.Adventure.TownBuildState;

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
    /// <summary>The 172×172 icon layer (minimap +0x12c, cleared by 00c1c4f0 and filled by 005acd80) drawn over the terrain.</summary>
    TextureRect? _minimapIcons;
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
        bus.On("adv_dig", _screen.DigWithSelected);
        bus.On("input_adv_show_kingdom_overview", () => _screen.ShowMissionBox("TabKingdom"));
        bus.On("input_open_puzzle_map", _screen.ShowPuzzleMap);
        bus.On("open_puzzle_map", _screen.ShowPuzzleMap);
        bus.On("SpellBookWindow", () => _screen.OpenAdventureSpellbook());
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
        // The fill keeps the host's full size and is cropped by the clip; hosts can still be
        // zero-sized when the bar is created, so size the fill on every update.
        if (bar.GetChildCount() > 0 && bar.GetChild(0) is TextureRect fill) fill.Size = host.Size;
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
        LayoutList(Math.Max(heroes.Count, towns.Count));
        var game = _screen.Session.Game;
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
            // Zzz marks a hero the player put to sleep; the engine has no sleep order yet, so it never
            // shows — and an empty slot shows nothing at all, like the original.
            Show(col, "HeroSleeping", false);
            Show(col, "MPPanel", hero != null);
            Show(col, "ManaPanel", hero != null);
            Show(col, "HeroBtn", hero != null);
            Show(col, "TownBtn", town != null);
            // Town marker (textures): yellow hammer NothingBuild = can build today, red house
            // BuildDone = built today, red hammer BuildNoResources = nothing affordable or allowed,
            // yellow house BuildAllComplete = everything built.
            var state = town != null ? game.BuildState(town) : (TownBuildState?)null;
            Show(col, "NothingBuild", state == TownBuildState.CanBuild);
            Show(col, "BuildDone", state == TownBuildState.BuiltToday);
            Show(col, "BuildNoResources", state == TownBuildState.NoResources);
            Show(col, "BuildAllComplete", state == TownBuildState.AllComplete);
        }
        UpdateMinimap();
        ApplyInputAuditToggles();
    }

    /// <summary>
    /// The list shows max(heroes, towns) columns — at least one, at most eight — and the end cap
    /// follows the last column: FUN_005eb570(count) is called with clamp(max(heroes, towns), 1, slots)
    /// (exe 005ee573..005ee5b0); it hides columns ≥ count, makes the panel count·61 + 13 + 14 wide
    /// (item width + Item_end width + Item_01's extra left edge) and puts Item_end at count·61 + 14.
    /// The scroll arrows start hidden (FUN_005ec980) and are shown here only when there are more
    /// than eight entries.
    /// </summary>
    void LayoutList(int entries)
    {
        int count = Math.Clamp(entries, 1, Columns);
        var panel = Screen.Find("TownHeroPanel");
        var first = Column(0);
        var second = Column(1);
        var end = Screen.Find("TownHeroPanel/Item_end");
        if (panel == null || first == null || second == null) return;
        float item = second.Size.X, firstExtra = first.Size.X - second.Size.X, endW = end?.Size.X ?? 0;
        for (int i = 0; i < Columns; i++)
            if (Column(i) is { } c) c.Visible = i < count;
        if (end != null) end.Position = new Vector2(first.Position.X + count * item + firstExtra, end.Position.Y);
        panel.Size = new Vector2(count * item + endW + firstExtra, panel.Size.Y);
        bool overflow = entries > Columns;
        if (Screen.Find("TownHeroPanel/LeftBtn") is { } left) left.Visible = overflow;
        if (Screen.Find("TownHeroPanel/RightBtn") is { } right)
        {
            right.Visible = overflow;
            right.Position = new Vector2(panel.Size.X - right.Size.X, right.Position.Y);
        }
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
        // Simultaneous turns: a human who ended its turn waits for the others (T_WAIT_FOR_PLAYERS).
        bool waiting = Ui.Net.LobbyService.Instance?.Adventure?.Replica.WaitingForPlayers == true;
        Screen.Find("EnemyTurnBox/Text")?.SetText(waiting ? UiResources.Instance.Text("UI/AdventureScreen/EnemyTurnBox/WaitForPlayers.txt") : null);
        if (playerIndex >= 0 && !waiting)
        {
            // EnemyTurn.txt: "Player <value=PlayerID> turn", filled with the colour name from the
            // box's related texts color_1..color_8 (Red, Blue, … in ColourModels order).
            var colour = _screen.Session.S.Players.Find(p => p.Index == playerIndex)?.Colour ?? "";
            int ci = AdvColors.ColourModelIndex(colour);
            var name = UiResources.Instance.Text($"UI/AdventureScreen/EnemyTurnBox/color_{Math.Max(1, ci)}.txt") ?? $"{playerIndex + 1}";
            Screen.Find("EnemyTurnBox/Text")?.SetValue("PlayerID", UiResources.StripMarkup(name).Trim());
        }
    }

    // ---------------------------------------------------------------- simultaneous turns

    public override void _EnterTree()
    {
        if (Ui.Net.LobbyService.Instance?.Adventure is not { } net) return;
        net.SimultaneousTurnsFinished += ShowSimultaneousFinished;
        if (net.SimultaneousMessagePending) { net.SimultaneousMessagePending = false; Callable.From(ShowSimultaneousFinished).CallDeferred(); }
    }

    public override void _ExitTree()
    {
        if (Ui.Net.LobbyService.Instance?.Adventure is { } net) net.SimultaneousTurnsFinished -= ShowSimultaneousFinished;
    }

    /// <summary>FUN_00679150 with T_SIMULTANEOUS_TURNS_FINISHED_HEADER / _TEXT (005a5d90 case 0x21).</summary>
    void ShowSimultaneousFinished()
    {
        if (!IsInsideTree()) return;
        var header = UiResources.Instance.Text("UI/AdventureScreen/EnemyTurnBox/SimultaneousTurnsFinishedHeader.txt") ?? "";
        var text = UiResources.Instance.Text("UI/AdventureScreen/EnemyTurnBox/SimultaneousTurnsFinishedText.txt") ?? "";
        Options.MessageBoxes.Message(_screen, header, text);
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
            ClipContents = true,
        };
        frame.AddChild(_minimap);
        _minimapIcons = new TextureRect
        {
            Name = "Icons", AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Nearest, MouseFilter = MouseFilterEnum.Ignore,
        };
        _minimap.AddChild(_minimapIcons);
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
        int b = map.BorderSize;
        float mx = b + local.X / _minimap.Size.X * (map.TilesX - 2 * b);
        float my = b + (1f - local.Y / _minimap.Size.Y) * (map.TilesY - 2 * b);
        _screen.World.Camera.CenterOn(new Tile((int)mx, (int)my, _screen.World.Floor));
    }

    /// <summary>
    /// The minimap (window class around 005acd80). The terrain buffer (+0x110) has one pixel per tile of the map without its
    /// border (size: world +0x4c − 2·border +0x1dc, ctor 005ad810); unexplored tiles are black. The objects are not
    /// coloured into it: they are icons of UI/AdventureScreen-FPP-2/MinimapTextures copied into a separate 172×172 layer
    /// (+0x12c, size 0xac; 00c1cae0 centres the icon on the point and overwrites the pixels whose alpha is not 0), placed
    /// by the transform 005ac8d0: x' = (x − border)·172 / (width − 2·border), y' = 172 − (y − border)·172 / (height − 2·border).
    /// </summary>
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
        var me = session.Human;
        var bits = !AdvWorld.RevealAll && f < me.Explored.Count ? me.Explored[f] : null;
        bool Explored(int x, int y) => bits == null || x < 0 || y < 0 || x >= map.TilesX || y >= map.TilesY || bits[y * map.TilesX + x];
        // Unexplored tiles are black.
        if (bits != null)
            for (int y = 0; y < Math.Min(map.TilesY, img.GetHeight()); y++)
            for (int x = 0; x < Math.Min(map.TilesX, img.GetWidth()); x++)
                if (!Explored(x, y)) img.SetPixel(x, y, Colors.Black);
        int b = map.BorderSize;
        int iw = Math.Max(1, map.TilesX - 2 * b), ih = Math.Max(1, map.TilesY - 2 * b);
        if (img.GetWidth() >= b + iw && img.GetHeight() >= b + ih)
            img = img.GetRegion(new Rect2I(b, b, iw, ih));
        img.FlipY();   // map y grows upwards on screen
        _minimap.Texture = ImageTexture.CreateFromImage(img);
        if (_minimapIcons != null)
        {
            var icons = MinimapIconLayer(f, b, iw, ih);
            // Icons over unexplored ground are not shown (the layer is masked with the explored tiles).
            if (bits != null)
                for (int y = 0; y < IconLayerSize; y++)
                for (int x = 0; x < IconLayerSize; x++)
                {
                    int tx = b + (int)(x * iw / (float)IconLayerSize), ty = b + (int)((IconLayerSize - 1 - y) * ih / (float)IconLayerSize);
                    if (!Explored(tx, ty)) icons.SetPixel(x, y, Colors.Transparent);
                }
            _minimapIcons.Texture = ImageTexture.CreateFromImage(icons);
        }
        _minimapOverlay?.QueueRedraw();
    }

    const int IconLayerSize = 172;

    /// <summary>
    /// The icon layer as 005acd80 fills it for a player (the branch where the viewing player's vf 0x44 ≠ 2), in its order:
    /// underground gates (list +0x140: objects whose building part is BUILDING_SUBTERRA_GATE, 00c1d2c0) as
    /// UnderworldExitEnter (00c1cf80); the citadels BUILDING_BIARA_CITADEL / BUILDING_DEMON_SOVEREIGN_CITADEL (list +0x14c)
    /// as Town_1 (00c1d090); flagged objects (list +0x134: neutral or owned by a player, 00c1d2c0) through 00c1cd70 —
    /// a town Town_%d, a mine or abandoned mine (type ids 0x16130cc3 / 0x16130cc5 = AdvMapMineShared / AdvMapAbanMineShared)
    /// Mine_%d, anything else Object_%d, %d being the owner's colour (0 neutral); the viewing player's caravans
    /// (Caravan_%d / Caravan_stopped_%d); the heroes on the floor (Hero_%d, owner's colour, at their tile, 005ac2e0).
    /// Objects are placed at their footprint centre (00c1c720: tile + the mean of the object's cell offsets).
    /// </summary>
    Image MinimapIconLayer(int floor, int border, int iw, int ih)
    {
        var layer = Image.CreateEmpty(IconLayerSize, IconLayerSize, false, Image.Format.Rgba8);
        var session = _screen.Session;
        var s = session.S;
        var map = session.Map;
        Vector2I At(Vector2 p) => new(
            (int)Math.Round((p.X - border) * IconLayerSize / iw),
            (int)Math.Round(IconLayerSize - (p.Y - border) * IconLayerSize / ih));
        int Colour(int player) => player < 0 ? 0 : AdvColors.ColourModelIndex(s.Players.Find(p => p.Index == player)?.Colour ?? "");
        var live = map.Objects.Where(o => o.Pos.Floor == floor && !(s.Objects.TryGetValue(o.Index, out var os) && os.Removed)).ToList();

        foreach (var o in live.Where(o => o.Kind == ObjectKind.Building && o.Shared.Type == "BUILDING_SUBTERRA_GATE" && !IsFlagged(o)))
            Blit(layer, "UnderworldExitEnter", At(Centre(o)));
        foreach (var o in live.Where(o => o.Shared.Type is "BUILDING_BIARA_CITADEL" or "BUILDING_DEMON_SOVEREIGN_CITADEL"))
            Blit(layer, "Town_1", At(Centre(o)));
        foreach (var o in live.Where(IsFlagged))
        {
            s.Objects.TryGetValue(o.Index, out var os);
            int owner = s.Town(o.Index)?.Owner ?? (os != null && os.Owner >= 0 ? os.Owner : o.Owner);
            string kind = o.Kind == ObjectKind.Town ? "Town_" : o.Kind is ObjectKind.Mine or ObjectKind.AbandonedMine ? "Mine_" : "Object_";
            Blit(layer, kind + Colour(owner), At(Centre(o)));
        }
        int me = session.Human.Index;
        foreach (var cv in s.Caravans)
            if (cv.Owner == me && cv.Pos.Floor == floor)
                Blit(layer, (cv.Stopped ? "Caravan_stopped_" : "Caravan_") + Colour(me), At(new Vector2(cv.Pos.X, cv.Pos.Y)));
        foreach (var h in s.Heroes)
            if (h.Alive && !h.Garrisoned && h.Pos.Floor == floor)
                Blit(layer, "Hero_" + Colour(h.Owner), At(new Vector2(h.Pos.X, h.Pos.Y)));
        return layer;
    }

    /// <summary>
    /// Flagged map objects (IAdvMapFlagged): the kinds whose visit captures them in the engine (towns, mines, abandoned
    /// mines, dwarven warrens, garrisons, dwellings, shipyards, lighthouses, dens of thieves).
    /// </summary>
    static bool IsFlagged(MapObject o) => o.Kind is ObjectKind.Town or ObjectKind.Mine or ObjectKind.AbandonedMine
        or ObjectKind.DwarvenWarren or ObjectKind.Garrison or ObjectKind.Dwelling or ObjectKind.Shipyard
        || o.Kind == ObjectKind.Building && o.Shared.Type is "BUILDING_LIGHTHOUSE" or H5.Adventure.Engine.Adventure.DenOfThieves;

    /// <summary>00c1c720: the object's tile plus the mean of its passable and blocked cell offsets (no division for one cell).</summary>
    static Vector2 Centre(MapObject o)
    {
        var cells = o.Shared.Passable.Select(c => H5.Adventure.Map.Footprint.Place(o.Pos, c, o.Rot))
            .Concat(o.BlockedTiles).ToList();
        float sx = 0, sy = 0;
        foreach (var t in cells) { sx += t.X - o.Pos.X; sy += t.Y - o.Pos.Y; }
        if (cells.Count > 1) { sx /= cells.Count; sy /= cells.Count; }
        return new Vector2(o.Pos.X + sx, o.Pos.Y + sy);
    }

    /// <summary>00c1cae0: the icon centred on the point (top-left at point − size/2), pixels with alpha 0 skipped.</summary>
    static void Blit(Image layer, string icon, Vector2I at)
    {
        if (MinimapIconImage(icon) is not { } src) return;
        int w = src.GetWidth(), h = src.GetHeight();
        int x0 = at.X - w / 2, y0 = at.Y - h / 2;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int px = x0 + x, py = y0 + y;
            if (px < 0 || py < 0 || px >= IconLayerSize || py >= IconLayerSize) continue;
            var c = src.GetPixel(x, y);
            if (c.A8 != 0) layer.SetPixel(px, py, c);
        }
    }

    static Dictionary<string, string>? _iconPaths;
    static readonly Dictionary<string, Image?> _iconImages = new();

    static Image? MinimapIconImage(string name)
    {
        if (_iconImages.TryGetValue(name, out var cached)) return cached;
        if (_iconPaths == null)
        {
            _iconPaths = new();
            const string list = "UI/AdventureScreen-FPP-2/MinimapTextures.(WindowRelatedTextures).xdb";
            try
            {
                if (Game.Instance.Fs.Exists(list))
                    foreach (var item in Game.Instance.Xdb.Load(list).Root?.Element("textures")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                        if ((string?)item.Element("TextureName") is { } n && (string?)item.Element("Texture")?.Attribute("href") is { Length: > 0 } h)
                            _iconPaths[n] = XdbRef.Parse(h, list).Path;
            }
            catch { /* no data: no icons */ }
        }
        Image? img = null;
        if (_iconPaths.TryGetValue(name, out var path) && Ui.UiResources.Instance.Texture(path)?.GetImage() is { } i)
        {
            img = (Image)i.Duplicate();
            if (img.IsCompressed()) img.Decompress();
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
        }
        _iconImages[name] = img;
        return img;
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
                // Rays at or above the horizon never meet the ground: stop them at the far plane.
                if (t < 0 || t > cam.Far) t = cam.Far;
                var p = AdvWorld.WorldToMap(o + d * t);
                int b = map.BorderSize;
                pts.Add(new Vector2((p.X - b) / (map.TilesX - 2 * b) * Size.X, (1 - (p.Y - b) / (map.TilesY - 2 * b)) * Size.Y));
            }
            // The view footprint is a trapezoid that can reach past the map edge; only the part
            // over the minimap is drawn, so the frame never spills onto the HUD around it.
            var clipped = ClipToRect(pts, new Rect2(Vector2.Zero, Size));
            if (clipped.Count >= 2)
            {
                clipped.Add(clipped[0]);
                DrawPolyline(clipped.ToArray(), new Color(1, 1, 1, 0.9f), 1f);
            }
        }

        /// <summary>Sutherland–Hodgman clip of a convex polygon to an axis-aligned rectangle.</summary>
        static List<Vector2> ClipToRect(List<Vector2> poly, Rect2 r)
        {
            static List<Vector2> Clip(List<Vector2> input, Func<Vector2, bool> inside, Func<Vector2, Vector2, Vector2> cross)
            {
                var output = new List<Vector2>();
                for (int i = 0; i < input.Count; i++)
                {
                    var cur = input[i];
                    var prev = input[(i + input.Count - 1) % input.Count];
                    bool ci = inside(cur), pi = inside(prev);
                    if (ci) { if (!pi) output.Add(cross(prev, cur)); output.Add(cur); }
                    else if (pi) output.Add(cross(prev, cur));
                }
                return output;
            }
            static Vector2 AtX(Vector2 a, Vector2 b, float x) => a + (b - a) * ((x - a.X) / (b.X - a.X));
            static Vector2 AtY(Vector2 a, Vector2 b, float y) => a + (b - a) * ((y - a.Y) / (b.Y - a.Y));
            float l = r.Position.X, t = r.Position.Y, rt = r.End.X, b = r.End.Y;
            var res = Clip(poly, p => p.X >= l, (a, c) => AtX(a, c, l));
            if (res.Count > 0) res = Clip(res, p => p.X <= rt, (a, c) => AtX(a, c, rt));
            if (res.Count > 0) res = Clip(res, p => p.Y >= t, (a, c) => AtY(a, c, t));
            if (res.Count > 0) res = Clip(res, p => p.Y <= b, (a, c) => AtY(a, c, b));
            return res;
        }
    }
}
