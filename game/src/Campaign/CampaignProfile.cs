using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using H5.Adventure.Engine;
using H5.Core.Data;

namespace H5Reborn.Campaign;

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
    /// <summary>"&lt;campaign&gt;:&lt;targetMission&gt;" (0-based) → heroes waiting for that mission.</summary>
    public Dictionary<string, List<CampaignHeroRecord>> CarriedHeroes = new();
    /// <summary>
    /// "&lt;campaign&gt;:&lt;mission&gt;" → the extra heroes (HeroesPool Count beyond its entries) stored by the mission won before
    /// it (0045f460 → record +0x18, handed on by 0045f080). Kept for the map's placeholder heroes; not consumed yet (GAP).
    /// </summary>
    public Dictionary<string, List<CampaignHeroRecord>> CarriedExtras = new();

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static CampaignProfile? _current;
    /// <summary>Progress of the selected player profile (game/src/Profiles).</summary>
    public static CampaignProfile Current =>
        _current is { } c && c.Name == Profiles.ProfileManager.CurrentName ? c : _current = Load(Profiles.ProfileManager.CurrentName);

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

    /// <summary>Key of the heroes waiting for a mission; <paramref name="targetMission"/> is 0-based like HeroesPool TargetMission.</summary>
    public static string CarryKey(string campaign, int targetMission) => $"{campaign}:{targetMission}";
}
