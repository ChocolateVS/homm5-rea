using Godot;
using H5.Core.Ui;
using H5.Save;
using H5Reborn.Adventure;
using H5Reborn.Options;
using H5Reborn.Ui;

namespace H5Reborn.Save;

/// <summary>
/// The stock save and load screens (UI/Save, UI/Load): the saves list on the left (rows
/// in the SavesList/Item format: save name, then time and date; Preselection on hover,
/// Selection on the chosen row), the SaveInfo panel (map name, the save's screenshot,
/// definition rows: map size, players, difficulty, game type, map or campaign
/// description), the name edit line (save screen), and Save/Load, Delete, Back.
/// Overwrite and delete ask first (UI/Common/saveload_resave, saveload_delete).
/// </summary>
public partial class SaveLoadScreen : UiScreen
{
    public enum Mode { Save, Load }

    readonly Mode _mode;
    readonly AdventureScreen? _game;
    readonly byte[]? _thumbnail;
    readonly Action _close;
    List<SaveEntry> _entries = new();
    readonly SavesListView _list;
    readonly TextureRect _shot = new() { AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
    UiNode? _definitionItem;
    UiEditLine? _edit;

    /// <summary>Which saves the list shows: network saves only on the network load list, the others elsewhere.</summary>
    readonly bool _network;
    readonly Action<SaveEntry>? _onLoad;

    public SaveLoadScreen(Mode mode, AdventureScreen? game, byte[]? thumbnail, Action close, bool network = false, Action<SaveEntry>? onLoad = null)
        : base(mode == Mode.Save ? "UI/Save.(WindowScreen).xdb" : "UI/Load.(WindowScreen).xdb")
    {
        _network = network || game?.Session.IsRemote == true;
        _onLoad = onLoad;
        _mode = mode;
        _game = game;
        _thumbnail = thumbnail;
        _close = close;
        // 007fecd0: the saves are items of the saveslist container (cleared, one row each, scrolled by its bar);
        // our rows are drawn by code in its content with the data's Selection / PreSelection markers.
        _list = new SavesListView(Find("saveslist") as UiScrollableContainer);
        if (_list.Container is { } box) box.Content.AddChild(_list); else Find("saveslist")?.AddChild(_list);
        Find("screenshoot")?.AddChild(_shot);
        _edit = Find("edit") as UiEditLine;
        if (_edit != null) { _edit.MaxLength = 64; _edit.Submitted += _ => DoSave(); }

        _list.Selected += _ => ShowInfo();
        _list.Activated += _ => { if (_mode == Mode.Load) DoLoad(); else DoSave(); };
        Bus.On("close", () => _close());
        Bus.On("save", DoSave);
        Bus.On("load", DoLoad);
        Bus.On("delete", DoDelete);
        Refresh();
    }

    /// <summary>Opens the save or load screen over the adventure map (Esc menu, F6/F7).</summary>
    public static void ShowInGame(AdventureScreen screen, Mode mode)
    {
        // The thumbnail is the map as it looks now, taken before the screen covers it.
        var thumb = mode == Mode.Save ? SaveManager.CaptureThumbnail(screen.GetViewport()) : null;
        SaveLoadScreen? s = null;
        s = new SaveLoadScreen(mode, screen, thumb, () => s!.QueueFree()) { MouseFilter = MouseFilterEnum.Stop };
        screen.DialogLayer.AddChild(s);
    }

    void Refresh(string? select = null)
    {
        // The exe keeps separate save/load screens per header type (SHeaderInfo, SLanSaveHeaderInfo, …).
        _entries = SaveSlots.List(SaveManager.Storage).Where(e => (e.Header.Kind == SaveKind.Multiplayer) == _network).ToList();
        _list.SetEntries(_entries);
        int index = select != null ? _entries.FindIndex(e => e.FileName == select) : -1;
        _list.Select(index >= 0 ? index : _mode == Mode.Load && _entries.Count > 0 ? 0 : -1);
        if (_mode == Mode.Save && _edit != null && _edit.Value.Length == 0 && _game != null)
            _edit.Value = _game.Session.Map.Name;
        ShowInfo();
    }

    SaveEntry? Current => _list.SelectedIndex is int i && i >= 0 && i < _entries.Count ? _entries[i] : null;

    void ShowInfo()
    {
        var e = Current;
        if (e != null && _mode == Mode.Save && _edit != null) _edit.Value = e.Header.Name;
        var h = e?.Header;
        if (h == null && _mode == Mode.Save && _game != null)
        {
            // Nothing selected on the save screen: describe the game about to be saved.
            var m = _game.Session.Map;
            h = new SaveHeader
            {
                MapName = m.Name, MapDescription = m.Description, MapSize = AdventureSave.MapSizeClass(m),
                Players = _game.Session.S.Players.Count, Difficulty = _game.Session.S.Difficulty,
            };
        }
        if (Find("mapname") is { } mapname) { mapname.Visible = h != null; mapname.SetValue("mapname", h?.MapName ?? ""); }
        _shot.Texture = null;
        if (e != null && e.Header.HasThumbnail)
        {
            try
            {
                var save = SaveArchive.FromBytes(SaveManager.Storage.Read(e.FileName));
                if (save.Parts.TryGetValue(AdventureSave.ThumbnailPart, out var png)) _shot.Texture = Png(png);
            }
            catch (Exception ex) when (ex is SaveCorruptException or IOException) { }
        }
        else if (e == null && _thumbnail != null) _shot.Texture = Png(_thumbnail);
        ShowDefinition(h);
        if (Find("delete") is { } del) del.Enabled = e != null;
        if (Find("load") is { } load) load.Enabled = e != null;
    }

    /// <summary>
    /// The save info definition (007fdb40 map saves / 007f7bb0 campaign saves): the definition container is cleared
    /// (slot 0x2c) and gets one UIGameRoot item (slot 8), shown only with a header. Map saves:
    /// ID_SAVELOAD_DEFINITION_ITEM — count (players, hidden when unknown), gametype (UIGameRoot game_type
    /// "game_type_%d", or "campain_game_type", 007f8200), mapsize (map_sizes "size%d", 007f6410), difficulty (hidden
    /// when unknown, 00a33740) and mapdescr. Campaign saves: ID_SAVELOAD_CAMPAIGN_DEFINITION_ITEM — count (missions)
    /// and campaigndescr.
    /// </summary>
    void ShowDefinition(SaveHeader? h)
    {
        if (Find("definition") is not UiScrollableContainer box) return;
        box.ClearItems();
        if (_definitionItem != null) { RemoveInstance(_definitionItem); _definitionItem = null; }
        if (h == null) return;
        bool campaign = h.Kind == SaveKind.Campaign && h.MissionsCount > 0;
        var item = CreateRootWindow(campaign ? "ID_SAVELOAD_CAMPAIGN_DEFINITION_ITEM" : "ID_SAVELOAD_DEFINITION_ITEM", box.Window);
        if (item == null) return;
        _definitionItem = item;
        UiNode? Part(string row, string child) =>
            item.Window.Children.FirstOrDefault(c => c.Name == row) is { } r
            && r.Children.FirstOrDefault(c => c.Name == child) is { } w ? NodeOf(w) : null;
        void Row(string row, string key, string value, bool shown = true)
        {
            if (item.Window.Children.FirstOrDefault(c => c.Name == row) is { } r && NodeOf(r) is { } n) n.Visible = r.Visible = shown;
            if (shown) Part(row, "value")?.SetValue(key, value);
        }
        if (campaign)
        {
            Row("count", "missionscount", h.MissionsCount.ToString());
            Row("campaigndescr", "campaigndescription", h.CampaignDescription ?? "");
        }
        else
        {
            Row("count", "playerscount", h.Players.ToString(), h.Players >= 0);
            Row("gametype", "gametype", (UiGameRoot.RelatedText("game_type", h.Kind == SaveKind.Campaign ? "campain_game_type" : $"game_type_{GameType(h.Kind)}") ?? "").Trim());
            Row("mapsize", "mapsize", (UiGameRoot.RelatedText("map_sizes", $"size{Math.Max(0, h.MapSize)}") ?? "").Trim());
            Row("difficulty", "difficulty", CustomGameScreen.DifficultyName(h.Difficulty), h.Difficulty >= 0);
            Row("mapdescr", "mapdescription", h.MapDescription);
        }
        box.AddItem(item);
    }

    /// <summary>game_type_%d: 0 single mission, 2 LAN, 4 hot-seat (our network games are taken as LAN: UNVERIFIED).</summary>
    static int GameType(SaveKind k) => k switch { SaveKind.Multiplayer => 2, SaveKind.HotSeat => 4, _ => 0 };

    static Texture2D? Png(byte[] png)
    {
        var img = new Image();
        return img.LoadPngFromBuffer(png) == Error.Ok ? ImageTexture.CreateFromImage(img) : null;
    }

    void DoSave()
    {
        if (_game == null) return;
        var name = _edit?.Value.Trim() ?? "";
        if (name.Length == 0) name = SaveManager.QuickName;
        if (SaveSlots.FindByName(SaveManager.Storage, name) != null)
            MessageBoxes.YesNo(this, SaveManager.Text("UI/Common/saveload_resave_header.txt", "Save"),
                SaveManager.Text("UI/Common/saveload_resave.txt", "Do you want to rewrite current save?"), yes => { if (yes) Write(name); });
        else Write(name);
    }

    void Write(string name)
    {
        if (SaveManager.Save(_game!, name, _thumbnail) is { } error)
        {
            MessageBoxes.Message(this, SaveManager.Text("UI/SaveLoad/SaveErrorTitle.txt", "Cannot create a save"), error);
            return;
        }
        _close();
    }

    void DoLoad()
    {
        if (Current is not { } e) return;
        if (_onLoad != null) { _close(); _onLoad(e); return; }
        if (SaveManager.Load(e.FileName) is { } error)
            MessageBoxes.Message(this, SaveManager.Text("UI/SaveLoad/LoadSpecific/load.txt", "Load"), error);
    }

    void DoDelete()
    {
        if (Current is not { } e) return;
        MessageBoxes.YesNo(this, SaveManager.Text("UI/Common/saveload_delete_caption.txt", "Delete save"),
            SaveManager.Text("UI/Common/saveload_delete.txt", "Do you want to delete current save?"), yes =>
            {
                if (!yes) return;
                SaveManager.Storage.Delete(e.FileName);
                Refresh();
            });
    }
}

/// <summary>
/// The saves list rows (UI/SaveLoad/SavesList/Item: 235x40, name over time/date) in the content of the saveslist
/// container: content size = rows × 40, scrolled by the container's bar; the selected / hovered row carries the
/// container's Selection / PreSelection marker (00888da0 → 00887850; selecting does not scroll).
/// </summary>
public partial class SavesListView : Control
{
    const float RowH = 40, RowW = 235;
    readonly string _nameFormat = UiResources.Instance.Text("UI/SaveLoad/SavesList/Item/name.txt") ?? "<body><value=savename>";
    readonly string _dateFormat = UiResources.Instance.Text("UI/SaveLoad/SavesList/Item/description.txt") ?? "<body_bright><value=thour>:<value=tminute> <value=tday>/<value=tmonth>/<value=tyear>";
    List<SaveEntry> _entries = new();
    int _hoverRow = -1;

    public UiScrollableContainer? Container { get; }
    public int? SelectedIndex { get; private set; }
    public event Action<int>? Selected;
    public event Action<int>? Activated;

    public SavesListView(UiScrollableContainer? container)
    {
        Container = container;
        MouseFilter = MouseFilterEnum.Pass;
        TextureFilter = TextureFilterEnum.ParentNode;
        if (container == null) { AnchorRight = 1; AnchorBottom = 1; ClipContents = true; }
    }

    public void SetEntries(List<SaveEntry> entries)
    {
        _entries = entries;
        _hoverRow = -1;
        Size = new Vector2(RowW, entries.Count * RowH);
        if (Container != null) { Container.ScrollPosition = 0; Container.SetContentSize(entries.Count * RowH); Container.Preselect(null); }
        QueueRedraw();
    }

    static Rect2 RowRect(int r) => new(0, r * RowH, RowW, RowH);

    public void Select(int index)
    {
        SelectedIndex = index >= 0 && index < _entries.Count ? index : null;
        Container?.Select(SelectedIndex is int i ? RowRect(i) : null);
        QueueRedraw();
    }

    int RowAt(Vector2 p) => p.X < 0 || p.X > RowW || p.Y < 0 ? -1 : (int)(p.Y / RowH) is var r && r < _entries.Count ? r : -1;

    public override void _Draw()
    {
        for (int r = 0; r < _entries.Count; r++)
        {
            float y = r * RowH;
            var h = _entries[r].Header;
            var t = h.SavedUtc.ToLocalTime();
            var values = new Dictionary<string, string>
            {
                ["savename"] = Escape(h.Name),
                ["thour"] = t.Hour.ToString("00"), ["tminute"] = t.Minute.ToString("00"),
                ["tday"] = t.Day.ToString("00"), ["tmonth"] = t.Month.ToString("00"), ["tyear"] = t.Year.ToString(),
            };
            BitmapTextLayout.Build(_nameFormat, values, new Rect2(0, y, RowW, 22), UiAlign.LowEnd, wrap: false).Draw(this);
            BitmapTextLayout.Build(_dateFormat, values, new Rect2(0, y + 20, RowW - 1, 24), UiAlign.LowEnd, wrap: false).Draw(this);
        }
    }

    static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');

    void Hover(int row)
    {
        if (row == _hoverRow) return;
        _hoverRow = row;
        Container?.Preselect(row >= 0 && row != SelectedIndex ? RowRect(row) : null);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                Hover(RowAt(mm.Position));
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                int hit = RowAt(mb.Position);
                if (hit < 0) break;
                Select(hit);
                Container?.Preselect(null);
                Selected?.Invoke(hit);
                if (mb.DoubleClick) Activated?.Invoke(hit);
                AcceptEvent();
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) Hover(-1);
    }
}

/// <summary>The network game load list (CLoadScreen&lt;SLanSaveHeaderInfo&gt;) opened by Load on UI/MPCreate.</summary>
public static class NetLoadList
{
    public static Control Open(Action<SaveEntry> load)
    {
        SaveLoadScreen? s = null;
        s = new SaveLoadScreen(SaveLoadScreen.Mode.Load, null, null, () => UiNavigator.CloseOverlay(s!), network: true, onLoad: load);
        return s;
    }
}
