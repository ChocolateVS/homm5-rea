using Godot;
using H5.Adventure.State;
using H5.Assets.Scenes;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Campaign;

/// <summary>The campaign mission currently being played (null for custom games).</summary>
public sealed record ActiveMission(CampaignDef Campaign, CampaignMission Mission, int BonusIndex, AdventureSession Session);

/// <summary>
/// The campaign game flow: Single Player → Campaign → campaign → mission → PWL screen
/// (briefing, bonus choice, loading, "press any key") → adventure map → MissionResults →
/// next mission's PWL screen, or back to the menu. Applies the chosen bonus and the heroes
/// carried over from earlier missions (HeroesPool) when the mission's map is loaded.
/// </summary>
public static class CampaignFlow
{
    static List<CampaignDef>? _campaigns;

    public static IReadOnlyList<CampaignDef> Campaigns => _campaigns ??= CampaignLoader.LoadStandardSet(Game.Instance.Xdb);

    public static CampaignDef? Find(string internalName) =>
        Campaigns.FirstOrDefault(c => c.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>A campaign is open once every campaign it depends on is completed.</summary>
    public static bool IsUnlocked(CampaignDef c, CampaignProfile p) =>
        c.DependentCampaigns.All(d => Campaigns.FirstOrDefault(x => x.Path.Equals(d, StringComparison.OrdinalIgnoreCase)) is not { } dep
                                      || p.IsCampaignCompleted(dep));

    /// <summary>Completed missions can be replayed; the first uncompleted one is the next to play.</summary>
    public static bool IsMissionAvailable(CampaignDef c, int mission, CampaignProfile p) =>
        p.IsCompleted(c.InternalName, mission) || Enumerable.Range(0, mission).All(i => p.IsCompleted(c.InternalName, i));

    public static ActiveMission? Active { get; private set; }

    public static void OpenPwl(Node host, string campaign, int mission)
    {
        var c = Find(campaign);
        if (c == null || mission < 0 || mission >= c.Missions.Count) { GD.PushWarning($"[H5] no campaign mission {campaign}/{mission}"); return; }
        var tree = (SceneTree)Engine.GetMainLoop();
        var pwl = new PwlScreen(c, c.Missions[mission]);
        var layer = new CanvasLayer { Name = "PWL", Layer = 50 };
        tree.Root.AddChild(layer);
        layer.AddChild(pwl);
        // The menu (or the finished adventure screen) goes away behind the PWL screen; the
        // adventure screen opened by StartMission becomes the next current scene.
        if (tree.CurrentScene is { } scene) { scene.QueueFree(); tree.CurrentScene = null; }
        _ = host;
    }

    /// <summary>
    /// Loads the mission's map and opens the adventure screen. Runs on the main thread (no
    /// worker threads, see docs/WEB_PORT.md); the PWL screen draws its progress bar first.
    /// </summary>
    public static AdventureScreen StartMission(CampaignDef c, CampaignMission m, int bonusIndex)
    {
        var profile = CampaignProfile.Current;
        var seed = (ulong)Time.GetTicksUsec();
        var session = AdventureSession.Start(m.MapId, seed, difficulty: profile.Difficulty);
        ApplyCarryOver(session, c, m, profile);
        if (bonusIndex >= 0 && bonusIndex < m.Bonuses.Count) ApplyBonus(session, m.Bonuses[bonusIndex]);
        Active = new ActiveMission(c, m, bonusIndex, session);
        var screen = AdventureScreen.Open(session);
        // Campaign maps are scripted: host their scripts even before AdventureScreen does it itself.
        // AdventureScreen.Open already added (and readied) the screen; attach on the next frame.
        Callable.From(() => ScriptUiBridge.Attach(screen)).CallDeferred();
        return screen;
    }

    static HeroState? MainHero(AdventureSession s, string targetHero)
    {
        if (targetHero.Length > 0 && s.Game.HeroByName(targetHero) is { } named) return named;
        return s.S.Heroes.FirstOrDefault(h => h.Alive && h.Owner == s.Human.Index);
    }

    /// <summary>Gives the chosen pre-mission bonus (E_BONUS_*) to the player's main hero / town / treasury.</summary>
    public static void ApplyBonus(AdventureSession s, CampaignBonus b)
    {
        var g = s.Game;
        var hero = MainHero(s, b.TargetHero);
        var player = s.Human.Index;
        g.RunScriptAction(() =>
        {
            switch (b.Type)
            {
                case "E_BONUS_ARTIFACT" when hero != null && b.Artifact != "ARTIFACT_NONE":
                    g.ScriptGiveArtifact(hero, b.Artifact);
                    break;
                case "E_BONUS_CREATURE" when hero != null && b.Count > 0:
                    hero.Army.TryAdd(b.Creature, b.Count);
                    break;
                case "E_BONUS_SPELL" when hero != null && b.Spell != "SPELL_NONE":
                    g.ScriptLearnSpell(hero, b.Spell);
                    break;
                case "E_BONUS_RESOURCE":
                    for (int r = 0; r < 7; r++)
                        if (b.Resources[r] != 0) g.ScriptGive(player, r, b.Resources[r]);
                    break;
                case "E_BONUS_BUILDING" when s.S.Towns.FirstOrDefault(t => t.Owner == player) is { } town:
                    g.ScriptUpgradeBuilding(town, b.Building);
                    break;
            }
        });
    }

    static void ApplyCarryOver(AdventureSession s, CampaignDef c, CampaignMission m, CampaignProfile p)
    {
        if (!p.CarriedHeroes.TryGetValue(CampaignProfile.CarryKey(c.InternalName, m.Index + 1), out var heroes)) return;
        foreach (var saved in heroes)
            if (s.Game.HeroByNameAnywhere(saved.ScriptName) is { } h) saved.ApplyTo(h);
    }

    /// <summary>Called when the active mission's game ends; records progress and carries heroes forward.</summary>
    public static void OnMissionEnded(bool won)
    {
        if (Active is not { } a) return;
        var p = CampaignProfile.Current;
        if (won)
        {
            p.MarkCompleted(a.Campaign.InternalName, a.Mission.Index);
            foreach (var pool in a.Mission.HeroesPool)
            {
                var h = a.Session.Game.HeroByNameAnywhere(pool.HeroScriptName);
                if (h == null) continue;
                var target = pool.TargetCampaign != null ? Campaigns.FirstOrDefault(x => x.Path.Equals(pool.TargetCampaign, StringComparison.OrdinalIgnoreCase))?.InternalName ?? a.Campaign.InternalName : a.Campaign.InternalName;
                var key = CampaignProfile.CarryKey(target, pool.TargetMission);
                if (!p.CarriedHeroes.TryGetValue(key, out var list)) p.CarriedHeroes[key] = list = new();
                list.RemoveAll(x => x.ScriptName == h.DefId);
                list.Add(SavedHero.From(h));
            }
            p.Save();
        }
    }

    /// <summary>After the results window: the next mission's PWL screen, or the campaign menu.</summary>
    public static void Continue(Node host, bool won)
    {
        var a = Active;
        Active = null;
        if (a != null && won && a.Mission.Index + 1 < a.Campaign.Missions.Count)
        {
            OpenPwl(host, a.Campaign.InternalName, a.Mission.Index + 1);
            return;
        }
        CampaignMenu.OpenOnStart = a != null;
        host.GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }
}
