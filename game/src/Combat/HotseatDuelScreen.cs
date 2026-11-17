using Godot;
using H5.Core.Xdb;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;
using H5Reborn.Ui.Net;

namespace H5Reborn.Combat;

/// <summary>
/// Hot-seat duel ("duel_mode_hotseat", 006919dd → 00730e50(0) → CHotSeatDuel 00733b50 / 00733680) on the stock duel
/// options window UI/MPDMWait without a network room. The exe builds one page per player (00733680: two
/// hero views, 00746220) and shows one at a time (00732930(page)): page 0 is player 1, page 1 player 2. The
/// buttons take the related texts hs_dm_start_text_%d / hs_dm_back_text_%d ("Next" / "Start", "Back" /
/// "Previous") and tooltips hs_dm_start_tooltip_%d / hs_dm_back_tooltip_%d of the page; the game name is the
/// related text ID_HS_DUEL_GAME_NAME. The arena selector (007447f0) lists DuelArenasTable's arenas; choosing one
/// (00744470) shows its Pic in arena_pic and the screen's related texture named by its town type (00acdae0) in
/// town_pic. The first arena has no CombatScene (Random_Arena): the start resolves it once to a random arena with a
/// scene (00743f80).
/// UNVERIFIED: the per-page state the screen's vf0(0, page == 0, page == 0, page == 0, 0) sets, the pages' preset
/// defaults (007469d0), and whether the exe requires a pick before Next.
/// </summary>
public partial class HotseatDuelScreen : UiScreen
{
    const string ArenasTable = "DuelArenasTable.(DuelArenas).xdb";

    sealed record ArenaEntry(string? Scene, string Name, string? Pic, string TownType);

    readonly ChatLogView _log = new();
    readonly PresetGridView? _grid;
    readonly TextureRect _face1 = Face(), _face2 = Face();
    readonly TextureRect _arenaPic = Picture(), _townPic = Picture();
    readonly List<ArenaEntry> _arenas = LoadArenas();
    readonly int?[] _picks = new int?[2];
    int _page;
    int _arena;
    int _resolvedArena = -1;

    public HotseatDuelScreen() : base("UI/MPDMWait.(WindowScreen).xdb")
    {
        // Network-only widgets of the stock window.
        foreach (var n in new[] { "tripleView_host", "tripleView_client", "time_pressing", "time_pressing_text", "ComboBoxPanel", "dndicon", "chatinput" })
            SetVisible(n, false);
        if (Find("combatturnspeed_text")?.GetParent() is Control speed) speed.Visible = false;
        if (Find("locked") is { } locked) locked.Enabled = false;

        if (Find("presets_grid") is { } gridNode && Root.Find("line_template") is { } template && Root.Find("presets_table_back") is { } back)
        {
            _grid = new PresetGridView(template, back.Rect.H, Find("table_slider") as UiScrollBar);
            _grid.Position = new Vector2(template.Rect.X - gridNode.Window.Rect.X, template.Rect.Y - gridNode.Window.Rect.Y);
            gridNode.AddChild(_grid);
            _grid.Picked += Pick;
        }
        Find("host_info/icon")?.AddChild(_face1);
        Find("client_info/icon")?.AddChild(_face2);
        Find("MPCommonChatLog")?.AddChild(_log);
        Find("arena_pic")?.AddChild(_arenaPic);
        Find("town_pic")?.AddChild(_townPic);
        if (Find("arena_selector") is UiComboBox combo)
        {
            combo.SetItems(_arenas.Select(a => a.Name), 0);
            combo.SelectionChanged += SelectArena;
        }
        Bus.On("start", Next);
        Bus.On("back", Back);
    }

    static TextureRect Face() => new()
    {
        AnchorRight = 1, AnchorBottom = 1,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    static TextureRect Picture() => new()
    {
        AnchorRight = 1, AnchorBottom = 1,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.Scale,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    /// <summary>DuelArenasTable arenas (SDuelArenas +0x44, 0x24 per entry): CombatScene, ArenaName, Pic, townType.</summary>
    static List<ArenaEntry> LoadArenas()
    {
        var list = new List<ArenaEntry>();
        if (!Game.Instance.Fs.Exists(ArenasTable) || Game.Instance.Xdb.Load(ArenasTable).Root is not { } root) return list;
        foreach (var item in root.Element("arenas")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            string? Href(string name) => (string?)item.Element(name)?.Attribute("href") is { Length: > 0 } h ? XdbRef.Parse(h, ArenasTable).Path : null;
            var scene = Href("CombatScene");
            var name = UiResources.Instance.Text(Href("ArenaName"))?.Trim() ?? "";
            list.Add(new ArenaEntry(scene != null ? System.IO.Path.GetFileNameWithoutExtension(scene) : null, name, Href("Pic"),
                (string?)item.Element("townType") ?? "TOWN_RANDOM_TYPE"));
        }
        return list;
    }

    public override void _Ready()
    {
        base._Ready();
        Find("game_name")?.SetValue("name", RelatedText("ID_HS_DUEL_GAME_NAME") ?? "");
        SelectArena(0);
        Refresh();
    }

    string? RelatedText(string id) =>
        Root.RelatedTexts.TryGetValue(id, out var p) ? UiResources.Instance.Text(p)?.Trim() : null;

    /// <summary>00744470: the combo's item, arena_pic = the arena's Pic, town_pic = the related texture of its town type.</summary>
    void SelectArena(int index)
    {
        if (index < 0 || index >= _arenas.Count) return;
        _arena = index;
        var a = _arenas[index];
        _arenaPic.Texture = UiResources.Instance.Texture(a.Pic);
        _townPic.Texture = Root.RelatedTextures.TryGetValue(a.TownType, out var t) ? UiResources.Instance.Texture(t) : null;
    }

    /// <summary>00743f80: an arena without a CombatScene resolves once to a random arena that has one.</summary>
    string? ResolveArena(ulong seed)
    {
        if (_arena >= _arenas.Count) return null;
        if (_arenas[_arena].Scene is { } s) return s;
        if (_resolvedArena < 0)
        {
            var withScene = Enumerable.Range(0, _arenas.Count).Where(i => _arenas[i].Scene != null).ToList();
            if (withScene.Count == 0) return null;
            _resolvedArena = withScene[(int)(seed % (ulong)withScene.Count)];
        }
        return _arenas[_resolvedArena].Scene;
    }

    void Pick(int index)
    {
        _picks[_page] = index;
        if (index < DuelPresetList.All.Count)
        {
            var p = DuelPresetList.All[index];
            _log.AddSystem($"Player {_page + 1}: {p.Name}" + (p.HeroName.Length > 0 ? $" ({p.HeroName}, level {p.Level})" : ""));
        }
        Refresh();
    }

    /// <summary>The start button: Next on page 0 (00732930(1)), Start on page 1 (00732f10).</summary>
    void Next()
    {
        if (_page == 0) { _page = 1; Refresh(); return; }
        Start();
    }

    /// <summary>The back button: Back on page 0 leaves the screen, Previous on page 1 returns to page 0.</summary>
    void Back()
    {
        if (_page == 1) { _page = 0; Refresh(); return; }
        UiNavigator.Goto("mainmenu");
    }

    void Refresh()
    {
        Find("host_info/info")?.SetValue("name", "Player 1");
        Find("client_info/info")?.SetValue("name", "Player 2");
        _face1.Texture = FaceOf(_picks[0], left: true);
        _face2.Texture = FaceOf(_picks[1], left: false);
        SetVisible("host_highlight", _picks[0] != null);
        SetVisible("client_highlight", _picks[1] != null);
        if (_grid != null) { _grid.Mine = _picks[_page]; _grid.Theirs = _picks[1 - _page]; _grid.Refresh(); }
        // 00732930: the page's button texts and tooltips.
        if (Find("creategamebutton") is { } start)
        {
            start.SetText(RelatedText($"hs_dm_start_text_{_page}"));
            start.SetTooltip(RelatedText($"hs_dm_start_tooltip_{_page}"));
        }
        if (Find("back_button") is { } back)
        {
            back.SetText(RelatedText($"hs_dm_back_text_{_page}"));
            back.SetTooltip(RelatedText($"hs_dm_back_tooltip_{_page}"));
        }
    }

    static Texture2D? FaceOf(int? i, bool left)
    {
        if (i is not int p || p < 0 || p >= DuelPresetList.All.Count) return null;
        return UiResources.Instance.Texture(left ? DuelPresetList.All[p].LeftFace : DuelPresetList.All[p].RightFace);
    }

    void Start()
    {
        if (_picks[0] is not int a || _picks[1] is not int b) { _log.AddSystem("Both players must choose a hero."); return; }
        var seed = (ulong)Time.GetTicksUsec();
        var arena = ResolveArena(seed);
        GD.Print($"[H5] hot-seat duel {a} vs {b}, arena {arena ?? "(engine pick)"}, seed {seed}");
        CombatScreen.Open(new LocalCombatSession(Game.Instance.Db, a, b, seed, new[] { 0, 1 }, arena));
    }
}
