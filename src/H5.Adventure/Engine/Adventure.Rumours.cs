using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Buy a rumour in a tavern (exe CRerollRumourCmd, vtable 00e197b4, ctor 00639170 with world, player, price and the
/// tavern's rumour holder; sent by the tavern screen's rumours page action 00635320). <paramref name="TownObject"/> is
/// the town or the map tavern (BUILDING_TAVERN) whose list grows.
/// </summary>
public sealed record BuyRumourCmd(int TownObject) : AdvCommand;

// Tavern rumours: the world's CRumours (CWorld +0x134, IRumours vtable 00e549cc, getter world vf 0x144 = 00af7e10)
// and the taverns' rumour lists (IAdvMapTavern: CAdvMapTown 00e95830, CAdvMapTavern 00e4cd88).
public sealed partial class Adventure
{
    /// <summary>Rumour price: RPGStats +0x54 (00b4c260) = DefaultStats RumorCost (100 gold in 3.1 and MMH 5.5).</summary>
    public int RumourCost => (int)Db.Stats.Get("RumorCost", 100);

    /// <summary>TOWN_NO_TYPE (types.xml value 2): the town type the map tavern passes (00873a60) and "any tavern".</summary>
    public const string RumourAnyTown = "TOWN_NO_TYPE";

    /// <summary>
    /// CRerollRumourCmd execute 004f4750: the player pays the price in gold (player vf 0xe0 with a gold purse 00423c20;
    /// nothing happens when it fails), then the holder's slot 0 rerolls with the world: the town's 00cce300 generates
    /// with the town type (town vf 0xd4), the map tavern's 00873a60 with TOWN_NO_TYPE. One entry is appended (or none,
    /// see <see cref="GenerateRumour"/>); the screen shows every entry (FIRST_RUMOR / RUMORS_FORMAT, 006372f3).
    /// </summary>
    string? DoBuyRumour(BuyRumourCmd c)
    {
        var p = Current;
        List<RumourEntry> list;
        string townType;
        if (S.Town(c.TownObject) is { } t)
        {
            if (t.Owner != p.Index) return "not your town";
            if (t.Level("TB_TAVERN") <= 0) return "no tavern";
            list = t.Rumours;
            townType = t.Type;
        }
        else if (c.TownObject >= 0 && c.TownObject < Map.Objects.Count && Map.Objects[c.TownObject] is { Kind: ObjectKind.Building } o
                 && o.Shared.Type == MapTavern && !S.Obj(o.Index).Removed)
        {
            list = S.Obj(o.Index).Rumours;
            townType = RumourAnyTown;
        }
        else return "no tavern";
        int cost = RumourCost;
        if (p.Purse[Purse.Gold] < cost) return "not enough gold";
        GiveOne(p, Purse.Gold, -cost);
        GenerateRumour(list, townType);
        return null;
    }

    /// <summary>
    /// Map start (0041ecd0, a new game only): CRumours slot 0 (008e7cc0) adds the common set
    /// (GameMechanics/Rumours/CommonGameRumours, SGameRumoursSet +0x44) and then the map's MapRumours (AdvMapDesc
    /// +0x210) through slot 1 (008e8560: text resolved by 004c7430, weight, town type, told 0).
    /// </summary>
    void SetupRumours()
    {
        S.Rumours = new RumoursState();
        foreach (var r in Db.CommonRumours.Concat(Map.Rumours))
            S.Rumours.Plain.Add(new PlainRumour { Text = r.Text, Weight = r.Weight, TownType = r.TownType });
    }

    /// <summary>
    /// The town's weekly rumour (town vf 0xf0 = 00cd2720, first step of the town's weekly update 00cd2b90): the
    /// list is emptied and one rumour generated with the town type. Map taverns have no weekly update
    /// (CAdvMapTavern implements no IAdvMapWeeklyUpdate), so their lists only grow by bought rumours.
    /// </summary>
    void TownWeeklyRumour(TownState t)
    {
        t.Rumours.Clear();
        GenerateRumour(t.Rumours, t.Type);
    }

    // ------------------------------------------------------------------ last events (IRumours slots 2..6)

    /// <summary>
    /// Stores a player's last event (008e7e60 monster +0, 008e7ea0 artifact +0xc, 008e7ee0 capture +0x18, 008e7f20
    /// purchase +0x24, 008e7f60 build +0x30 with the level +0x3c) by player id 1..8 (id − 1 = our player index).
    /// The callers skip the record while the world's AI estimate flag (world vf 0x138, +0x84, set by vf 0x13c from
    /// the AI's hypothetical turns) is set; the engine never plays such turns on the real state.
    /// </summary>
    void RecordRumourEvent(int player, Action<RumourEvents> set)
    {
        if (player >= 0 && player < S.Rumours.Last.Length) set(S.Rumours.Last[player]);
    }

    /// <summary>
    /// A monster army beaten (00af6680 → slot 2): the creature name (004bcbe0) of the stack its army's vf 0x14 picks.
    /// GAP-details: that index (army vf 0x14) is not traced; the first stack of the beaten army is named.
    /// </summary>
    void RumourMonsterBeaten(HeroState winner, IEnumerable<Stack> defenders)
    {
        if (defenders.FirstOrDefault(s => s.Count > 0) is { } s && Core.Creatures.TryGetValue(s.Creature, out var c))
            RecordRumourEvent(winner.Owner, e => e.Monster = c.Name);
    }

    /// <summary>A hero gains an artifact (hero 00b4d940 → slot 3): the artifact's name.</summary>
    void RumourArtifactGained(HeroState h, string artifact)
    {
        if (Core.Artifacts.TryGetValue(artifact, out var a)) RecordRumourEvent(h.Owner, e => e.Artifact = a.Name);
    }

    /// <summary>
    /// A flagged object changes owner (CAdvMapFlagged 004530e0 → slot 4, when the new owner is a player and differs
    /// from the old one): the object's name (vf 0x94; the custom name of vf 0x3c when it has one).
    /// GAP-details: the name getter is not traced per class; the name the adventure UI shows is used (town name, the
    /// first messagesFileRef text, else the type).
    /// </summary>
    void RumourCaptured(MapObject o, int player) => RecordRumourEvent(player, e => e.Capture = RumourObjectName(o));

    /// <summary>Creatures bought (town 00cdd630, dwelling command 004f4fb0 → slot 5): the creature name.</summary>
    void RumourCreaturesBought(int player, string creature)
    {
        if (Core.Creatures.TryGetValue(creature, out var c)) RecordRumourEvent(player, e => e.Purchase = c.Name);
    }

    /// <summary>A building built (00cd4a40 → slot 6): its name at the new level (00cd3fc0) and the level.</summary>
    void RumourBuilt(TownState t, string building, int level)
    {
        var name = Db.Building(t.Type, building, level)?.Name ?? "";
        if (name.Length > 0) RecordRumourEvent(t.Owner, e => { e.Build = name; e.BuildLevel = level; });
    }

    string RumourObjectName(MapObject o)
    {
        if (o.Kind == ObjectKind.Town && S.Town(o.Index) is { Name.Length: > 0 } t) return t.Name;
        var href = (string?)o.Shared.Raw?.Element("messagesFileRef")?.Elements("Item").FirstOrDefault()?.Attribute("href");
        if (href is { Length: > 0 } && Db.ReadText(href, o.Shared.Path) is { Length: > 0 } name)
            return System.Text.RegularExpressions.Regex.Replace(name, "<[^>]*>", "").Trim();
        return o.Shared.Type.Replace("BUILDING_", "").Replace('_', ' ');
    }

    // ------------------------------------------------------------------ generator 008e85b0

    sealed class RumourCandidate
    {
        public int Weight, Kind;
        public string Text = "";
        /// <summary>[5]: the floor (kinds 1, 3, 5), the player's position in the player list (7..0x1c), the plain rumour (0).</summary>
        public int Index;
        public string Format = "";
    }

    static readonly string[] StatFormats =
    {
        "RUMOUR_FORMAT_PLAYER_BEST_ARMIES", "RUMOUR_FORMAT_PLAYER_BEST_ARMY", "RUMOUR_FORMAT_PLAYER_MOST_GOLD",
        "RUMOUR_FORMAT_PLAYER_MOST_WOOD", "RUMOUR_FORMAT_PLAYER_MOST_ORE", "RUMOUR_FORMAT_PLAYER_MOST_SULFUR",
        "RUMOUR_FORMAT_PLAYER_MOST_CRYSTAL", "RUMOUR_FORMAT_PLAYER_MOST_GEMS", "RUMOUR_FORMAT_PLAYER_MOST_MERCURY",
        "RUMOUR_FORMAT_PLAYER_MOST_OBELISKS", "RUMOUR_FORMAT_PLAYER_BEST_CREATURE", "RUMOUR_FORMAT_PLAYER_BEST_INCOME",
        "RUMOUR_FORMAT_PLAYER_MOST_MINES", "RUMOUR_FORMAT_PLAYER_MOST_ARTIFACTS", "RUMOUR_FORMAT_PLAYER_BEST_HERO",
        "RUMOUR_FORMAT_PLAYER_MOST_TOWNS", "RUMOUR_FORMAT_PLAYER_BEST_TOWN",
    };

    /// <summary>
    /// CRumours slot 7 (008e85b0, args world, list, town type): gathers weighted candidates and appends one rumour.
    /// Players = ids 1..8 with world vf 0x68 whose vf 0x6c is 1 (in play). Candidates, all weight 100:
    /// the buried Tear of Asha (grail manager world vf 0x108 → vf 0x18): kind 1 (its floor, only when the map has an
    /// underground, world vf 0x28(1)) and kind 2 (its ground, 008e7fb0); per floor 0..1, every map artifact whose
    /// artifact is not WAND_OF_X (3) / SCROLL_OF_SPELL_X (52) and of class +0x24 = 1 (ARTF_CLASS_MAJOR): kind 3
    /// (floor, with an underground) and kind 4 (ground); kinds 5/6 (see GAP); per statistic 7..0x17 one candidate for
    /// the leading player (<see cref="RumourStat"/>); per last event 0x18..0x1c and player a candidate when the event's
    /// text is set (0x1c BUILD, or UPGRADE when the level is above 1). The event slot read is the player's position in
    /// the list (+0x40 per entry) while the writers index by id − 1, as in the exe.
    /// Then the plain rumours of TOWN_NO_TYPE or the given town type, weight · 3 / (told + 1); their weights are
    /// multiplied by f = (sum of the other weights · 3) / (plain sum) (1 when either is below 1). With
    /// total = f · plain sum + others, r = rng % total (world vf 0x98 → vf 0x20; 0 without calling it when total is
    /// 0) picks the first candidate whose running sum exceeds r (else candidate 0). Two tries, the picked one and the
    /// next: the entry (kind 0: RUMOUR_FORMAT_PLAINTEXT with the text, the plain rumour's told count + 1; kinds 1/3/5:
    /// OBJECT_FLOOR_SURFACE / _UNDERGROUND; kinds 2/4/6 and 7..0x1c: the candidate's format and text, the latter with
    /// the player's id, vf 0x30) is appended unless its format is empty or it equals the list's last entry (008ea5d0).
    /// GAP: kinds 5/6 (objects with vf 0x28 → an interface whose vf 0x20 holds, named by vf 0x94) are not identified
    /// and not offered; the world flag that suppresses all non-plain candidates (world vf 0x14 → +0x88, byte +9) is
    /// not identified and taken as clear.
    /// </summary>
    public void GenerateRumour(List<RumourEntry> list, string townType)
    {
        var players = RumourPlayers();
        var cands = RumourCandidateList(players, townType);
        if (cands.Count == 0) return;

        int total = cands.Sum(c => c.Weight);
        int roll = total == 0 ? 0 : (int)((uint)S.Rng.NextU64() % (uint)total);
        int pick = 0;
        for (int i = 0, run = 0; i < cands.Count; i++)
        {
            run += cands[i].Weight;
            if (roll < run) { pick = i; break; }
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var c = cands[(attempt + pick) % cands.Count];
            var entry = new RumourEntry();
            switch (c.Kind)
            {
                case 0:
                    var plain = S.Rumours.Plain[c.Index];
                    entry.Format = Db.RumourFormat("RUMOUR_FORMAT_PLAINTEXT");
                    entry.Text = plain.Text;
                    plain.Told++;
                    break;
                case 1 or 3 or 5:
                    entry.Format = Db.RumourFormat(c.Index != 0 ? "RUMOUR_FORMAT_OBJECT_FLOOR_UNDERGROUND" : "RUMOUR_FORMAT_OBJECT_FLOOR_SURFACE");
                    entry.Text = c.Text;
                    break;
                case 2 or 4 or 6:
                    entry.Format = c.Format;
                    entry.Text = c.Text;
                    break;
                default:
                    entry.Format = c.Format;
                    entry.Text = c.Text;
                    entry.Player = players[c.Index].Index + 1;
                    break;
            }
            if (entry.Format.Length == 0 || entry.SameAs(list.Count > 0 ? list[^1] : new RumourEntry())) continue;
            list.Add(entry);
            return;
        }
    }

    /// <summary>The weighted candidates <see cref="GenerateRumour"/> would draw from (plain weights already scaled).</summary>
    public IReadOnlyList<(int Kind, int Weight, int Index, string Format, string Text)> RumourCandidates(string townType) =>
        RumourCandidateList(RumourPlayers(), townType).Select(c => (c.Kind, c.Weight, c.Index, c.Format, c.Text)).ToList();

    /// <summary>World vf 0x68(1..8) with vf 0x6c = 1: the players in play, by id.</summary>
    List<PlayerState> RumourPlayers() => S.Players.Where(p => p.Alive).OrderBy(p => p.Index).ToList();

    List<RumourCandidate> RumourCandidateList(List<PlayerState> players, string townType)
    {
        var cands = new List<RumourCandidate>();
        bool underground = Map.Floor(1) != null;

        // Kinds 1/2: the Tear of Asha while it is buried (the manager's artifact, vf 0x18(0), alive).
        if (S.Grail is { } grail && !S.GrailFound && Core.Artifacts.TryGetValue(GrailArtifact, out var ga))
        {
            if (underground) cands.Add(new RumourCandidate { Weight = 100, Kind = 1, Text = ga.Name, Index = grail.Floor });
            if (RumourGroundFormat(grail) is { Length: > 0 } gf) cands.Add(new RumourCandidate { Weight = 100, Kind = 2, Text = ga.Name, Format = gf });
        }
        // Kinds 3/4: artifacts lying on the map, floor by floor (floor vf 0x18 list, object vf 0x20 → the artifact).
        for (int floor = 0; floor < 2; floor++)
        {
            if (Map.Floor(floor) == null) continue;
            foreach (var o in Map.Objects)
            {
                if (o.Kind != ObjectKind.Artifact || o.Pos.Floor != floor || S.Obj(o.Index).Removed) continue;
                var id = S.Obj(o.Index).ArtifactId ?? o.ArtifactId;
                if (id is null or "WAND_OF_X" or "SCROLL_OF_SPELL_X" || !Core.Artifacts.TryGetValue(id, out var ad) || ad.Type != "ARTF_CLASS_MAJOR") continue;
                if (underground) cands.Add(new RumourCandidate { Weight = 100, Kind = 3, Text = ad.Name, Index = floor });
                if (RumourGroundFormat(o.Pos) is { Length: > 0 } af) cands.Add(new RumourCandidate { Weight = 100, Kind = 4, Text = ad.Name, Format = af });
            }
        }
        // Kinds 7..0x17: the statistics.
        for (int kind = 7; kind < 0x18; kind++)
        {
            int best = -1, bestIdx = -1;
            bool first = true;
            for (int i = 0; i < players.Count; i++)
            {
                if (RumourStat(kind, players[i]) is not { } v) continue;
                if (v > 0 && best < v)
                {
                    if (best != -1) first = false;
                    best = v;
                    bestIdx = i;
                }
            }
            // As in the exe: only when the leader overtook an earlier positive player (the flag at [esp+0x17] is
            // cleared only on an update with a value already set).
            if (!first && bestIdx != -1)
                cands.Add(new RumourCandidate { Weight = 100, Kind = kind, Index = bestIdx, Format = Db.RumourFormat(StatFormats[kind - 7]) });
        }
        // Kinds 0x18..0x1c: the last events.
        for (int kind = 0x18; kind < 0x1d; kind++)
            for (int i = 0; i < players.Count && i < S.Rumours.Last.Length; i++)
            {
                var e = S.Rumours.Last[i];
                var (text, key) = kind switch
                {
                    0x18 => (e.Monster, "RUMOUR_FORMAT_PLAYER_LAST_MONSTER"),
                    0x19 => (e.Artifact, "RUMOUR_FORMAT_PLAYER_LAST_ARTIFACT"),
                    0x1a => (e.Capture, "RUMOUR_FORMAT_PLAYER_LAST_CAPTURE"),
                    0x1b => (e.Purchase, "RUMOUR_FORMAT_PLAYER_LAST_PURCHASE"),
                    _ => (e.Build, e.BuildLevel > 1 ? "RUMOUR_FORMAT_PLAYER_LAST_UPGRADE" : "RUMOUR_FORMAT_PLAYER_LAST_BUILD"),
                };
                if (text.Length > 0) cands.Add(new RumourCandidate { Weight = 100, Kind = kind, Text = text, Index = i, Format = Db.RumourFormat(key) });
            }

        // Plain rumours (008eb500 appends them after the others).
        int others = cands.Sum(c => c.Weight), firstPlain = cands.Count, plainSum = 0;
        for (int k = 0; k < S.Rumours.Plain.Count; k++)
        {
            var r = S.Rumours.Plain[k];
            if (r.TownType != RumourAnyTown && r.TownType != townType) continue;
            int w = r.Weight * 3 / (r.Told + 1);
            cands.Add(new RumourCandidate { Weight = w, Kind = 0, Index = k });
            plainSum += w;
        }
        int f = plainSum < 1 || others * 3 / plainSum < 1 ? 1 : others * 3 / plainSum;
        for (int i = firstPlain; i < cands.Count; i++) cands[i].Weight *= f;
        return cands;
    }

    /// <summary>
    /// A statistic of 008e85b0 for one player (32-bit values; null = the exe skips the player): 7 the sum and 8 the
    /// best of the heroes' army values (004eaaa0), 9..0xf gold, wood, ore, sulfur, crystal, gems, mercury (player vf
    /// 0xd8), 0x10 obelisks visited (none → skipped), 0x11 the best stack value (004e9b90), 0x12 the income value
    /// (vf 0xe8 → 004ea050), 0x13 owned mines (flagged objects of both floors whose vf 0x28 holds: CAdvMapMine,
    /// CAdvMapAbanMine, CAdvMapDwarvenWarren), 0x14 worn plus backpack artifacts, 0x15 the best hero estimate
    /// (004eb050), 0x16 towns, 0x17 the best town value (004eb480 with the day of the week). A player without heroes
    /// (7, 8, 0x11, 0x14, 0x15) or towns (0x17) is skipped.
    /// GAP-details: the estimates are the AI ports (OriginalAdventureAI) with their documented gaps; the income is the
    /// expected income (no Dwarven Warren draw); the hero estimate uses the freelancer role.
    /// </summary>
    int? RumourStat(int kind, PlayerState p)
    {
        var heroes = S.Heroes.Where(h => h.Alive && h.Owner == p.Index).ToList();
        var st = AiStats;
        switch (kind)
        {
            case 7 or 8 or 0x11 or 0x14 or 0x15 when heroes.Count == 0: return null;
            case 7: return heroes.Aggregate(0, (acc, h) => unchecked(acc + (int)AI.OriginalAdventureAI.ArmyValue(this, st, h.Army, h)));
            case 8: return heroes.Max(h => unchecked((int)AI.OriginalAdventureAI.ArmyValue(this, st, h.Army, h)));
            case 9: return p.Purse[Purse.Gold];
            case 10: return p.Purse[Purse.Wood];
            case 11: return p.Purse[Purse.Ore];
            case 12: return p.Purse[Purse.Sulfur];
            case 13: return p.Purse[Purse.Crystal];
            case 14: return p.Purse[Purse.Gem];
            case 15: return p.Purse[Purse.Mercury];
            case 0x10: return ObelisksVisited(p) is var n && n >= 1 ? n : null;
            case 0x11:
                int bestStack = 0;
                foreach (var h in heroes)
                    foreach (var s in h.Army)
                        if (s is { Count: > 0 }) bestStack = Math.Max(bestStack, unchecked((int)AI.OriginalAdventureAI.StackValue(this, st, s, h)));
                return bestStack;
            case 0x12: return unchecked((int)AI.OriginalAdventureAI.ResourceValue(this, p.Index, ExpectedIncome(p)));
            case 0x13:
                return Map.Objects.Count(o => o.Kind is ObjectKind.Mine or ObjectKind.AbandonedMine or ObjectKind.DwarvenWarren
                                              && !S.Obj(o.Index).Removed && S.Obj(o.Index).Owner == p.Index);
            case 0x14: return heroes.Sum(h => h.Artifacts.Count + h.Backpack.Count);
            case 0x15:
                var ai = new AI.OriginalAdventureAI();
                return heroes.Max(h => unchecked((int)ai.HeroEstimate(this, st, h, AI.HeroRole.Freelancer)));
            case 0x16: return S.Towns.Count(t => t.Owner == p.Index);
            case 0x17:
                var towns = S.Towns.Where(t => t.Owner == p.Index).ToList();
                if (towns.Count == 0) return null;
                var diff = st.Difficulty[Math.Clamp(S.Difficulty, 0, st.Difficulty.Length - 1)];
                return towns.Max(t => unchecked((int)AI.OriginalAdventureAI.TownValue(this, st, diff, t, p.Index)));
        }
        return null;
    }

    /// <summary>
    /// 008e7fb0: the ground type (00bcc310 → 004366d0) counted over the tiles within 5 of the object (clamped to the
    /// map); the first most frequent type, when it covers at least half of the tiles (integer half), names the
    /// ground: TT_DIRT, GRASS, SAND, SNOW, LAVA, SUBTERRANEAN (also DWARVENMINES), SMALL_WATER, BIG_WATER, TAIGA,
    /// WASTELAND → RUMOUR_FORMAT_OBJECT_GROUND_*; roads / none give no text.
    /// </summary>
    string RumourGroundFormat(Tile at)
    {
        if (Map.Floor(at.Floor) is not { } fl || fl.Ground.Length == 0) return "";
        int x0 = Math.Max(at.X - 5, 0), x1 = Math.Min(at.X + 5, fl.Width - 1);
        int y0 = Math.Max(at.Y - 5, 0), y1 = Math.Min(at.Y + 5, fl.Height - 1);
        var counts = new List<int>();
        int cells = 0;
        for (int y = y0; y <= y1; y++)
        {
            if (x0 <= x1) cells += x1 - x0 + 1;
            for (int x = x0; x <= x1; x++)
            {
                int g = fl.Ground[y * fl.Width + x];
                while (counts.Count <= g) counts.Add(0);
                counts[g]++;
            }
        }
        int top = -1;
        for (int i = 0; i < counts.Count; i++)
            if (top == -1 || counts[top] < counts[i]) top = i;
        if (top == -1 || counts[top] < cells / 2) return "";
        string? key = top switch
        {
            0 => "RUMOUR_FORMAT_OBJECT_GROUND_DIRT",
            1 => "RUMOUR_FORMAT_OBJECT_GROUND_GRASS",
            2 => "RUMOUR_FORMAT_OBJECT_GROUND_SAND",
            3 => "RUMOUR_FORMAT_OBJECT_GROUND_SNOW",
            4 => "RUMOUR_FORMAT_OBJECT_GROUND_LAVA",
            5 or 12 => "RUMOUR_FORMAT_OBJECT_GROUND_SUBTERRANEAN",
            10 => "RUMOUR_FORMAT_OBJECT_GROUND_SMALL_WATER",
            11 => "RUMOUR_FORMAT_OBJECT_GROUND_BIG_WATER",
            13 => "RUMOUR_FORMAT_OBJECT_GROUND_TAIGA",
            14 => "RUMOUR_FORMAT_OBJECT_GROUND_WASTELAND",
            _ => null,
        };
        return key == null ? "" : Db.RumourFormat(key);
    }
}
