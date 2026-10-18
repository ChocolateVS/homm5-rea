using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5Reborn.Campaign;

public sealed class SavedStack
{
    public string Creature = "";
    public int Count;
}

/// <summary>
/// A hero saved at the end of a campaign mission (HeroesPool) and restored in the mission
/// named by TargetMission: level, experience, attributes, skills, perks, spells, artifacts,
/// army and war machines. Position, owner and identity come from the new map.
/// </summary>
public sealed class SavedHero
{
    public string ScriptName = "";
    public int Level = 1;
    public long Experience;
    public int Attack, Defence, SpellPower, Knowledge;
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new(), Spells = new(), Artifacts = new(), Backpack = new();
    public List<SavedStack> Army = new();
    public bool Ballista, FirstAidTent, AmmoCart;

    public static SavedHero From(HeroState h) => new()
    {
        ScriptName = h.DefId,
        Level = h.Level, Experience = h.Experience,
        Attack = h.Attack, Defence = h.Defence, SpellPower = h.SpellPower, Knowledge = h.Knowledge,
        Skills = h.Skills.ToList(),
        Perks = h.Perks.ToList(), Spells = h.Spells.ToList(), Artifacts = h.Artifacts.ToList(), Backpack = h.Backpack.ToList(),
        Army = h.Army.Live().Select(s => new SavedStack { Creature = s.Creature, Count = s.Count }).ToList(),
        Ballista = h.Ballista, FirstAidTent = h.FirstAidTent, AmmoCart = h.AmmoCart,
    };

    public void ApplyTo(HeroState h)
    {
        h.Level = Level; h.Experience = Experience;
        h.Attack = Attack; h.Defence = Defence; h.SpellPower = SpellPower; h.Knowledge = Knowledge;
        h.Skills = Skills.ToList();
        h.Perks = Perks.ToList(); h.Spells = Spells.ToList(); h.Artifacts = Artifacts.ToList(); h.Backpack = Backpack.ToList();
        h.Army = H5.Adventure.State.Army.Empty();
        foreach (var s in Army) h.Army.TryAdd(s.Creature, s.Count);
        h.Ballista = Ballista; h.FirstAidTent = FirstAidTent; h.AmmoCart = AmmoCart;
    }
}

/// <summary>
/// Per-profile campaign progress (completed missions, carried heroes, difficulty), kept in
/// user://profiles/&lt;name&gt;/campaigns.json. The original keeps this in its profile folder;
/// the stock format is not reproduced (saves are not interchangeable anyway).
/// </summary>
public sealed class CampaignProfile
{
    public string Name = "Player";
    /// <summary>Campaign internal name → completed mission indices.</summary>
    public Dictionary<string, SortedSet<int>> Completed = new();
    /// <summary>"&lt;campaign&gt;:&lt;targetMission&gt;" → heroes waiting for that mission.</summary>
    public Dictionary<string, List<SavedHero>> CarriedHeroes = new();
    /// <summary>0 easy … 3 heroic (UIGameDifficultyInfo order).</summary>
    public int Difficulty = 1;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static CampaignProfile? _current;
    public static CampaignProfile Current => _current ??= Load("Player");

    static string PathFor(string name) => $"user://profiles/{name}/campaigns.json";

    public static CampaignProfile Load(string name)
    {
        var path = PathFor(name);
        if (Godot.FileAccess.FileExists(path))
        {
            try
            {
                using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
                var p = JsonSerializer.Deserialize<CampaignProfile>(f.GetAsText(), Json);
                if (p != null) { p.Name = name; return p; }
            }
            catch (Exception e) { GD.PushWarning($"[H5] campaign profile {path}: {e.Message}"); }
        }
        return new CampaignProfile { Name = name };
    }

    public void Save()
    {
        DirAccess.MakeDirRecursiveAbsolute($"user://profiles/{Name}");
        using var f = Godot.FileAccess.Open(PathFor(Name), Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(this, Json));
    }

    public bool IsCompleted(string campaign, int mission) =>
        Completed.TryGetValue(campaign, out var s) && s.Contains(mission);

    public bool IsCampaignCompleted(H5.Assets.Scenes.CampaignDef c) =>
        c.Missions.Count > 0 && c.Missions.All(m => IsCompleted(c.InternalName, m.Index));

    public void MarkCompleted(string campaign, int mission)
    {
        if (!Completed.TryGetValue(campaign, out var s)) Completed[campaign] = s = new SortedSet<int>();
        s.Add(mission);
    }

    public static string CarryKey(string campaign, int targetMission1Based) => $"{campaign}:{targetMission1Based}";
}
