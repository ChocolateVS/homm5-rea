using H5.Adventure.Map;
using H5.Adventure.State;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.Engine;

/// <summary>The NWorld tooltip record an object's ITooltiped vf0 returns.</summary>
public enum ObjTooltipKind
{
    /// <summary>SObjCommonTooltip (vtable 00e1564c, built by 004b5190): name, description, a marker line.</summary>
    Common,
    /// <summary>STooltipFlaggedInfo (00e10478, ctor 004536b0): name, description, owner, a dwelling line.</summary>
    Flagged,
    /// <summary>SAdvMineTooltip (00e50e98, ctor 008b6b40): name, description, owner, army, threat level.</summary>
    Mine,
    /// <summary>SAdvMapGarrisonTooltip (00e885c0, ctor 00c06760): name, owner, army, threat level.</summary>
    Garrison,
}

/// <summary>How the tooltip army is listed (00b496d0 mode): 0 exact counts, 1 roughAmounts words (004bc930).</summary>
public enum ArmyDetail { Exact = 0, Rough = 1 }

/// <summary>
/// What an adventure-map object's hover tooltip carries for a viewer: the fields the per-class producers fill into
/// the NWorld tooltip records. Texts are file refs / RPGRoot text ids for the UI to resolve.
/// </summary>
public sealed class ObjTooltip
{
    public ObjTooltipKind Kind;
    /// <summary>Name (shared vf 0x94: messagesFileRef[0]) and the skill the witch hut appends (" (" skill ")").</summary>
    public string NameRef = "";
    public string? NameSkill;
    /// <summary>Description (00ce1820: messagesFileRef[1]; none with fewer than two entries).</summary>
    public string? DescRef;
    /// <summary>A description given by an RPGRoot id (abandoned mine ADVENTURE_ABANDONED_MINE_DESC_*).</summary>
    public string? DescRpg;
    /// <summary>The artifact whose own name / description the tooltip shows (004ed5c0 → 00ca6f70).</summary>
    public string? ArtifactId;
    /// <summary>The marker (SObjCommonTooltip +0x24 / STooltipFlaggedInfo +0x30) as an RPGRoot text id.</summary>
    public string? Mark;
    /// <summary>The owner shown (004fdb60), -1 for none.</summary>
    public int Owner = -1;
    public List<Stack>? Army;
    public ArmyDetail Detail = ArmyDetail.Rough;
    /// <summary>Threat level for the viewer's selected hero (004ea7a0), null when not computed.</summary>
    public int? Danger;
    /// <summary>The owner's dwelling line (009a6210: +0x3c shown, +0x40 count, +0x44 max).</summary>
    public bool DwellingLine;
    public int DwellingCount, DwellingMax;
}

public sealed partial class Adventure
{
    /// <summary>
    /// The building tooltip's marker group per building type: CAdvMapBuilding primary slot 0x20 = 00b01470 switches on
    /// the type (+0xec, tables 00b01908 / 00b018e8) and asks one vfunc; "" = no marker.
    /// </summary>
    public static string BuildingMarkGroup(string type) => type switch
    {
        // Player vf 0x104 (the player's object set +0xa8): cases 8, 9, 0x14, 0x44.
        "BUILDING_WATER_WHEEL" or "BUILDING_WINDMILL" or "BUILDING_MAGIC_SPRING" or "BUILDING_MYSTICAL_GARDEN" => "player_week",
        // Player vf 0xfc (the player's visited objects +0x84): cases 0xa, 0xd, 0xe, 0x39, 0x67.
        "BUILDING_LEAN_TO" or "BUILDING_WAGON" or "BUILDING_SKELETON" or "BUILDING_OBELISK" or "BUILDING_TOMB_OF_THE_WARRIOR" => "player",
        // Hero vf 0x210 (the stables flag): case 0xf.
        "BUILDING_STABLES" => "stables",
        // Hero vf 0x1fc (the next-battle bonus keyed on the object): cases 0x10–0x13, 0x15–0x18, 0x3c, 0x3e, 0x76, 0x77.
        "BUILDING_RALLY_FLAG" or "BUILDING_OASIS" or "BUILDING_LAKE_OF_SCARLET_SWAN" or "BUILDING_FONTAIN_OF_FORTUNE"
            or "BUILDING_FONTAIN_OF_YOUTH" or "BUILDING_FAERIE_RING" or "BUILDING_IDOL_OF_FORTUNE" or "BUILDING_TEMPLE"
            or "BUILDING_BUOY" or "BUILDING_MERMAIDS" or "BUILDING_NOMADS_SHAMAN" or "BUILDING_FORTUITOUS_SANCTUARY" => "hero_battle",
        // Hero vf 0x208 (drank from a well today): case 0x1c.
        "BUILDING_MAGIC_WELL" => "well",
        // Hero vf 0x1f4 (objects the hero visited): cases 0x32–0x36, 0x3b, 0x3d, 0x45, 0x65, 0x66, 0x6a.
        "BUILDING_STAR_AXIS" or "BUILDING_GARDEN_OF_REVELATION" or "BUILDING_MARLETTO_TOWER" or "BUILDING_MERCENARY_CAMP"
            or "BUILDING_LEARNING_STONE" or "BUILDING_TREE_OF_KNOWLEDGE" or "BUILDING_SIRENS" or "BUILDING_ARENA"
            or "BUILDING_SCHOOL_OF_MAGIC" or "BUILDING_WAR_ACADEMY" or "BUILDING_LIBRARY_OF_ENLIGHTENMENT" => "hero",
        // Case 0x37.
        "BUILDING_WITCH_HUT" => "witch_hut",
        _ => "",
    };

    /// <summary>
    /// The hover tooltip of a non-monster, non-hero, non-town object for <paramref name="viewer"/> with its selected hero
    /// <paramref name="selected"/> (the producers' arguments: hero interface (vf 0x14 → hero), player). Per class (the
    /// ITooltiped vf0 of each class, found through its vtable): default 004b5190 (treasure, prison, cartographer,
    /// shipyard, teleport, sign, tavern 00873aa0, hill fort 008b4290, sanctuary 00b49d00), building 00b01390 → 00b01470,
    /// sphinx 009cb850, keymaster tent 00bc94b0, flagged 004527e0 (flagged buildings, army holders, ships), dwelling
    /// 009a6660, mine 00b091d0, abandoned mine 008b5f20, garrison 00ca4ee0, artifact 004ed5c0. Null = no tooltip.
    /// </summary>
    public ObjTooltip? ObjectTooltip(int viewer, HeroState? selected, MapObject o)
    {
        var os = S.Obj(o.Index);
        var p = PlayerByIndex(viewer);
        var sh = o.Shared;
        int owner = os.Owner >= 0 ? os.Owner : o.Owner;
        var t = new ObjTooltip
        {
            NameRef = sh.Messages.Count > 0 ? sh.Messages[0] : "",
            DescRef = sh.Messages.Count >= 2 ? sh.Messages[1] : null,
        };
        bool playerVisited = p != null && (p.VisitedObjects.Contains(o.Index) || os.VisitedBy.Contains(viewer));
        bool ally = owner >= 0 && Allied(owner, viewer);
        switch (o.Kind)
        {
            case ObjectKind.Tent:
            case ObjectKind.Building when sh.Type == "BUILDING_KEYMASTER_TENT":
                // 00bc94b0: the player's keys (vf 0x124, bit mask) hold the tent's colour 1..8 → ADVENTURE_KEYOBTAINED.
                t.Mark = sh.Color is >= 1 and <= 8 && p != null && p.Keys.Contains(sh.Color) ? "ADVENTURE_KEYOBTAINED" : "ADVENTURE_KEYNOTOBTAINED";
                return t;
            case ObjectKind.Sphinx:
                // 009cb850: player vf 0xfc → TT_VISITED / TT_NOT_VISITED.
                if (p != null) t.Mark = playerVisited ? "TT_VISITED" : "TT_NOT_VISITED";
                return t;
            case ObjectKind.Building:
                // 00b01390: the type switch 00b01470 (its first-check vfunc on the object's 4th virtual base is assumed
                // true; with it false the default plus player vf 0xfc TT_VISITED / TT_NOT_VISITED is shown instead).
                BuildingMark(t, o, os, p, selected, playerVisited);
                return t;
            case ObjectKind.Dwelling:
            {
                // 009a6660: shared type 0x54 / 0x5e / 0x5f (fire lake, refugee camp, elemental conflux) → the default.
                if (sh.Type is "BUILDING_FIRE_LAKE" or "BUILDING_REFUGEE_CAMP" or "BUILDING_ELEMENTAL_CONFLUX") return t;
                t.Kind = ObjTooltipKind.Flagged;
                t.Owner = owner;
                if (owner >= 0 && owner == viewer)
                {
                    // The owner: ADVENTURE_DWELLING_TOOLTIP_CREATURES_AVAILABLE when a creature count is > 0, else
                    // _NOT_AVAILABLE; 009a6210: count = the available creatures (+0x40), max (+0x44), line shown when max > 0.
                    int count = os.Available.Sum();
                    t.Mark = count > 0 ? "ADVENTURE_DWELLING_TOOLTIP_CREATURES_AVAILABLE" : "ADVENTURE_DWELLING_TOOLTIP_CREATURES_NOT_AVAILABLE";
                    t.DwellingCount = count;
                    // TODO(exe): 009a6210 sums eight fields of each 0x2c-byte record of the dwelling's vf 0x10 list for
                    // the max; the weekly growth of its creatures stands in.
                    t.DwellingMax = sh.DwellingCreatures.Sum(c => Core.Creatures.TryGetValue(c, out var cd) ? cd.WeeklyGrowth : 0);
                    t.DwellingLine = t.DwellingMax > 0;
                }
                return t;
            }
            case ObjectKind.Mine:
            case ObjectKind.AbandonedMine:
            {
                // 00b091d0 / 008b5f20: SAdvMineTooltip; army mode 0 for the owner's team (00a85030), else 1.
                t.Kind = ObjTooltipKind.Mine;
                t.Owner = owner;
                if (o.Kind == ObjectKind.AbandonedMine)
                {
                    // 008b5f20: no messagesFileRef[1]; the mine's resource 2..6 picks the description (wood / ore: none).
                    t.DescRef = null;
                    t.DescRpg = os.ResourceKind switch
                    {
                        Purse.Mercury => "ADVENTURE_ABANDONED_MINE_DESC_MERCURY",
                        Purse.Crystal => "ADVENTURE_ABANDONED_MINE_DESC_CRYSTAL",
                        Purse.Sulfur => "ADVENTURE_ABANDONED_MINE_DESC_SULFUR",
                        Purse.Gem => "ADVENTURE_ABANDONED_MINE_DESC_GEM",
                        Purse.Gold => "ADVENTURE_ABANDONED_MINE_DESC_GOLD",
                        _ => null,
                    };
                }
                FillArmy(t, os, owner, viewer, selected, ally);
                return t;
            }
            case ObjectKind.Garrison:
                // 00ca4ee0: name only (no description), owner, army (mode 0 for the team), threat level.
                t.Kind = ObjTooltipKind.Garrison;
                t.DescRef = null;
                t.Owner = owner;
                FillArmy(t, os, owner, viewer, selected, ally);
                return t;
            case ObjectKind.Artifact:
                // 004ed5c0: one artifact → its own tooltip (00ca6f70: the artifact's name and description); none → none.
                if ((os.ArtifactId ?? o.ArtifactId ?? sh.ArtifactId) is not { Length: > 0 } a) return null;
                t.ArtifactId = a;
                return t;
            case ObjectKind.Treasure:
            case ObjectKind.Prison:
            case ObjectKind.Cartographer:
            case ObjectKind.Shipyard:
            case ObjectKind.Sign:
            case ObjectKind.HillFort:
            case ObjectKind.SeerHut:
            case ObjectKind.Shrine:
            case ObjectKind.DwarvenWarren:
                // The default (004b5190 through primary slot 0x1c, 004b46e0); seer hut 004f2760 adds quest states, shrine
                // 00a80a80 has its own SAdvMapShrineTooltip window, dwarven warren 00457a30 a resource line: not built.
                return t;
            default:
                if (owner >= 0)
                {
                    // 004527e0: STooltipFlaggedInfo with the owner (flagged buildings, army holders, ships).
                    t.Kind = ObjTooltipKind.Flagged;
                    t.Owner = owner;
                }
                return t;
        }
    }

    void BuildingMark(ObjTooltip t, MapObject o, ObjState os, PlayerState? p, HeroState? hero, bool playerVisited)
    {
        // 00b01470: a hero test needs the hero argument (ESI), a player test the player (EBX); nothing without it.
        static string Pick(bool v) => v ? "TT_VISITED" : "TT_NOT_VISITED";
        bool heroVisited = hero != null && (hero.VisitedOnce.Contains(o.Index) || hero.VisitedWeek.Contains(o.Index) || hero.VisitedDay.Contains(o.Index));
        switch (BuildingMarkGroup(o.Shared.Type))
        {
            case "player_week":
                // TODO(exe): player vf 0x104 reads the player's set +0xa8 (filler not traced); the object's weekly flag stands in.
                if (p != null) t.Mark = Pick(os.LastVisitWeek != 0 && os.LastVisitWeek == S.Week);
                break;
            case "player":
                if (p != null) t.Mark = Pick(playerVisited);
                break;
            case "stables":
                if (hero != null) t.Mark = Pick(hero.StablesThisWeek);
                break;
            case "hero_battle":
                if (hero != null) t.Mark = Pick(hero.VisitedSinceBattle.Contains(o.Index));
                break;
            case "well":
                if (hero != null) t.Mark = Pick(hero.DrankFromWellToday);
                break;
            case "hero":
                if (hero != null) t.Mark = Pick(heroVisited);
                break;
            case "witch_hut":
                // 00b01787: needs both. Player vf 0xfc → the name gets " (" skill ")" and, unless the hero visited, TT_NOT_LEARNED;
                // then hero vf 0x1f4 → TT_LEARNED.
                if (p == null || hero == null) break;
                if (playerVisited)
                {
                    t.NameSkill = os.SkillId;
                    if (!heroVisited) t.Mark = "TT_NOT_LEARNED";
                }
                if (heroVisited) t.Mark = "TT_LEARNED";
                break;
        }
    }

    void FillArmy(ObjTooltip t, ObjState os, int owner, int viewer, HeroState? selected, bool ally)
    {
        var army = (owner < 0 ? os.Guards : os.Army.Where(x => x != null).Select(x => x!)).Where(x => x is { Count: > 0 }).ToList();
        t.Army = army;
        t.Detail = ally ? ArmyDetail.Exact : ArmyDetail.Rough;
        // The threat level (004ea7a0) only for a viewer outside the team with a selected hero (004e9280).
        if (!ally && selected != null && selected.Owner == viewer) t.Danger = MonsterDanger(selected, army);
    }
}
