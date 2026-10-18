using Godot;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn.Campaign;

/// <summary>
/// Drives the main menu's campaign panels (UI/MainMenu2 SelectCampaign / SelectMission):
/// Single Player → Campaign shows the SelectCampaign variant whose block count fits the
/// campaign list (MenuPanelNN holds NN−1 campaign blocks plus Load Campaign), each block
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
            string panelName = $"MenuPanel{Math.Clamp(list.Count + 1, 1, 10):00}";
            foreach (var child in select.GetChildren())
                if (child is UiNode n && n.Name.ToString().StartsWith("MenuPanel")) n.Visible = n.Name == panelName;
            var panel = menu.Find($"SelectCampaign/{panelName}");
            if (panel == null) return;
            for (int i = 0; i < 9; i++)
            {
                var block = Child(panel, $"CampaignBlock{i + 1:00}");
                if (block == null) continue;
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

    static void UpdateSetup(MainMenuScreen menu, CampaignProfile profile)
    {
        foreach (var view in menu.FindAll("ProfileNameView")) view.SetText("<h3><color_title>" + profile.Name);
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
