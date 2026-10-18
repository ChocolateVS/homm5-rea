using Godot;
using H5.Net;
using H5Reborn.Ui.Net;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// Dialogs built on the original UI/MPDMCreate window (parchment panel, edit
/// lines, check boxes, Create/Cancel buttons). The stock create-game dialog is
/// used as is; connect and player-name prompts reuse its parts with new labels.
/// </summary>
public abstract partial class MpdmDialog : UiScreen
{
    protected readonly UiNode? Header, Label;
    protected readonly UiEditLine? NameEdit, PasswordEdit;
    protected readonly UiNode? Check, CreateButton;

    protected MpdmDialog() : base("UI/MPDMCreate.(WindowScreen).xdb")
    {
        var statics = Root.Descendants().Where(w => w.Type == "WindowStaticText").Select(Node).ToList();
        Header = statics.ElementAtOrDefault(0);
        Label = statics.ElementAtOrDefault(1);
        NameEdit = Find("name") as UiEditLine;
        PasswordEdit = Find("password") as UiEditLine;
        Check = Find("lockedgame");
        CreateButton = Find("create");
        foreach (var n in new[] { "realtime", "triple_heroes", "CustomPresetsPanel" }) SetVisible(n, false);
        if (Find("combatturnspeed_text")?.GetParent() is Control speed) speed.Visible = false;
        Bus.On("cancel", Close);
        Bus.On("create", Confirm);
        NameEdit?.CallDeferred(Control.MethodName.GrabFocus);
        if (NameEdit != null) NameEdit.Submitted += _ => Confirm();
    }

    protected abstract void Confirm();

    protected virtual void Close() => UiNavigator.CloseOverlay(this);

    protected void HideCheck()
    {
        if (Check == null) return;
        Check.Visible = false;
        if (PasswordEdit != null) PasswordEdit.Visible = false;
    }
}

/// <summary>
/// The stock "Create Game" dialog of the duel lobby. When this computer is the host and an
/// online server is configured, the stock "Autosave" box becomes "List online (join code)".
/// </summary>
public partial class CreateGameDialog : MpdmDialog
{
    readonly UiNode? _online;

    public CreateGameDialog()
    {
        var lobby = LobbyService.Instance;
        if (NameEdit != null) NameEdit.Value = $"{lobby.PlayerName}'s duel";
        if (PasswordEdit != null) PasswordEdit.Password = true;
        // State 0 of the stock check box is "checked"; a locked game needs a password.
        Check?.SetState(1);
        if (Check != null) Check.Clicked += _ => UpdatePassword();
        UpdatePassword();
        _online = Find("realtime");
        if (_online != null && lobby.IsHosting && lobby.OnlineCode == null && !string.IsNullOrWhiteSpace(lobby.Settings.MasterUrl))
        {
            if (_online.FindChild("*", recursive: false, owned: false) is UiNode label) label.SetText("<value_name>List online (join code)");
            _online.SetState(1);
            _online.Visible = true;
        }
    }

    void UpdatePassword() { if (PasswordEdit != null) PasswordEdit.Visible = Check?.State == 0; }

    protected override async void Confirm()
    {
        var lobby = LobbyService.Instance;
        var password = Check?.State == 0 ? PasswordEdit?.Value : null;
        lobby.Send(new CreateRoom(NameEdit?.Value ?? "", "duel", 2, string.IsNullOrEmpty(password) ? null : password, null));
        Close();
        if (_online is { Visible: true, State: 0 })
        {
            try { await lobby.PublishOnlineAsync(!string.IsNullOrEmpty(password)); }
            catch (Exception e) { GD.Print($"[UI] publish failed: {e.Message}"); }
        }
    }
}

/// <summary>Password prompt for joining a locked game room.</summary>
public partial class PasswordDialog : MpdmDialog
{
    readonly int _roomId;

    public PasswordDialog(int roomId)
    {
        _roomId = roomId;
        Header?.SetText("<h3_header>Locked Game");
        Label?.SetText("<body_bright>Password");
        if (NameEdit != null) { NameEdit.Value = ""; NameEdit.Password = true; }
        HideCheck();
        CreateButton?.SetText("Join");
    }

    protected override void Confirm()
    {
        LobbyService.Instance.Send(new JoinRoom(_roomId, NameEdit?.Value ?? ""));
        Close();
    }
}

/// <summary>
/// Connect prompt built from the stock create-game window parts. One field takes an
/// IP address ("192.168.1.20", "host:27615", "[fe80::1]:27615"), a WebSocket URL
/// ("wss://games.example.com") or a 6-letter online join code. "Host this game" hosts
/// on this computer instead (LAN-visible; optionally listed online with a join code).
/// The second edit line holds the online (master) server used for codes and listing.
/// </summary>
public partial class ConnectDialog : MpdmDialog
{
    readonly UiNode? _online;
    readonly BitmapText _serverLabel = new() { MouseFilter = MouseFilterEnum.Ignore };

    public ConnectDialog(string? prefill = null)
    {
        var lobby = LobbyService.Instance;
        Header?.SetText("<h3_header>Connect");
        if (NameEdit != null) NameEdit.Value = prefill ?? "";
        if (PasswordEdit != null)
        {
            PasswordEdit.Password = false;
            PasswordEdit.Value = lobby.Settings.MasterUrl ?? "";
            PasswordEdit.Visible = true;
            _serverLabel.Markup = "<right><value_name>Online server:";
            PasswordEdit.GetParent()?.AddChild(_serverLabel);
            // Place it after layout, just above the edit line.
            void Place()
            {
                // Right-aligned on the check box row, clear of its "Host this game" label.
                _serverLabel.Position = PasswordEdit.Position + new Vector2(PasswordEdit.Size.X - 180, -21);
                _serverLabel.Size = new Vector2(180, 18);
            }
            PasswordEdit.Resized += Place;
            Callable.From(Place).CallDeferred();
        }
        if (Check?.FindChild("*", recursive: false, owned: false) is UiNode checkLabel)
            checkLabel.SetText("<value_name>Host this game");
        Check?.SetState(1);
        if (Check != null) Check.Clicked += _ => UpdateMode();
        // The stock "Autosave" check box becomes "List online": publish the hosted game with a join code.
        _online = Find("realtime");
        if (_online != null)
        {
            if (_online.FindChild("*", recursive: false, owned: false) is UiNode onlineLabel)
                onlineLabel.SetText("<value_name>List online (join code)");
            _online.SetState(1);
        }
        CreateButton?.SetText("Connect");
        UpdateMode();
    }

    bool HostMode => Check?.State == 0;
    bool ListOnline => _online?.State == 0;

    void UpdateMode()
    {
        if (NameEdit != null) NameEdit.Visible = !HostMode;
        if (_online != null) _online.Visible = HostMode;
        Label?.SetText(HostMode ? "<body_bright>Host a game on this computer" : "<body_bright>Address, URL or join code");
        CreateButton?.SetText(HostMode ? "Host" : "Connect");
    }

    /// <summary>True for strings that look like an online join code rather than an address.</summary>
    public static bool IsJoinCode(string s)
    {
        var c = H5.Net.Master.JoinCodes.Normalize(s);
        return c.Length == H5.Net.Master.JoinCodes.Length && c.All(ch => H5.Net.Master.JoinCodes.Alphabet.Contains(ch)) && !s.Contains('.');
    }

    protected override async void Confirm()
    {
        var lobby = LobbyService.Instance;
        var server = PasswordEdit?.Value?.Trim() ?? "";
        if (server != (lobby.Settings.MasterUrl ?? "")) lobby.SetMasterUrl(server);
        var input = NameEdit?.Value?.Trim() ?? "";
        try
        {
            if (HostMode) await lobby.HostAsync(publishOnline: ListOnline);
            else if (IsJoinCode(input)) await lobby.JoinOnlineAsync(input);
            else if (input.Length == 0) { Label?.SetText("<body_bright><color=FFFF6040>Enter an address or a join code"); return; }
            else
            {
                var (host, port) = ParseAddress(input);
                await lobby.ConnectAsync(host, port);
            }
            UiNavigator.CloseOverlay(this);
        }
        catch (Exception e)
        {
            GD.Print($"[UI] connect failed: {e.Message}");
            Label?.SetText($"<body_bright><color=FFFF6040>{ChatLogView.Escape(e.Message)}");
        }
    }

    public static (string host, int port) ParseAddress(string s)
    {
        s = s.Trim();
        if (s.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return (s, 0);
        if (s.StartsWith('['))
        {
            int end = s.IndexOf(']');
            if (end > 0)
                return (s[..(end + 1)], end + 2 < s.Length && s[end + 1] == ':' && int.TryParse(s[(end + 2)..], out var p6) ? p6 : ProtocolInfo.DefaultPort);
        }
        int colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var p)) return (s[..colon], p);
        return (s.Length > 0 ? s : "127.0.0.1", ProtocolInfo.DefaultPort);
    }
}

/// <summary>"Player Name" prompt (the lobby's changename reaction).</summary>
public partial class PlayerNameDialog : MpdmDialog
{
    public PlayerNameDialog()
    {
        Header?.SetText("<h3_header>Player Name");
        if (NameEdit != null) NameEdit.Value = LobbyService.Instance.PlayerName;
        HideCheck();
        CreateButton?.SetText("OK");
    }

    protected override void Confirm()
    {
        LobbyService.Instance.SetPlayerName(NameEdit?.Value ?? "");
        Close();
    }
}
