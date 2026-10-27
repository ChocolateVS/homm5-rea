using Godot;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;
using H5Reborn.Ui.Net;

namespace H5Reborn.Combat;

/// <summary>
/// Hot-seat duel setup on the stock "Duel options" window (UI/MPDMWait) without a
/// network room: both players sit at this computer and pick their presets from the
/// original 24-preset grid in turn (player 1, then player 2; further picks alternate),
/// then Start Game opens the combat with both sides controlled locally.
/// </summary>
public partial class HotseatDuelScreen : UiScreen
{
    readonly ChatLogView _log = new();
    readonly PresetGridView? _grid;
    readonly TextureRect _face1 = Face(), _face2 = Face();
    int? _p1, _p2;
    int _next;   // 0 = player 1 picks next, 1 = player 2

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
        Bus.On("start", Start);
        Bus.On("back", () => UiNavigator.Goto("mainmenu"));
    }

    static TextureRect Face() => new()
    {
        AnchorRight = 1, AnchorBottom = 1,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    public override void _Ready()
    {
        base._Ready();
        Find("game_name")?.SetValue("name", "Hot-Seat Duel");
        _log.AddSystem("Player 1: choose a hero.");
        Refresh();
    }

    void Pick(int index)
    {
        if (_next == 0) _p1 = index; else _p2 = index;
        if (index < DuelPresetList.All.Count)
        {
            var p = DuelPresetList.All[index];
            _log.AddSystem($"Player {_next + 1}: {p.Name}" + (p.HeroName.Length > 0 ? $" ({p.HeroName}, level {p.Level})" : ""));
        }
        _next = _p2 == null ? 1 : 1 - _next;
        if (_p2 == null) _log.AddSystem("Player 2: choose a hero.");
        Refresh();
    }

    void Refresh()
    {
        Find("host_info/info")?.SetValue("name", "Player 1");
        Find("client_info/info")?.SetValue("name", "Player 2");
        _face1.Texture = FaceOf(_p1, left: true);
        _face2.Texture = FaceOf(_p2, left: false);
        SetVisible("host_highlight", _p1 != null);
        SetVisible("client_highlight", _p2 != null);
        if (_grid != null) { _grid.Mine = _p1; _grid.Theirs = _p2; _grid.Refresh(); }
    }

    static Texture2D? FaceOf(int? i, bool left)
    {
        if (i is not int p || p < 0 || p >= DuelPresetList.All.Count) return null;
        return UiResources.Instance.Texture(left ? DuelPresetList.All[p].LeftFace : DuelPresetList.All[p].RightFace);
    }

    void Start()
    {
        if (_p1 is not int a || _p2 is not int b) { _log.AddSystem("Both players must choose a hero."); return; }
        var seed = (ulong)Time.GetTicksUsec();
        GD.Print($"[H5] hot-seat duel {a} vs {b}, seed {seed}");
        CombatScreen.Open(new LocalCombatSession(Game.Instance.Db, a, b, seed, new[] { 0, 1 }));
    }
}
