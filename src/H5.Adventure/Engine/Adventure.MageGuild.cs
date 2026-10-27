using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Town spell offers per H5_Game.exe: the magic guild (FUN_00cd1b40), the Academy Library's extra spell, the Fortress
/// Runic Shrine runes (FUN_00ccfd30) and the Stronghold warcries (FUN_00ccfe90), all drawn from the map's spell manager
/// (vtable 00e6eb00, filled by FUN_00a35080), and the list a town offers at a circle (CTown vf 0x24 = FUN_00ccfab0).
/// </summary>
public sealed partial class Adventure
{
    // MagicSchool enum (FUN_00acff90): 0 DESTRUCTIVE, 1 DARK, 2 LIGHT, 3 SUMMONING, 4 ADVENTURE, 5 RUNIC, 6 WARCRIES, 7 SPECIAL, 8 NONE.
    static readonly string[] SchoolEnum =
    {
        "MAGIC_SCHOOL_DESTRUCTIVE", "MAGIC_SCHOOL_DARK", "MAGIC_SCHOOL_LIGHT", "MAGIC_SCHOOL_SUMMONING",
        "MAGIC_SCHOOL_ADVENTURE", "MAGIC_SCHOOL_RUNIC", "MAGIC_SCHOOL_WARCRIES", "MAGIC_SCHOOL_SPECIAL", "MAGIC_SCHOOL_NONE",
    };
    const int SchoolAdventure = 4, SchoolRunic = 5, SchoolWarcries = 6, SchoolSpecial = 7, SchoolNone = 8;
    // SpellID values the manager tests by number.
    const int SpellDarkRitual = 0x43, SpellDebugTeleport = 0xd0;

    /// <summary>Spells per guild circle: table 00e95544 indexed by min(max(circle, 0), 5) = {0, 3, 3, 3, 2, 2}.</summary>
    public static readonly int[] GuildSpellsPerCircle = { 0, 3, 3, 3, 2, 2 };

    static int SchoolNo(string school) => Array.IndexOf(SchoolEnum, school) is var i and >= 0 ? i : SchoolNone;

    /// <summary>The map's spell manager buckets (vtable 00e6eb00): +0x24 all, +0x30 non-adventure, +0x3c runes, +0x60 warcries, per level.</summary>
    sealed class SpellBuckets
    {
        public readonly List<string>[] All = NewLevels(), Combat = NewLevels(), Runic = NewLevels(), Warcry = NewLevels();
        public readonly HashSet<string> Known = new(StringComparer.Ordinal);
        static List<string>[] NewLevels() => Enumerable.Range(0, 6).Select(_ => new List<string>()).ToArray();
    }

    SpellBuckets? _spellBuckets;
    SpellBuckets Buckets => _spellBuckets ??= BuildSpellBuckets();

    /// <summary>
    /// FUN_00a35080 (manager slot 0): the map's spellIDs (map +0x1e0, in their order) are registered; an empty list
    /// registers every spell of the table (FUN_00a34f50, slots 0..count−1). With a list, a map under Maps/Multiplayer
    /// (FUN_00c9d9e0) whose Version (+0x48) is below 2 also gets every rune, below 3 every warcry.
    /// </summary>
    SpellBuckets BuildSpellBuckets()
    {
        var b = new SpellBuckets();
        var table = Core.Spells.Values.Where(s => s.Stock && s.Index >= 0).OrderBy(s => s.Index).ToList();
        if (Map.SpellIds.Count == 0)
        {
            foreach (var s in table) RegisterSpell(b, s);
            return b;
        }
        foreach (var id in Map.SpellIds) if (Core.Spell(id) is { } s) RegisterSpell(b, s);
        bool multiplayer = Map.Id.Contains("Maps/Multiplayer", StringComparison.Ordinal) || Map.Id.Contains("Maps\\Multiplayer", StringComparison.Ordinal);
        if (Map.Version < 2 && multiplayer)
            foreach (var s in table.Where(s => s.Index != 0 && SchoolNo(s.School) == SchoolRunic)) RegisterSpell(b, s);
        if (Map.Version < 3 && multiplayer)
            foreach (var s in table.Where(s => s.Index != 0 && SchoolNo(s.School) == SchoolWarcries)) RegisterSpell(b, s);
        return b;
    }

    /// <summary>Whether FUN_00a34e20 skips a spell: mass versions (FUN_00a33e20) and empowered ones (FUN_00a33f70).</summary>
    static bool MassOrEmpowered(int index) => index is >= 0xd2 and <= 0xdd or >= 0xdf and <= 0xe9 or 0x12a;

    /// <summary>
    /// FUN_00a34e20: valid (FUN_00a349f0: runes and warcries always; otherwise not Dark Ritual and not SPECIAL / NONE),
    /// not SPELL_ABILITY_CUSTOM1..4 (0x15c..0x15f), not a mass or empowered spell, circle 1..5 (warcries 1..3), not yet
    /// registered (slot 0x18 = FUN_00a34af0). Runes go to +0x3c, warcries to +0x60, the rest to +0x24 and, unless
    /// adventure (FUN_00a34580: school 4 or SPELL_DEBUG_TELEPORT), to +0x30 too.
    /// </summary>
    static void RegisterSpell(SpellBuckets b, SpellDef s)
    {
        int idx = s.Index, school = SchoolNo(s.School), lvl = s.Level;
        if (idx == 0) return;
        bool valid = school is SchoolRunic or SchoolWarcries || (idx != SpellDarkRitual && school is not SchoolSpecial and not SchoolNone);
        if (!valid || idx is >= 0x15c and <= 0x15f || MassOrEmpowered(idx)) return;
        if (school == SchoolWarcries ? lvl is < 1 or > 3 : lvl is < 1 or > 5) return;
        if (!b.Known.Add(s.Id)) return;
        if (school == SchoolRunic) b.Runic[lvl].Add(s.Id);
        else if (school == SchoolWarcries) b.Warcry[lvl].Add(s.Id);
        else
        {
            b.All[lvl].Add(s.Id);
            if (!(school == SchoolAdventure || idx == SpellDebugTeleport)) b.Combat[lvl].Add(s.Id);
        }
    }

    /// <summary>
    /// Manager slot 0x10 = FUN_00a34f80: the spells of one circle and school — school 8 every non-adventure spell,
    /// 5 the runes, 6 the warcries (circles 1..3), otherwise the non-adventure spells of that school.
    /// </summary>
    List<string> SpellsOf(int level, int school)
    {
        if (level is < 1 or > 5) return new();
        var b = Buckets;
        return school switch
        {
            SchoolNone => b.Combat[level].ToList(),
            SchoolRunic => b.Runic[level].ToList(),
            SchoolWarcries => level <= 3 ? b.Warcry[level].ToList() : new(),
            _ => b.Combat[level].Where(id => SchoolNo(Core.Spell(id)!.School) == school).ToList(),
        };
    }

    /// <summary>Manager slot 8 → slot 4 (FUN_00a34d60): one random spell of <see cref="SpellsOf"/>, null when there is none.</summary>
    string? RandomSpellOf(int level, int school)
    {
        var l = SpellsOf(level, school);
        return l.Count == 0 ? null : l[S.Rng.Next(l.Count)];
    }

    /// <summary>
    /// FUN_00cd1990: the town's two schools are the shared record's MagicSchool_0 / _1 (no record: LIGHT, DARK); the
    /// other two come from the list {LIGHT, DARK, DESTRUCTIVE, SUMMONING} with the first removed properly and the second
    /// shifted out without shrinking the list (the exe's erase leaves the end pointer), then its slots 0 and 1.
    /// </summary>
    (int Main, int Second, int Other1, int Other2) GuildSchools(string townType)
    {
        var list = new List<int> { 2, 1, 0, 3 };
        int main = list[0], second = list[1];
        if (Db.TownMagicSchools.TryGetValue(townType, out var ms)) { main = SchoolNo(ms.School0); second = SchoolNo(ms.School1); }
        int i = list.IndexOf(main);
        if (i >= 0) list.RemoveAt(i);
        i = list.IndexOf(second);
        if (i >= 0) { for (int k = i; k + 1 < list.Count; k++) list[k] = list[k + 1]; }
        return (main, second, list[0], list[1]);
    }

    /// <summary>
    /// FUN_00cd1b40(from, to): rerolls guild circles from..to (their spells leave the guild list +0x190 and the
    /// Library list +0x1f4). Per circle: one random spell of each town school; then random picks from the other two
    /// schools until the circle holds GuildSpellsPerCircle; then, unless those ran out, one more of them for the
    /// Library; then every adventure spell of the circle (bucket +0x24 filtered by FUN_00a34580).
    /// Called with (1, 5) when the town is placed (FUN_00cd79d0) and with (old level + 1, 5) when a guild level is
    /// built (00cd4a40).
    /// </summary>
    public void RollMageGuild(TownState t, int from = 1, int to = 5)
    {
        t.LibrarySpells.RemoveAll(id => Core.Spell(id)?.Level is int l && l >= from && l <= to);
        var (main, second, other1, other2) = GuildSchools(t.Type);
        for (int lvl = Math.Max(from, 1); lvl <= to && lvl <= 5; lvl++)
        {
            var list = new List<string>();
            int got = 0;
            if (RandomSpellOf(lvl, main) is { } a) { list.Add(a); got++; }
            if (RandomSpellOf(lvl, second) is { } b) { list.Add(b); got++; }
            var cand = SpellsOf(lvl, other1);
            cand.AddRange(SpellsOf(lvl, other2));
            int want = GuildSpellsPerCircle[Math.Clamp(lvl, 0, 5)] - got;
            bool ranOut = false;
            for (int k = 0; k < want; k++)
            {
                if (cand.Count == 0) { ranOut = true; break; }
                int i = S.Rng.Next(cand.Count);
                list.Add(cand[i]);
                cand.RemoveAt(i);
            }
            if (!ranOut && cand.Count > 0)
            {
                int i = S.Rng.Next(cand.Count);
                t.LibrarySpells.Add(cand[i]);
            }
            foreach (var id in Buckets.All[lvl])
                if (Core.Spell(id) is { } d && (SchoolNo(d.School) == SchoolAdventure || d.Index == SpellDebugTeleport)) list.Add(id);
            t.MageGuild[lvl - 1] = list;
        }
    }

    /// <summary>FUN_00ccfd30(from, to): drops the runes of circles from..to (+0x2c4), then one random rune per circle.</summary>
    public void RollRunes(TownState t, int from = 1, int to = 5)
    {
        t.RuneSpells.RemoveAll(id => Core.Spell(id)?.Level is int l && l >= from && l <= to);
        for (int lvl = from; lvl <= to; lvl++)
            if (RandomSpellOf(lvl, SchoolRunic) is { } r) t.RuneSpells.Add(r);
    }

    /// <summary>FUN_00ccfe90(from, to): drops the warcries of circles from..to (+0x2d8); circle 1 gets every warcry of
    /// that circle, the others one random warcry each.</summary>
    public void RollWarcries(TownState t, int from = 1, int to = 3)
    {
        t.WarcrySpells.RemoveAll(id => Core.Spell(id)?.Level is int l && l >= from && l <= to);
        for (int lvl = from; lvl <= to; lvl++)
        {
            if (lvl == 1) t.WarcrySpells.AddRange(SpellsOf(1, SchoolWarcries));
            else if (RandomSpellOf(lvl, SchoolWarcries) is { } w) t.WarcrySpells.Add(w);
        }
    }

    /// <summary>Town placement (FUN_00cd79d0): FUN_00cd1b40(1, 5), FUN_00ccfd30(1, 5), FUN_00ccfe90(1, 3) for every town.</summary>
    void RollTownSpells(TownState t)
    {
        RollMageGuild(t, 1, 5);
        RollRunes(t, 1, 5);
        RollWarcries(t, 1, 3);
    }

    /// <summary>
    /// The spell side of building one level (00cd4a40, before the level is raised): magic guild (type 6) rerolls
    /// circles old + 1 .. 5; town ability 0x20 (Fortress TB_SPECIAL_1) at levels 0 / 1 / 2 rolls runes 1–2 / 3–4 / 5;
    /// ability 0x27 (Stronghold TB_SPECIAL_1) at levels 0 / 1 / 2 rolls warcry circle 1 / 2 / 3.
    /// </summary>
    void TownSpellsOnBuild(TownState t, string building, int oldLevel)
    {
        if (building == "TB_MAGIC_GUILD") RollMageGuild(t, oldLevel + 1, 5);
        if (building != "TB_SPECIAL_1") return;
        if (t.Type == "TOWN_FORTRESS")
        {
            if (oldLevel == 0) RollRunes(t, 1, 2);
            else if (oldLevel == 1) RollRunes(t, 3, 4);
            else if (oldLevel == 2) RollRunes(t, 5, 5);
        }
        else if (t.Type == "TOWN_STRONGHOLD" && oldLevel is >= 0 and <= 2)
            RollWarcries(t, oldLevel + 1, oldLevel + 1);
    }

    /// <summary>
    /// CTown vf 0x24 = FUN_00ccfab0: the spells a town offers at circle <paramref name="level"/>, in this order:
    /// warcries with ability 0x27 (circle 1; 0x28 up to 2; 0x29 any); unless Stronghold, the guild's spells of the circle
    /// when the guild (type 6) is at least that level, followed by the Library's (ability 0x15, Academy TB_SPECIAL_1); runes
    /// with ability 0x20 (circles 1–2; 0x21 up to 4; 0x22 up to 5). Abilities are table 00f7c998 rows (FUN_00cce1c0: level ≥).
    /// </summary>
    public List<string> TownSpellsAt(TownState t, int level)
    {
        var res = new List<string>();
        bool Lvl(string id) => Core.Spell(id)?.Level == level;
        int s1 = t.Level("TB_SPECIAL_1");
        if (t.Type == "TOWN_STRONGHOLD" && s1 >= 1 && (level < 2 || (s1 >= 2 && level < 3) || s1 >= 3))
            res.AddRange(t.WarcrySpells.Where(Lvl));
        if (t.Type != "TOWN_STRONGHOLD")
        {
            int g = t.Level("TB_MAGIC_GUILD");
            if (level <= g && g != 0 && level is >= 1 and <= 5)
            {
                res.AddRange(t.MageGuild[level - 1].Where(Lvl));
                if (t.Type == "TOWN_ACADEMY" && s1 >= 1) res.AddRange(t.LibrarySpells.Where(Lvl));
            }
        }
        if (t.Type == "TOWN_FORTRESS" && s1 >= 1 && (level < 3 || (s1 >= 2 && level < 5) || (s1 >= 3 && level < 6)))
            res.AddRange(t.RuneSpells.Where(Lvl));
        return res;
    }
    /// <summary>The guild screen's building (FUN_006145a0): TB_SPECIAL_1 (0x11) in a Stronghold (town type 10), else TB_MAGIC_GUILD (6).</summary>
    public static string GuildScreenBuilding(TownState t) => t.Type == "TOWN_STRONGHOLD" ? "TB_SPECIAL_1" : "TB_MAGIC_GUILD";

    /// <summary>
    /// The magic guild screen's pages (FUN_006309d0, called from the screen setup 00631110). G = the level of
    /// <see cref="GuildScreenBuilding"/> (town vfunc 0x34). For i = 1, 2, … while the bookmark ("OrcsButton%02d" in a
    /// Stronghold, else "Button%02d") exists: the circle's offer (vf 0x24, <see cref="TownSpellsAt"/>) loses its
    /// adventure spells (school 4: to the adventure list when i ≤ G), runes (5: to the rune list, any i) and warcries
    /// (6: to the warcry list when i ≤ G); the page (caption T_ORCS_CIRCLE_i / T_CIRCLE_i) holds the warcries in a
    /// Stronghold, else what is left, and only when i ≤ G (its bookmark shown iff i ≤ G). Then, with a non-empty
    /// adventure list the "Button06" page takes it, and with a non-empty rune list the "Button07" page takes the runes
    /// (both bookmarks shown; nothing when no such page exists).
    /// </summary>
    public List<GuildPage> GuildScreenPages(TownState t, Func<string, bool> bookmarkExists)
    {
        bool orcs = t.Type == "TOWN_STRONGHOLD";
        int g = t.Level(GuildScreenBuilding(t));
        var pages = new List<GuildPage>();
        var adventure = new List<string>();
        var runes = new List<string>();
        for (int i = 1; ; i++)
        {
            string name = (orcs ? "OrcsButton" : "Button") + i.ToString("00");
            if (!bookmarkExists(name)) break;
            var rest = new List<string>();
            var cries = new List<string>();
            foreach (var id in TownSpellsAt(t, i))
                switch (Core.Spell(id)?.School)
                {
                    case "MAGIC_SCHOOL_ADVENTURE": if (i <= g) adventure.Add(id); break;
                    case "MAGIC_SCHOOL_RUNIC": runes.Add(id); break;
                    case "MAGIC_SCHOOL_WARCRIES": if (i <= g) cries.Add(id); break;
                    default: rest.Add(id); break;
                }
            pages.Add(new GuildPage(name, (orcs ? "T_ORCS_CIRCLE_" : "T_CIRCLE_") + i, i <= g ? (orcs ? cries : rest) : new(), i <= g, false));
        }
        int Find(string n) => pages.FindIndex(p => p.Bookmark == n);
        if (pages.Count > 0 && adventure.Count > 0 && Find("Button06") is var a and >= 0)
            pages[a] = pages[a] with { Spells = adventure, Shown = true };
        if (pages.Count > 0 && runes.Count > 0 && Find("Button07") is var r and >= 0)
            pages[r] = pages[r] with { Spells = runes, Shown = true, Runes = true };
        return pages;
    }
}

/// <summary>One magic guild screen page (FUN_006309d0): bookmark name, caption text key, spells, bookmark shown, rune panel.</summary>
public sealed record GuildPage(string Bookmark, string Caption, List<string> Spells, bool Shown, bool Runes);

