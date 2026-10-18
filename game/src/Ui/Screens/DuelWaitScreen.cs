using System.Text.Json.Nodes;
using Godot;
using H5.Net;
using H5Reborn.Ui.Net;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// UI/MPDMWait, "Duel options": both players' portraits and names, the presets
/// table, room chat, and Start Game (host) / Ready (guest) / Cancel. Backed by the
/// lobby server's room state; preset picks travel as per-player options.
/// </summary>
public partial class DuelWaitScreen : UiScreen
{
    readonly ChatLogView _log = new();
    readonly PresetGridView? _grid;
    readonly TextureRect _hostFace = Face(), _clientFace = Face();
    bool _autoStartPending = DevArgs.AutoStart;

    public DuelWaitScreen() : base("UI/MPDMWait.(WindowScreen).xdb")
    {
        foreach (var n in new[] { "tripleView_host", "tripleView_client", "time_pressing", "time_pressing_text", "ComboBoxPanel", "dndicon" })
            SetVisible(n, false);
        if (Find("combatturnspeed_text")?.GetParent() is Control speed) speed.Visible = false;
        if (Find("locked") is { } locked) locked.Enabled = false;

        if (Find("presets_grid") is { } gridNode && Root.Find("line_template") is { } template && Root.Find("presets_table_back") is { } back)
        {
            _grid = new PresetGridView(template, back.Rect.H);
            _grid.Position = new Vector2(template.Rect.X - gridNode.Window.Rect.X, template.Rect.Y - gridNode.Window.Rect.Y);
            gridNode.AddChild(_grid);
            _grid.Picked += Pick;
        }
        Find("host_info/icon")?.AddChild(_hostFace);
        Find("client_info/icon")?.AddChild(_clientFace);
        Find("MPCommonChatLog")?.AddChild(_log);
        if (Find("chatinput") is UiEditLine chat)
            chat.Submitted += text =>
            {
                if (text.Trim().Length == 0) return;
                LobbyService.Instance.Send(new Chat(0, text));
                chat.Value = "";
            };

        Bus.On("start", StartOrReady);
        Bus.On("back", Leave);
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
        var lobby = LobbyService.Instance;
        lobby.MessageReceived += OnMessage;
        lobby.Disconnected += OnDisconnected;
        if (DevArgs.Preset is { } p) { DevArgs.Preset = null; Pick(p); }
        UpdateRoom();
    }

    public override void _ExitTree()
    {
        LobbyService.Instance.MessageReceived -= OnMessage;
        LobbyService.Instance.Disconnected -= OnDisconnected;
    }

    void OnDisconnected(string reason)
    {
        if (UiNavigator.Current == this) UiNavigator.Goto("lobby");
    }

    RoomPlayer? Me => LobbyService.Instance.Room?.Players.FirstOrDefault(p => p.PlayerId == LobbyService.Instance.PlayerId);
    bool IsHost => Me?.IsHost ?? false;

    static int? PresetOf(RoomPlayer? p) => p?.Options?["preset"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    void OnMessage(Message m)
    {
        switch (m)
        {
            case RoomState: UpdateRoom(); break;
            case Chat c:
                var from = LobbyService.Instance.Room?.Players.FirstOrDefault(p => p.PlayerId == c.FromPlayerId)?.Name ?? $"#{c.FromPlayerId}";
                _log.AddChat(from, c.Text);
                break;
            case ErrorMsg e: _log.AddSystem(e.Text); break;
            case GameStarted gs:
                GD.Print($"[UI] GameStarted side={gs.YourSide} presets={gs.Setup["presets"]?.ToJsonString()}");
                if (LobbyService.Instance.Combat is { } combat)
                    Callable.From(() => Combat.CombatScreen.Open(combat)).CallDeferred();
                break;
            case GameEnded ge: _log.AddSystem($"Game ended: {ge.Reason}"); break;
        }
    }

    void UpdateRoom()
    {
        var room = LobbyService.Instance.Room;
        if (room == null) return;
        Find("game_name")?.SetValue("name", ChatLogView.Escape(room.Name));
        var host = room.Players.FirstOrDefault(p => p.IsHost);
        var client = room.Players.FirstOrDefault(p => !p.IsHost);
        Find("host_info/info")?.SetValue("name", ChatLogView.Escape(host?.Name ?? ""));
        Find("client_info/info")?.SetValue("name", ChatLogView.Escape(client?.Name ?? UiResources.Instance.Text("UI/MPDMWait/waiting.txt") ?? ""));
        _hostFace.Texture = PresetFace(host, left: true);
        _clientFace.Texture = PresetFace(client, left: false);
        SetVisible("host_highlight", PresetOf(host) != null);
        SetVisible("client_highlight", client?.Ready ?? false);

        if (_grid != null)
        {
            _grid.Mine = PresetOf(Me);
            _grid.Theirs = PresetOf(room.Players.FirstOrDefault(p => p.PlayerId != LobbyService.Instance.PlayerId));
            _grid.Refresh();
        }
        if (Find("creategamebutton") is { } btn)
            btn.SetText(IsHost ? null : Me?.Ready == true ? "Not Ready" : "Ready");

        if (!IsHost && DevArgs.Ready && PresetOf(Me) != null && Me?.Ready == false)
        {
            DevArgs.Ready = false;
            LobbyService.Instance.Send(new SetReady(true));
        }
        if (IsHost && _autoStartPending && room.Players.Count == 2 && room.Players.All(p => p.IsHost || p.Ready) && !room.InProgress)
        {
            _autoStartPending = false;
            StartOrReady();
        }
    }

    static Texture2D? PresetFace(RoomPlayer? p, bool left)
    {
        if (PresetOf(p) is not { } i || i < 0 || i >= DuelPresetList.All.Count) return null;
        var preset = DuelPresetList.All[i];
        return UiResources.Instance.Texture(left ? preset.LeftFace : preset.RightFace);
    }

    void Pick(int index)
    {
        LobbyService.Instance.Send(new SetPlayerOptions(new JsonObject { ["preset"] = index }));
        if (index < DuelPresetList.All.Count)
        {
            var p = DuelPresetList.All[index];
            _log.AddSystem(p.HeroName.Length > 0 ? $"{p.Name}: {p.HeroName}, level {p.Level}, {p.Stacks} stacks" : $"{p.Name}: level {p.Level}, {p.Stacks} stacks");
        }
    }

    void StartOrReady()
    {
        var room = LobbyService.Instance.Room;
        if (room == null) return;
        if (!IsHost)
        {
            if (PresetOf(Me) == null) { _log.AddSystem("Choose a hero first."); return; }
            LobbyService.Instance.Send(new SetReady(!(Me?.Ready ?? false)));
            return;
        }
        if (room.Players.Count < 2) { _log.AddSystem("Waiting for an opponent."); return; }
        if (room.Players.Any(p => PresetOf(p) == null)) { _log.AddSystem("Both players must choose a hero."); return; }
        LobbyService.Instance.Send(new StartGame());
    }

    void Leave()
    {
        LobbyService.Instance.LeaveRoom();
        UiNavigator.Goto("lobby");
    }
}
