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
    readonly DefinitionView _definition = new();
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
        _list = new SavesListView();
        Find("saveslist")?.AddChild(_list);
        Find("screenshoot")?.AddChild(_shot);
        Find("definition")?.AddChild(_definition);
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
        _definition.Show(h);
        if (Find("delete") is { } del) del.Enabled = e != null;
        if (Find("load") is { } load) load.Enabled = e != null;
    }

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

/// <summary>The saves list rows (UI/SaveLoad/SavesList/Item: 235x40, name over time/date).</summary>
public partial class SavesListView : Control
{
    const float RowH = 40, RowW = 235;
    readonly string _nameFormat = UiResources.Instance.Text("UI/SaveLoad/SavesList/Item/name.txt") ?? "<body><value=savename>";
    readonly string _dateFormat = UiResources.Instance.Text("UI/SaveLoad/SavesList/Item/description.txt") ?? "<body_bright><value=thour>:<value=tminute> <value=tday>/<value=tmonth>/<value=tyear>";
    readonly Texture2D? _hover = UiResources.Instance.Texture("UI/SaveLoad/Preselection.(Texture).dds");
    readonly Texture2D? _selected = UiResources.Instance.Texture("UI/SaveLoad/Selection.(Texture).dds");
    List<SaveEntry> _entries = new();
    int _hoverRow = -1, _scroll;

    public int? SelectedIndex { get; private set; }
    public event Action<int>? Selected;
    public event Action<int>? Activated;

    public SavesListView()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.ParentNode;
    }

    public void SetEntries(List<SaveEntry> entries) { _entries = entries; _scroll = 0; QueueRedraw(); }

    public void Select(int index)
    {
        SelectedIndex = index >= 0 && index < _entries.Count ? index : null;
        if (SelectedIndex is int i)
        {
            int visible = Math.Max(1, (int)(Size.Y / RowH));
            if (i < _scroll) _scroll = i;
            else if (i >= _scroll + visible) _scroll = i - visible + 1;
        }
        QueueRedraw();
    }

    int RowAt(Vector2 p) => p.X < 0 || p.X > RowW || p.Y < 0 ? -1 : _scroll + (int)(p.Y / RowH) is var r && r < _entries.Count ? r : -1;

    public override void _Draw()
    {
        for (int r = _scroll; r < _entries.Count; r++)
        {
            float y = (r - _scroll) * RowH;
            if (y >= Size.Y) break;
            var rect = new Rect2(0, y, RowW, RowH);
            if (r == SelectedIndex && _selected != null) DrawTextureRect(_selected, rect, false);
            else if (r == _hoverRow && _hover != null) DrawTextureRect(_hover, rect, false);
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

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                int row = RowAt(mm.Position);
                if (row != _hoverRow) { _hoverRow = row; QueueRedraw(); }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                int hit = RowAt(mb.Position);
                if (hit < 0) break;
                Select(hit);
                Selected?.Invoke(hit);
                if (mb.DoubleClick) Activated?.Invoke(hit);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _scroll = Math.Max(0, _scroll - 1); QueueRedraw(); AcceptEvent(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _scroll = Math.Clamp(_scroll + 1, 0, Math.Max(0, _entries.Count - (int)(Size.Y / RowH))); QueueRedraw(); AcceptEvent(); break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && _hoverRow != -1) { _hoverRow = -1; QueueRedraw(); }
    }
}

/// <summary>SaveInfo's definition rows (UI/SaveLoad/SaveInfo/Definition texts).</summary>
public partial class DefinitionView : Control
{
    const string Dir = "UI/SaveLoad/SaveInfo/Definition/";
    readonly List<(string Name, string Value)> _rows = new();
    string _descHeader = "", _desc = "";

    public DefinitionView()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.ParentNode;
    }

    static string T(string file) => UiResources.Instance.Text(Dir + file) ?? "";

    static string V(string format, string key, string value) => format.Replace($"<value={key}>", value, StringComparison.OrdinalIgnoreCase);

    public void Show(SaveHeader? h)
    {
        _rows.Clear();
        _descHeader = _desc = "";
        if (h != null)
        {
            if (h.MapSize >= 0) _rows.Add((T("mapsizename.txt"), V(T("mapsizevalue.txt"), "mapsize", T($"mapsize{h.MapSize}.txt"))));
            _rows.Add((T("playerscountname.txt"), V(T("playerscountvalue.txt"), "playerscount", h.Players.ToString())));
            _rows.Add((T("difficultyname.txt"), V(T("difficultyvalue.txt"), "difficulty", Difficulty(h.Difficulty))));
            if (h.Kind == SaveKind.Campaign && h.MissionsCount > 0)
            {
                _rows.Add((T("missionscountname.txt"), V(T("missionscountvalue.txt"), "missionscount", h.MissionsCount.ToString())));
                _descHeader = T("campaigndescriptionname.txt");
                _desc = V(T("campaigndescriptionvalue.txt"), "campaigndescription", h.CampaignDescription ?? "");
            }
            else
            {
                _descHeader = T("mapdescriptionname.txt");
                _desc = V(T("mapdescriptionvalue.txt"), "mapdescription", h.MapDescription);
            }
        }
        QueueRedraw();
    }

    static string Difficulty(int d)
    {
        var names = new[] { "Easy", "Normal", "Hard", "Heroic" };
        int i = Math.Clamp(d, 0, 3);
        return UiResources.Instance.Text($"GameMechanics/RefTables/UIDifficultyInfo/Difficulty{names[i]}Name.txt")?.Trim() ?? names[i];
    }

    public override void _Draw()
    {
        // Leave room for the container's scroll bar on the right (definition ScrollBar).
        float y = 0, w = Size.X - 20;
        foreach (var (name, value) in _rows)
        {
            BitmapTextLayout.Build(name, null, new Rect2(0, y, w, 20), UiAlign.LowEnd, wrap: false).Draw(this);
            BitmapTextLayout.Build(value, null, new Rect2(0, y, w, 20), UiAlign.LowEnd, wrap: false).Draw(this);
            y += 20;
        }
        if (_descHeader.Length == 0) return;
        y += 6;
        BitmapTextLayout.Build(_descHeader, null, new Rect2(0, y, w, 22), UiAlign.LowEnd, wrap: false).Draw(this);
        y += 22;
        BitmapTextLayout.Build(_desc, null, new Rect2(0, y, w, Math.Max(20, Size.Y - y)), UiAlign.LowEnd, wrap: true).Draw(this);
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
