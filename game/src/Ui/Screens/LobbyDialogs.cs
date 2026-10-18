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

/// <summary>The stock "Create Game" dialog of the duel lobby.</summary>
public partial class CreateGameDialog : MpdmDialog
{
    public CreateGameDialog()
    {
        if (NameEdit != null) NameEdit.Value = $"{LobbyService.Instance.PlayerName}'s duel";
        if (PasswordEdit != null) PasswordEdit.Password = true;
        // State 0 of the stock check box is "checked"; a locked game needs a password.
        Check?.SetState(1);
        if (Check != null) Check.Clicked += _ => UpdatePassword();
        UpdatePassword();
    }

    void UpdatePassword() { if (PasswordEdit != null) PasswordEdit.Visible = Check?.State == 0; }

    protected override void Confirm()
    {
        var password = Check?.State == 0 ? PasswordEdit?.Value : null;
        LobbyService.Instance.Send(new CreateRoom(NameEdit?.Value ?? "", "duel", 2, string.IsNullOrEmpty(password) ? null : password, null));
        Close();
    }
}

/// <summary>Connect prompt shown on entering the lobby: host on this computer or join an address.</summary>
public partial class ConnectDialog : MpdmDialog
{
    public ConnectDialog()
    {
        Header?.SetText("<h3_header>Local Network");
        Label?.SetText("<body_bright>Server address");
        if (NameEdit != null) NameEdit.Value = "127.0.0.1";
        PasswordEdit!.Visible = false;
        if (Check?.FindChild("*", recursive: false, owned: false) is UiNode checkLabel)
            checkLabel.SetText("<value_name>Host this game");
        if (Check != null) Check.Clicked += _ => UpdateMode();
        CreateButton?.SetText("Connect");
        UpdateMode();
    }

    bool HostMode => Check?.State == 0;

    void UpdateMode()
    {
        if (NameEdit != null) NameEdit.Visible = !HostMode;
        Label?.SetText(HostMode ? "<body_bright>Host a game" : "<body_bright>Server address");
    }

    protected override async void Confirm()
    {
        var lobby = LobbyService.Instance;
        try
        {
            if (HostMode) await lobby.HostAsync();
            else
            {
                var (host, port) = ParseAddress(NameEdit?.Value ?? "");
                await lobby.ConnectAsync(host, port);
            }
            UiNavigator.CloseOverlay(this);
        }
        catch (Exception e)
        {
            GD.Print($"[UI] connect failed: {e.Message}");
            Label?.SetText(HostMode ? "<body_bright><color=FFFF6040>Could not start the server" : "<body_bright><color=FFFF6040>Could not connect");
        }
    }

    protected override void Close()
    {
        UiNavigator.CloseOverlay(this);
        UiNavigator.Goto("multiplayer");
    }

    public static (string host, int port) ParseAddress(string s)
    {
        s = s.Trim();
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p)) return (s[..colon], p);
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
