using Godot;
using H5.Core.Ui;
using H5Reborn.Options;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn.Profiles;

/// <summary>
/// Drives the main menu's profile parts (UI/MainMenu2): the SetupPanel's profile name,
/// the ChangeProfile panel (New Profile edit line + Create, Available Profiles list,
/// Delete with UI/MainMenu2/DeleteProfileQuestion, Select), the first-run name prompt
/// (UI/SimpleEditBox titled with UI/SetFirstProfileName/header.txt, as
/// ID_SET_FIRST_PROFILE_NAME_SCREEN does when no profile exists), and the Load Game and
/// Hall of Fame entries.
/// </summary>
public static class ProfileMenu
{
    public static void Attach(MainMenuScreen menu)
    {
        ProfileListView? list = null;

        void UpdateNames()
        {
            foreach (var view in menu.FindAll("ProfileNameView")) view.SetText("<h3><color_title>" + Escape(ProfileManager.CurrentName));
        }

        void ShowPanel()
        {
            menu.SetVisible("SinglePlayer/ChangeProfile", true);
            if (list == null && menu.Find("SinglePlayer/ChangeProfile/ProfileList") is { } host)
            {
                list = new ProfileListView();
                host.AddChild(list);
                list.Activated += _ => Select();
            }
            list?.SetNames(ProfileManager.List(), ProfileManager.CurrentName);
        }

        void Select()
        {
            if (list?.SelectedName is not { } name) return;
            ProfileManager.Select(name);
            OptionsStore.Reload();
            UpdateNames();
            menu.SetVisible("SinglePlayer/ChangeProfile", false);
        }

        menu.Bus.On("change_profile", ShowPanel);
        menu.Bus.On("profile_select", Select);
        menu.Bus.On("profile_select_dbl_click", Select);
        menu.Bus.On("profile_create", () =>
        {
            var edit = menu.Find("SinglePlayer/ChangeProfile/ProfileNameEdit") as UiEditLine;
            var name = edit?.Value.Trim() ?? "";
            if (ProfileManager.Validate(name) is { } problem)
            {
                MessageBoxes.Message(menu, UiResources.Instance.Text("UI/MainMenu2/ChangeProfile/Create.txt") ?? "Create",
                    problem == "exists" ? "A profile with this name already exists." : "Please enter a valid profile name.");
                return;
            }
            ProfileManager.Create(name);
            if (edit != null) edit.Value = "";
            list?.SetNames(ProfileManager.List(), name);
        });
        menu.Bus.On("profile_delete", () =>
        {
            if (list?.SelectedName is not { } name) return;
            MessageBoxes.YesNo(menu, UiResources.Instance.Text("UI/MainMenu2/DeleteProfileTitle.txt") ?? "Delete profile",
                UiResources.Instance.Text("UI/MainMenu2/DeleteProfileQuestion.txt") ?? "Do you really want to delete the selected profile?", yes =>
                {
                    if (!yes) return;
                    ProfileManager.Delete(name);
                    OptionsStore.Reload();
                    UpdateNames();
                    list.SetNames(ProfileManager.List(), ProfileManager.CurrentName);
                });
        });

        if (!UiNavigator.Has("load"))
            UiNavigator.Register("load", () => new Save.SaveLoadScreen(Save.SaveLoadScreen.Mode.Load, null, null, () => UiNavigator.Goto("mainmenu")));
        if (!UiNavigator.Has("halloffame"))
            UiNavigator.Register("halloffame", () => new HallOfFameScreen(() => UiNavigator.Goto("mainmenu")));
        menu.Bus.On("load_game", () => UiNavigator.Goto("load"));
        menu.Bus.On("hall_of_fame", () => UiNavigator.Goto("halloffame"));

        UpdateNames();
        OptionsStore.Apply();
        if (ProfileManager.NoProfiles) Callable.From(() => AskFirstName(menu, UpdateNames)).CallDeferred();

        // Dev/test: `--profile-dev=profiles|halloffame|load|firstname` opens that part once the menu exists.
        if (!_devUsed && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--profile-dev=")) is { } dev)
        {
            _devUsed = true;
            Callable.From(() =>
            {
                switch (dev["--profile-dev=".Length..])
                {
                    case "profiles": menu.ShowOnly("SinglePlayer", "Main", "SinglePlayer", "MultiPlayer"); ShowPanel(); break;
                    case "halloffame": UiNavigator.Goto("halloffame"); break;
                    case "load": UiNavigator.Goto("load"); break;
                    case "firstname": AskFirstName(menu, UpdateNames); break;
                }
            }).CallDeferred();
        }
    }

    static bool _devUsed;

    /// <summary>The first-run prompt: a name is required before playing.</summary>
    static void AskFirstName(Control parent, Action done)
    {
        var box = new UiScreen("UI/SimpleEditBox.(WindowScreen).xdb") { MouseFilter = Control.MouseFilterEnum.Stop, Name = "FirstProfile" };
        box.Find("header")?.SetValue("value", UiResources.Instance.Text("UI/SetFirstProfileName/header.txt")?.Trim() ?? "Enter profile name");
        if (box.Find("canceledit") is { } cancel) cancel.Visible = false;
        var edit = box.Find("editname") as UiEditLine;
        if (edit != null) { edit.MaxLength = ProfileManager.MaxNameLength; edit.Value = ProfileManager.DefaultName; }
        void Ok()
        {
            var name = edit?.Value.Trim() ?? "";
            if (ProfileManager.Validate(name) != null) return;
            ProfileManager.Create(name);
            ProfileManager.Select(name);
            OptionsStore.Reload();
            box.QueueFree();
            done();
        }
        box.Bus.On("setname", Ok);
        if (edit != null) edit.Submitted += _ => Ok();
        parent.AddChild(box);
        edit?.GrabFocus();
    }

    static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');
}

/// <summary>Rows of the Available Profiles list (UI/MainMenu2/ChangeProfile/ProfileListItem.txt, selection textures).</summary>
public partial class ProfileListView : Control
{
    const float HeaderH = 20, RowH = 20;
    readonly string _format = UiResources.Instance.Text("UI/MainMenu2/ChangeProfile/ProfileListItem.txt") ?? "<body>";
    readonly Texture2D? _sel = UiResources.Instance.Texture("UI/MainMenu2/ChangeProfile/ProfileListSelection.(Texture).dds");
    readonly Texture2D? _pre = UiResources.Instance.Texture("UI/MainMenu2/ChangeProfile/ProfileListPreselection.(Texture).dds");
    List<string> _names = new();
    int _selected = -1, _hover = -1;

    public string? SelectedName => _selected >= 0 && _selected < _names.Count ? _names[_selected] : null;
    public event Action<int>? Activated;

    public ProfileListView()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.ParentNode;
    }

    public void SetNames(List<string> names, string? select)
    {
        _names = names;
        _selected = select == null ? -1 : names.FindIndex(n => n.Equals(select, StringComparison.OrdinalIgnoreCase));
        QueueRedraw();
    }

    int RowAt(Vector2 p) => p.Y < HeaderH ? -1 : (int)((p.Y - HeaderH) / RowH) is var r && r < _names.Count ? r : -1;

    public override void _Draw()
    {
        for (int i = 0; i < _names.Count; i++)
        {
            var rect = new Rect2(0, HeaderH + i * RowH, Size.X - 30, RowH);
            if (i == _selected && _sel != null) DrawTextureRect(_sel, rect, false);
            else if (i == _hover && _pre != null) DrawTextureRect(_pre, rect, false);
            BitmapTextLayout.Build(_format + "<center>" + _names[i].Replace('<', '‹').Replace('>', '›'), null, rect, UiAlign.Center, wrap: false).Draw(this);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm when RowAt(mm.Position) != _hover:
                _hover = RowAt(mm.Position); QueueRedraw(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when RowAt(mb.Position) is var r && r >= 0:
                _selected = r; QueueRedraw();
                if (mb.DoubleClick) Activated?.Invoke(r);
                AcceptEvent();
                break;
        }
    }
}
