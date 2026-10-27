using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Town spell offers per H5_Game.exe: guild generator FUN_00cd1b40 (schools FUN_00cd1990 from AdvMapTownShared
/// MagicSchool_0/_1 at +0x124/+0x128, per-circle counts table 00e95544), spell manager vtable 00e6eb00 (FUN_00a35080,
/// FUN_00a34e20, FUN_00a34f80), runes FUN_00ccfd30, warcries FUN_00ccfe90, offer list CTown vf 0x24 = FUN_00ccfab0,
/// rerolls on build in 00cd4a40.
/// </summary>
[Collection("adv")]
public class MageGuildTests(Fixture f)
{
    static Purse Rich() => new(new Resources(999, 999, 999, 999, 999, 999, 999999));

    /// <summary>A2M1 with its map spell list (map +0x1e0) cleared, so the manager registers the whole table.</summary>
    (Engine.Adventure G, TownState T) Town(string type)
    {
        var map = f.Loader.Load(H5.Adventure.Map.MapCatalog.Find(f.Fs, f.Install, "A2M1")!);
        map.SpellIds.Clear();
        var g = Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 7)).Game;
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Type = type;
        return (g, t);
    }

    string School(string id) => f.Db.Core.Spell(id)!.School;
    int Level(string id) => f.Db.Core.Spell(id)!.Level;

    [Fact]
    public void SpellEnumSlotsMatchTheExeConstants()
    {
        // FUN_00a33e20 / FUN_00a33f70 / FUN_00a349f0 test these SpellID values.
        Assert.Equal(0x43, f.Db.Core.Spell("SPELL_ABILITY_DARK_RITUAL")?.Index);
        Assert.Equal(0xd2, f.Db.Core.Spell("SPELL_MASS_CURSE")?.Index);
        Assert.Equal(0xdf, f.Db.Core.Spell("SPELL_EMPOWERED_MAGIC_ARROW")?.Index);
    }

    [Fact]
    public void EachCircleHasOneSpellOfEachTownSchoolAndFillsFromTheOtherTwo()
    {
        foreach (var type in new[] { "TOWN_HEAVEN", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_PRESERVE", "TOWN_FORTRESS" })
        {
            var (g, t) = Town(type);
            var (s0, s1) = f.Db.TownMagicSchools[type];
            for (int seed = 0; seed < 8; seed++)
            {
                g.S.Rng = new H5.Combat.Rng((ulong)(seed + 1));
                g.RollMageGuild(t);
                for (int lvl = 1; lvl <= 5; lvl++)
                {
                    var circle = t.MageGuild[lvl - 1];
                    Assert.All(circle, id => Assert.Equal(lvl, Level(id)));
                    var combat = circle.Where(id => School(id) != "MAGIC_SCHOOL_ADVENTURE").ToList();
                    // 00e95544: 3, 3, 3, 2, 2.
                    Assert.Equal(Engine.Adventure.GuildSpellsPerCircle[lvl], combat.Count);
                    Assert.Equal(combat.Count, combat.Distinct().Count());
                    Assert.Equal(1, combat.Count(id => School(id) == s0));
                    Assert.Equal(1, combat.Count(id => School(id) == s1));
                    // FUN_00a34e20 never registers mass / empowered versions.
                    Assert.DoesNotContain(combat, id => id.StartsWith("SPELL_MASS_") || id.StartsWith("SPELL_EMPOWERED_"));
                    // Every adventure spell of the circle is appended (bucket +0x24, FUN_00a34580).
                    var adventure = f.Db.Core.Spells.Values.Where(s => s.Stock && s.Level == lvl && s.School == "MAGIC_SCHOOL_ADVENTURE"
                        && s.Index is not (>= 0x15c and <= 0x15f)).Select(s => s.Id);
                    Assert.Equal(adventure.OrderBy(x => x), circle.Where(id => School(id) == "MAGIC_SCHOOL_ADVENTURE").OrderBy(x => x));
                }
                // The Library list (+0x1f4) gets one more spell of the other schools per circle.
                Assert.Equal(5, t.LibrarySpells.Count);
                Assert.All(t.LibrarySpells, id => Assert.True(School(id) != s0 && School(id) != s1 && School(id) != "MAGIC_SCHOOL_ADVENTURE"));
                for (int lvl = 1; lvl <= 5; lvl++) Assert.DoesNotContain(t.LibrarySpells.Single(id => Level(id) == lvl), t.MageGuild[lvl - 1]);
            }
        }
    }

    [Fact]
    public void TheMapSpellListLimitsTheGuild()
    {
        // FUN_00a35080: a non-empty map spellIDs list registers only those spells.
        var map = f.Map("A2M1");
        Assert.NotEmpty(map.SpellIds);
        var g = f.NewGame("A2M1");
        foreach (var t in g.S.Towns)
            foreach (var circle in t.MageGuild) Assert.All(circle, id => Assert.Contains(id, map.SpellIds));
    }

    [Fact]
    public void BuildingAGuildLevelRerollsThatCircleAndTheOnesAbove()
    {
        var (g, t) = Town("TOWN_HEAVEN");
        g.Current.Purse = Rich();
        t.Built["TB_MAGIC_GUILD"] = 1;
        t.MaxLevel["TB_MAGIC_GUILD"] = 5;
        // Mark circles with an impossible spell so a reroll is visible.
        for (int i = 0; i < 5; i++) t.MageGuild[i] = new() { "MARK" };
        foreach (var (k, _) in t.MaxLevel.ToList()) if (k != "TB_MAGIC_GUILD") t.Built[k] = g.Db.MaxLevel(t.Type, k);
        var r = g.Apply(new BuildCmd(t.Object, "TB_MAGIC_GUILD") { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { "MARK" }, t.MageGuild[0]);           // 00cd4a40: FUN_00cd1b40(old level + 1, 5)
        for (int i = 1; i < 5; i++) Assert.DoesNotContain("MARK", t.MageGuild[i]);
    }

    [Fact]
    public void RunesOnePerCircleAndOfferedByShrineLevel()
    {
        var (g, t) = Town("TOWN_FORTRESS");
        g.RollRunes(t);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, t.RuneSpells.Select(Level).OrderBy(x => x));
        Assert.All(t.RuneSpells, id => Assert.Equal("MAGIC_SCHOOL_RUNIC", School(id)));
        // FUN_00ccfab0: ability 0x20 → circles 1–2, 0x21 → up to 4, 0x22 → up to 5; no guild needed.
        t.Built["TB_MAGIC_GUILD"] = 0;
        int Offered(int shrine) { t.Built["TB_SPECIAL_1"] = shrine; return Enumerable.Range(1, 5).Count(l => g.TownSpellsAt(t, l).Any(id => School(id) == "MAGIC_SCHOOL_RUNIC")); }
        Assert.Equal(0, Offered(0));
        Assert.Equal(2, Offered(1));
        Assert.Equal(4, Offered(2));
        Assert.Equal(5, Offered(3));
    }

    [Fact]
    public void WarcriesCircleOneHasAllAndStrongholdHasNoGuild()
    {
        var (g, t) = Town("TOWN_STRONGHOLD");
        g.RollWarcries(t);
        var l1 = f.Db.Core.Spells.Values.Where(s => s.Stock && s.Level == 1 && s.School == "MAGIC_SCHOOL_WARCRIES").Select(s => s.Id).OrderBy(x => x);
        Assert.Equal(l1, t.WarcrySpells.Where(id => Level(id) == 1).OrderBy(x => x));
        Assert.Single(t.WarcrySpells, id => Level(id) == 2);
        Assert.Single(t.WarcrySpells, id => Level(id) == 3);
        t.Built["TB_MAGIC_GUILD"] = 5;
        t.Built["TB_SPECIAL_1"] = 1;
        Assert.All(g.TownSpellsAt(t, 1), id => Assert.Equal("MAGIC_SCHOOL_WARCRIES", School(id)));
        Assert.Empty(g.TownSpellsAt(t, 2));                       // 0x28 needed for circle 2
        t.Built["TB_SPECIAL_1"] = 3;
        Assert.Single(g.TownSpellsAt(t, 3));
        Assert.Empty(g.TownSpellsAt(t, 4));
    }

    [Fact]
    public void LibraryAddsItsSpellOnlyInAnAcademyWithTheGuildCircle()
    {
        var (g, t) = Town("TOWN_ACADEMY");
        g.RollMageGuild(t);
        t.Built["TB_MAGIC_GUILD"] = 2;
        t.Built["TB_SPECIAL_1"] = 0;
        Assert.Equal(t.MageGuild[0], g.TownSpellsAt(t, 1));
        t.Built["TB_SPECIAL_1"] = 1;
        Assert.Equal(t.MageGuild[0].Append(t.LibrarySpells.Single(id => Level(id) == 1)), g.TownSpellsAt(t, 1));
        Assert.Empty(g.TownSpellsAt(t, 3));                       // guild level 2: circle 3 not offered, Library neither
    }
}
