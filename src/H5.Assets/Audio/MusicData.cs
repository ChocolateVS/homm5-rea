using System.Globalization;
using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Audio;

/// <summary>A Sounds/_(Ambient) AmbientSoundSet: a looping bed plus random one-shots (pauses in seconds).</summary>
public sealed record AmbientSet(string Path, string? Loop, IReadOnlyList<string> Sounds, float MinPause, float MaxPause);

/// <summary>One race's entry of Sounds/_(Music)/TableRaceMusic.xdb (UIGameRoot.TableRaceMusic).</summary>
public sealed class RaceMusic
{
    public string Race = "";
    public string? TownAmbientSoundSet;
    public MusicTrack? Town, Tavern, Dwelling, Siege, WinCombat, LoseCombat, RetreatCombat, Wait;
    public List<MusicTrack> Combat = new();
    public string? MagicGuildSound, TownHallSound, MarketplaceSound, ShipyardSound, BlacksmithSound, UpgradeTownBuildingSound;
}

/// <summary>The music lists of one campaign mission (Campaigns/*.(Campaign).xdb).</summary>
public sealed class MissionMusic
{
    public List<MusicTrack> Start = new(), AdditionalAdventure = new(), AdditionalWait = new(), AdditionalCombat = new();
    public MusicTrack? CampaignAdventure, WinMission, LoseMission;
}

/// <summary>
/// Reads the original sound-routing data: music descriptors, ambient sets, the race music
/// table, UIGameRoot's named musics/sounds, AdvMapTile terrain music/ambience and campaign
/// mission music. Engine-neutral; the Godot client plays what this returns.
/// </summary>
public sealed class MusicData(XdbLoader xdb)
{
    public const string RaceTablePath = "Sounds/_(Music)/TableRaceMusic.xdb";
    public const string UiGameRootPath = "UI/UIGameRoot.(UIGameRoot).xdb";
    public const string UiConstsPath = "UI/uiconsts.(UIGameConstsH5).xdb";

    readonly Dictionary<string, MusicTrack?> _tracks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AmbientSet?> _ambients = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string? Ambient, MusicTrack? Music)> _tiles = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, RaceMusic>? _races;
    Dictionary<string, string>? _uiMusics, _uiSounds, _uiAmbients;

    public XdbLoader Xdb => xdb;

    static string? Href(XElement? e) => e?.Attribute("href")?.Value is { Length: > 0 } h ? h : null;
    static int Int(XElement? e) => int.TryParse(e?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    static float Float(XElement? e) => float.TryParse(e?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    string? Resolve(string? href, string current)
    {
        if (href == null) return null;
        var p = XdbRef.Parse(href, current).Path;
        return xdb.FileSystem.Exists(p) ? p : null;
    }

    /// <summary>A Music descriptor by href (relative hrefs resolve against <paramref name="current"/>).</summary>
    public MusicTrack? Track(string? href, string current = "")
    {
        var path = Resolve(href, current);
        if (path == null) return null;
        if (_tracks.TryGetValue(path, out var t)) return t;
        var r = xdb.Load(path).Root;
        t = r == null ? null : new MusicTrack(path, r.Element("FileName")?.Value?.Trim() ?? "",
            Int(r.Element("FadeIn")), Int(r.Element("FadeOut")), Int(r.Element("MinPausedTime")), Int(r.Element("MaxPausedTime")));
        if (t != null && t.FileName.Length == 0) t = null;
        return _tracks[path] = t;
    }

    public AmbientSet? Ambient(string? href, string current = "")
    {
        var path = Resolve(href, current);
        if (path == null) return null;
        if (_ambients.TryGetValue(path, out var a)) return a;
        var r = xdb.Load(path).Root;
        a = r == null ? null : new AmbientSet(path,
            Resolve(Href(r.Element("Loop")), path),
            r.Element("sounds")?.Elements("Item").Select(i => Resolve(Href(i), path)).Where(s => s != null).Select(s => s!).ToList() ?? new(),
            Float(r.Element("MinPauseTime")), Float(r.Element("MaxPauseTime")));
        return _ambients[path] = a;
    }

    public IReadOnlyDictionary<string, RaceMusic> Races => _races ??= LoadRaces();

    public RaceMusic? Race(string? race) =>
        race != null && Races.TryGetValue(race, out var r) ? r : Races.GetValueOrDefault("TOWN_NO_TYPE");

    Dictionary<string, RaceMusic> LoadRaces()
    {
        var map = new Dictionary<string, RaceMusic>(StringComparer.OrdinalIgnoreCase);
        if (!xdb.FileSystem.Exists(RaceTablePath)) return map;
        var root = xdb.Load(RaceTablePath).Root!;
        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var info = item.Element("musicInfo");
            if (info == null) continue;
            var rm = new RaceMusic
            {
                Race = item.Element("race")?.Value ?? "",
                TownAmbientSoundSet = Resolve(Href(info.Element("TownAmbientSoundSet")), RaceTablePath),
                Town = Track(Href(info.Element("TownMusic")), RaceTablePath),
                Tavern = Track(Href(info.Element("TavernMusic")), RaceTablePath),
                Dwelling = Track(Href(info.Element("DwellingMusic")), RaceTablePath),
                Siege = Track(Href(info.Element("SiegeMusic")), RaceTablePath),
                WinCombat = Track(Href(info.Element("WinCombatMusic")), RaceTablePath),
                LoseCombat = Track(Href(info.Element("LooseCombatMusic")), RaceTablePath),
                RetreatCombat = Track(Href(info.Element("RetreatCombatMusic")), RaceTablePath),
                Wait = Track(Href(info.Element("WaitMusic")), RaceTablePath),
                MagicGuildSound = Resolve(Href(info.Element("MagicGuildSound")), RaceTablePath),
                TownHallSound = Resolve(Href(info.Element("TownHallSound")), RaceTablePath),
                MarketplaceSound = Resolve(Href(info.Element("MarketplaceSound")), RaceTablePath),
                ShipyardSound = Resolve(Href(info.Element("ShipyardSound")), RaceTablePath),
                BlacksmithSound = Resolve(Href(info.Element("BlacksmithSound")), RaceTablePath),
                UpgradeTownBuildingSound = Resolve(Href(info.Element("UpgradeTownBuildingSound")), RaceTablePath),
            };
            foreach (var c in info.Element("combatMusics")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (Track(Href(c), RaceTablePath) is { } t) rm.Combat.Add(t);
            map[rm.Race] = rm;
        }
        return map;
    }

    Dictionary<string, string> UiTable(string list, string field)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!xdb.FileSystem.Exists(UiGameRootPath)) return d;
        var root = xdb.Load(UiGameRootPath).Root!;
        foreach (var item in root.Element(list)?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (item.Element("ID")?.Value is { } id && Resolve(Href(item.Element(field)), UiGameRootPath) is { } p)
                d[id] = p;
        return d;
    }

    /// <summary>UIGameRoot musics (MAIN_MENU_MUSIC_ID, MP_MAIN_MENU_MUSIC_ID, CREDITS_SCREEN_MUSIC_ID, HERO_FIND_GRAIL_MUSIC…).</summary>
    public MusicTrack? UiMusic(string id) => (_uiMusics ??= UiTable("musics", "Music")).TryGetValue(id, out var p) ? Track(p) : null;

    /// <summary>UIGameRoot sounds (HERO_LEVEL_UP_SOUND, NEW_WEEK_GOOD_SOUND, OBJECTIVE_COMPLETED_SOUND…): a Sound xdb path.</summary>
    public string? UiSound(string id) => (_uiSounds ??= UiTable("sounds", "Sound")).GetValueOrDefault(id);

    /// <summary>UIGameRoot ambientSounds (INVISIBLE_AMBIENT_SOUND).</summary>
    public string? UiAmbient(string id) => (_uiAmbients ??= UiTable("ambientSounds", "AmbientSound")).GetValueOrDefault(id);

    /// <summary>
    /// The Sound played by a uiconsts command list such as ButtonClickSound /
    /// DisableButtonClickSound (each item is a (UISPlaySound) file with a Sound href).
    /// </summary>
    public string? UiConstSound(string field)
    {
        if (!xdb.FileSystem.Exists(UiConstsPath)) return null;
        var item = xdb.Load(UiConstsPath).Root?.Element(field)?.Element("Commands")?.Element("Item");
        var cmd = Resolve(Href(item), UiConstsPath);
        return cmd == null ? null : Resolve(Href(xdb.Load(cmd).Root?.Element("Sound")), cmd);
    }

    /// <summary>An (AdvMapTile) descriptor's AmbientSoundSet path and AdventureMusic.</summary>
    public (string? Ambient, MusicTrack? Music) Tile(string tilePath)
    {
        tilePath = PakFileSystem.Normalize(tilePath);
        if (_tiles.TryGetValue(tilePath, out var v)) return v;
        v = (null, null);
        if (xdb.FileSystem.Exists(tilePath) && xdb.Load(tilePath).Root is { } r)
            v = (Resolve(Href(r.Element("AmbientSoundSet")), tilePath), Track(Href(r.Element("AdventureMusic")), tilePath));
        return _tiles[tilePath] = v;
    }

    /// <summary>An (AdvMapHeroShared) descriptor's AdventureMusic (campaign heroes have one).</summary>
    public MusicTrack? HeroMusic(string? sharedPath)
    {
        if (string.IsNullOrEmpty(sharedPath) || !xdb.FileSystem.Exists(sharedPath)) return null;
        return Track(Href(xdb.Load(sharedPath).Root?.Element("AdventureMusic")), PakFileSystem.Normalize(sharedPath));
    }

    /// <summary>Music lists of mission <paramref name="index"/> of a (Campaign) xdb.</summary>
    public MissionMusic Mission(string campaignPath, int index)
    {
        var mm = new MissionMusic();
        if (!xdb.FileSystem.Exists(campaignPath)) return mm;
        campaignPath = PakFileSystem.Normalize(campaignPath);
        var root = xdb.Load(campaignPath).Root!;
        mm.CampaignAdventure = Track(Href(root.Element("AdventureMusic")), campaignPath);
        mm.WinMission = Track(Href(root.Element("WinMissionMusic")), campaignPath);
        mm.LoseMission = Track(Href(root.Element("LoseMissionMusic")), campaignPath);
        var mission = root.Element("Missions")?.Elements("Item").ElementAtOrDefault(index);
        if (mission == null) return mm;
        void Fill(List<MusicTrack> list, string name)
        {
            foreach (var i in mission.Element(name)?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (Track(Href(i), campaignPath) is { } t) list.Add(t);
        }
        Fill(mm.Start, "startMusics");
        Fill(mm.AdditionalAdventure, "additionalAdventureMusics");
        Fill(mm.AdditionalWait, "additionalWaitMusics");
        Fill(mm.AdditionalCombat, "additionalCombatMusics");
        return mm;
    }

    /// <summary>
    /// Music FileName is relative to the original game's working directory (bin/):
    /// "..\Music\a1\Campaign-Dungeon.ogg" → &lt;install&gt;/music/a1/Campaign-Dungeon.ogg.
    /// The install folder is lower-case "music" on GOG; lookup is case-insensitive per segment.
    /// </summary>
    public static string? ResolveFile(string installRoot, string fileName) =>
        H5.Core.IO.InstallPath.ResolveFile(installRoot, "bin", fileName);
}
