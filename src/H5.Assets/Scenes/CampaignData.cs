using System.Globalization;
using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Scenes;

/// <summary>A pre-mission bonus offered on the PWL screen (Campaign Missions/Item/Bonuses).</summary>
public sealed class CampaignBonus
{
    /// <summary>E_BONUS_NONE, E_BONUS_ARTIFACT, E_BONUS_CREATURE, E_BONUS_RESOURCE, E_BONUS_SPELL, E_BONUS_BUILDING, …</summary>
    public string Type = "E_BONUS_NONE";
    /// <summary>Script name of the hero receiving it; empty = the player's main hero.</summary>
    public string TargetHero = "";
    public string Creature = "CREATURE_UNKNOWN";
    public int Count;
    public string Artifact = "ARTIFACT_NONE";
    public string Spell = "SPELL_NONE";
    public string Building = "TB_TOWN_HALL";
    /// <summary>Wood, Ore, Mercury, Crystal, Sulfur, Gem, Gold.</summary>
    public int[] Resources = new int[7];

    public static readonly string[] ResourceNames = { "Wood", "Ore", "Mercury", "Crystal", "Sulfur", "Gem", "Gold" };
    public bool IsNone => Type == "E_BONUS_NONE";
}

/// <summary>A hero saved at the end of a mission and carried to a later one (HeroesPool).</summary>
public sealed record CarryOverHero(string HeroScriptName, string? TargetCampaign, int TargetMission);

public sealed class CampaignMission
{
    public int Index;
    public string Name = "", Description = "";
    /// <summary>Pak path of the mission's map-tag.xdb (AdvMapDescTag).</summary>
    public string MapTag = "";
    /// <summary>The scenario map id for the adventure loader, e.g. "A2C1M1".</summary>
    public string MapId = "";
    public List<CampaignBonus> Bonuses = new();
    public int HeroesPoolCount;
    public List<CarryOverHero> HeroesPool = new();
    public List<string> StartMusic = new();
}

public sealed class CampaignDef
{
    public string Path = "", InternalName = "";
    public string Name = "", FullName = "", Comment = "", Description = "";
    public List<string> DependentCampaigns = new();
    public List<CampaignMission> Missions = new();
    public string? AdventureMusic, WinMissionMusic, LoseMissionMusic, Icon;
    public bool UserCampaign;
}

/// <summary>
/// Loads the stock campaigns: Campaigns/CampaignsSets/*.xdb list Campaigns/*.(Campaign).xdb
/// in order; each campaign's missions point at Maps/Scenario/&lt;id&gt;/map-tag.xdb.
/// </summary>
public static class CampaignLoader
{
    public static List<CampaignDef> LoadStandardSet(XdbLoader xdb)
    {
        var fs = xdb.FileSystem;
        var sets = fs.Enumerate("Campaigns/CampaignsSets/").Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: p, Root: xdb.Load(p).Root!))
            .OrderBy(s => int.TryParse((string?)s.Root.Element("Order"), out var o) ? o : 0)
            .ToList();
        var result = new List<CampaignDef>();
        foreach (var (path, root) in sets)
            foreach (var item in root.Element("Campaigns")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var href = XdbRef.Parse((string?)item.Attribute("href"), path);
                if (!href.IsEmpty && fs.Exists(href.Path) && result.All(c => !c.Path.Equals(href.Path, StringComparison.OrdinalIgnoreCase)))
                    result.Add(Load(xdb, href.Path));
            }
        return result;
    }

    public static CampaignDef Load(XdbLoader xdb, string path)
    {
        path = PakFileSystem.Normalize(path);
        var r = xdb.Load(path).Root!;
        string T(XElement? e) => Text(xdb, e, path);
        var c = new CampaignDef
        {
            Path = path,
            InternalName = (string?)r.Element("InternalName") ?? "",
            Name = T(r.Element("NameFileRef")),
            FullName = T(r.Element("FullNameFileRef")),
            Comment = T(r.Element("NameCommentFileRef")),
            Description = T(r.Element("DescriptionFileRef")),
            AdventureMusic = Href(r.Element("AdventureMusic"), path),
            WinMissionMusic = Href(r.Element("WinMissionMusic"), path),
            LoseMissionMusic = Href(r.Element("LoseMissionMusic"), path),
            Icon = Href(r.Element("Icon"), path),
            UserCampaign = (string?)r.Element("UserCampaign") == "true",
        };
        foreach (var d in r.Element("DependentCampaigns")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (Href(d, path) is { } dp) c.DependentCampaigns.Add(dp);
        int index = 0;
        foreach (var m in r.Element("Missions")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var mission = new CampaignMission
            {
                Index = index++,
                Name = T(m.Element("NameFileRef")),
                Description = T(m.Element("NameCommentFileRef")),
                MapTag = Href(m.Element("MissionTag"), path) ?? "",
            };
            var parts = mission.MapTag.Split('/');
            mission.MapId = parts.Length >= 2 ? parts[^2] : "";
            foreach (var b in m.Element("Bonuses")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                mission.Bonuses.Add(ReadBonus(b));
            var pool = m.Element("HeroesPool");
            mission.HeroesPoolCount = int.TryParse((string?)pool?.Element("Count"), out var cnt) ? cnt : 0;
            foreach (var h in pool?.Element("Heroes")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                mission.HeroesPool.Add(new CarryOverHero((string?)h.Element("HeroScriptName") ?? "",
                    Href(h.Element("TargetCampaign"), path),
                    int.TryParse((string?)h.Element("TargetMission"), out var tm) ? tm : -1));
            foreach (var s in m.Element("startMusics")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (Href(s, path) is { } sp) mission.StartMusic.Add(sp);
            c.Missions.Add(mission);
        }
        return c;
    }

    static CampaignBonus ReadBonus(XElement b)
    {
        var bonus = new CampaignBonus
        {
            Type = (string?)b.Element("Type") ?? "E_BONUS_NONE",
            TargetHero = (string?)b.Element("Target")?.Element("Hero") ?? "",
            Creature = (string?)b.Element("BonusArmy")?.Element("Creature") ?? "CREATURE_UNKNOWN",
            Count = int.TryParse((string?)b.Element("BonusArmy")?.Element("Count"), out var n) ? n : 0,
            Artifact = (string?)b.Element("BonusArtifactID") ?? "ARTIFACT_NONE",
            Spell = (string?)b.Element("BonusSpellID") ?? "SPELL_NONE",
            Building = (string?)b.Element("Building") ?? "TB_TOWN_HALL",
        };
        var res = b.Element("BonusResources");
        for (int i = 0; i < 7; i++)
            bonus.Resources[i] = int.TryParse((string?)res?.Element(CampaignBonus.ResourceNames[i]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return bonus;
    }

    internal static string? Href(XElement? e, string currentFile)
    {
        var h = (string?)e?.Attribute("href");
        if (string.IsNullOrWhiteSpace(h)) return null;
        var r = XdbRef.Parse(h, currentFile);
        return r.IsEmpty ? null : r.Path;
    }

    /// <summary>
    /// Text refs in campaign files are relative to the campaign file, but the stock texts.pak
    /// stores some of them under extra "Campaigns/" levels; try the literal path first, then
    /// nested fallbacks, so every stock mission name resolves.
    /// </summary>
    internal static string Text(XdbLoader xdb, XElement? e, string currentFile)
    {
        var h = (string?)e?.Attribute("href");
        if (string.IsNullOrWhiteSpace(h)) return "";
        var direct = xdb.ReadText(h, currentFile);
        if (direct != null) return direct.Trim();
        if (h.StartsWith('/')) return "";
        var fs = xdb.FileSystem;
        for (int depth = 1; depth <= 8; depth++)
        {
            var cand = "Campaigns/" + string.Concat(Enumerable.Repeat("Campaigns/", depth)) + h;
            if (fs.TryReadAllBytes(cand, out var bytes)) return TextDecoding.Decode(bytes).Trim();
        }
        return "";
    }
}
