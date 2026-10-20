using Godot;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn.Campaign;

/// <summary>
/// Drives the main menu's campaign panels (UI/MainMenu2 SelectCampaign / SelectMission):
/// Single Player → Campaign shows the SelectCampaign "MenuPanel" (the standard set's six
/// campaign blocks; MenuPanelNN are the user-campaign variants), each block
/// showing the campaign name on its button and the hero (NameComment) below; locked
/// campaigns (dependencies not completed) are disabled. Choosing a campaign shows its
/// missions the same way (completed missions and the next one are enabled), and choosing
/// a mission opens the PWL screen.
/// </summary>
public static class CampaignMenu
{
    /// <summary>Set before switching to the main menu to land on the campaign list.</summary>
    public static bool OpenOnStart;

    static readonly string[] Panels =
    {
        "Main", "SinglePlayer", "MultiPlayer", "SelectCampaign", "SelectMission", "HotSeatSubMenu", "LoadReplay",
    };

    public static void Attach(MainMenuScreen menu)
    {
        H5.Assets.Scenes.CampaignDef? current = null;
        var profile = CampaignProfile.Current;

        void ShowCampaigns()
        {
            menu.ShowOnly("SelectCampaign", Panels);
            var list = CampaignFlow.Campaigns;
            var select = menu.Find("SelectCampaign")!;
            // 00699dc0 / 0069a600: the standard campaign set uses the single "MenuPanel" (6 blocks, no
            // Load Campaign); MenuPanel<count+1> belongs to the user-campaign list. 0069a290 takes the
            // panel's CampaignBlock01.. children; block i shows campaign i while i < the set's count.
            const string panelName = "MenuPanel";
            foreach (var child in select.GetChildren())
                if (child is UiNode n && n.Name.ToString().StartsWith("MenuPanel")) n.Visible = n.Name == panelName;
            var panel = menu.Find($"SelectCampaign/{panelName}");
            if (panel == null) return;
            for (int i = 0; ; i++)
            {
                var block = Child(panel, $"CampaignBlock{i + 1:00}");
                if (block == null) break;
                if (i >= list.Count) { block.Visible = false; continue; }
                var c = list[i];
                block.Visible = true;
                var btn = Child(block, "SelectCampaignBtn");
                btn?.SetText(c.Name);
                btn?.SetMeta("campaign", c.InternalName);
                if (btn != null) btn.Enabled = CampaignFlow.IsUnlocked(c, profile);
                Child(block, "CampaignDescView")?.SetText("<menu_small>" + c.Comment);
            }
            UpdateSetup(menu, profile);
            ApplyVisualStates(panel);
            // Dev (screenshots): `--campaign-tab=Btn_Campaign_H5A2` clicks that tab.
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--campaign-tab=")) is { } tab && Child(panel, tab[15..]) is { } tabBtn)
                tabBtn.Activate();
        }

        void ShowMissions(H5.Assets.Scenes.CampaignDef c)
        {
            current = c;
            menu.ShowOnly("SelectMission", Panels);
            menu.Find("SelectMission/CampaignNameView")?.SetText("<h2><color_header><outline>" + c.FullName);
            var panel = menu.Find("SelectMission/MenuPanel");
            if (panel == null) return;
            for (int i = 0; i < 5; i++)
            {
                var block = Child(panel, $"MissionBlock{i + 1:00}");
                if (block == null) continue;
                if (i >= c.Missions.Count) { block.Visible = false; continue; }
                var m = c.Missions[i];
                block.Visible = true;
                var btn = Child(block, "SelectMissionBtn");
                btn?.SetText(m.Name);
                btn?.SetMeta("mission", i);
                if (btn != null) btn.Enabled = CampaignFlow.IsMissionAvailable(c, i, profile);
                Child(block, "MissionDescView")?.SetText("<menu_small>" + m.Description);
            }
            UpdateSetup(menu, profile);
        }

        menu.Bus.On("campaign", ShowCampaigns);
        menu.Bus.On("select_campaign_back", () => menu.ShowOnly("SinglePlayer", Panels));
        menu.Bus.On("select_mission_back", ShowCampaigns);
        menu.Bus.On("campaign_selected", (_, node) =>
        {
            if (node?.HasMeta("campaign") == true && CampaignFlow.Find(node.GetMeta("campaign").AsString()) is { } c) ShowMissions(c);
        });
        menu.Bus.On("mission_selected", (_, node) =>
        {
            if (current == null || node?.HasMeta("mission") != true) return;
            CampaignFlow.OpenPwl(menu, current.InternalName, node.GetMeta("mission").AsInt32());
        });

        if (OpenOnStart)
        {
            OpenOnStart = false;
            ShowCampaigns();
        }
    }

    /// <summary>Setvar holding the campaign difficulty (H5_Game.exe 00665f80; stock default_profile value 1).</summary>
    public const string DifficultyVar = "select_campaign_mission_difficulty";

    /// <summary>Difficulty items (UIGameDifficultyInfo: easy, normal, hard, heroic; the exe's count is 4, 008478d0).</summary>
    static readonly string[] DifficultyIds = { "EASY", "NORMAL", "HARD", "HEROIC" };

    /// <summary>
    /// The campaign difficulty as the combo shows it (00698410): the var, or 0 when it is outside the items.
    /// </summary>
    public static int Difficulty
    {
        get
        {
            int v = (int)Options.OptionsStore.GetFloat(DifficultyVar, 1);
            return v >= 0 && v < DifficultyIds.Length ? v : 0;
        }
    }

    static List<string> DifficultyItems()
    {
        const string table = "GameMechanics/RefTables/UIGameDifficultyInfo.xdb";
        var xdb = Game.Instance.Xdb;
        var items = Game.Instance.Fs.Exists(table) ? xdb.Load(table).Root?.Element("objects")?.Elements("Item").ToList() : null;
        return DifficultyIds.Select(id =>
        {
            var obj = items?.FirstOrDefault(i => (string?)i.Element("ID") == "UI_GAME_DIFFICULTY_" + id)?.Element("obj");
            string? Read(string field) => xdb.ReadText((string?)obj?.Element(field)?.Attribute("href"), table)?.Trim();
            return Read("NameFileRef") ?? id;
        }).ToList();
    }

    /// <summary>
    /// SetupPanel (00698600): profile name, ChangeProfile button, and the DifficultyCombo filled with the
    /// difficulty names; choosing one (difficulty_changed, 006984b0) stores the index in the setvar.
    /// </summary>
    static void UpdateSetup(MainMenuScreen menu, CampaignProfile profile)
    {
        foreach (var view in menu.FindAll("ProfileNameView")) view.SetText("<h3><color_title>" + profile.Name);
        var items = DifficultyItems();
        foreach (var node in menu.FindAll("DifficultyCombo"))
        {
            if (node is not UiComboBox combo) continue;
            combo.SetItems(items, Difficulty);
            if (combo.HasMeta("h5_difficulty")) continue;
            combo.SetMeta("h5_difficulty", true);
            combo.SelectionChanged += i =>
            {
                Options.OptionsStore.Set(DifficultyVar, i.ToString());
                Options.OptionsStore.Save();
            };
        }
    }

    /// <summary>
    /// MMH55's MenuPanel groups the campaigns under tab buttons (Btn_Campaign_H5/H5A1/H5A2, one radio
    /// group) whose visual states show or hide their list (VisualOnEnterState → UI_SHOW_WINDOW). The
    /// current state's commands are applied when the panel opens (inferred: the UI engine's initial
    /// visual-state pass was not traced), so no list shows until a tab is chosen.
    /// </summary>
    static void ApplyVisualStates(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is UiNode n && n.Window.ButtonStates.Count > n.State && n.Window.ButtonStates[n.State].OnVisualEnter.Count > 0) n.SetState(n.State);
            ApplyVisualStates(c);
        }
    }

    static UiNode? Child(Node parent, string name)
    {
        foreach (var c in parent.GetChildren())
        {
            if (c is UiNode u && u.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) return u;
            if (Child(c, name) is { } deep) return deep;
        }
        return null;
    }
}
