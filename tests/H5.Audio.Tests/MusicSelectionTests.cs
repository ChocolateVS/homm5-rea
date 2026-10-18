using H5.Assets.Audio;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Audio.Tests;

public class MusicSelectionTests
{
    static MusicTrack T(string name) => new(name, name + ".ogg", 2000, 2000, 500, 15000);

    [Fact]
    public void CompositeNeverPlaysATrackThreeTimesInARow()
    {
        var a = T("a"); var b = T("b");
        var calc = CompositeMusicCalculator.Of(new[] { a, b });
        var rng = new Random(1);
        string? prev = null; int run = 0;
        for (int i = 0; i < 5000; i++)
        {
            var t = calc.Next(rng)!;
            run = t.Path == prev ? run + 1 : 1;
            prev = t.Path;
            Assert.True(run <= 2, $"track {t.Path} played {run} times in a row at draw {i}");
        }
    }

    [Fact]
    public void CompositeWithOneCandidateRepeatsIt()
    {
        var a = T("a");
        var calc = new CompositeMusicCalculator(new IMusicCalculator[] { new ConstantMusicCalculator(a), new ConstantMusicCalculator(null) });
        var rng = new Random(3);
        for (int i = 0; i < 10; i++) Assert.Same(a, calc.Next(rng));
    }

    [Fact]
    public void CompositeOfNothingIsSilent() =>
        Assert.Null(new CompositeMusicCalculator(new IMusicCalculator?[] { null, new ConstantMusicCalculator(null) }).Next(new Random()));

    [Fact]
    public void StartMusicPlaysOnceThenMainComposite()
    {
        var start = T("start"); var main = T("main");
        var calc = new StartThenMusicCalculator(new ConstantMusicCalculator(start), new ConstantMusicCalculator(main));
        var rng = new Random();
        Assert.Same(start, calc.Next(rng));
        for (int i = 0; i < 5; i++) Assert.Same(main, calc.Next(rng));
    }

    [Fact]
    public void SiegeMusicFirstThenCombat()
    {
        var siege = T("siege"); var combat = T("combat");
        var calc = new SiegeMusicCalculator(siege, new ConstantMusicCalculator(combat));
        var rng = new Random();
        Assert.Same(siege, calc.Next(rng));
        Assert.Same(combat, calc.Next(rng));
        Assert.Same(combat, calc.Next(rng));
    }

    [Fact]
    public void AmbientIsTheMajoritySetOfTheNineTilesAndShroudCountsAsInvisible()
    {
        // 3x3 around (5,5): 5 grass tiles, 4 unexplored → grass wins; with 5 unexplored → invisible.
        string? Tile(int x, int y) => "grass";
        bool Explored5(int x, int y) => (x + y) % 2 == 0;    // 5 of the 9 tiles around (5,5)
        Assert.Equal("grass", AdvAmbientPicker.Pick(5, 5, 10, 10, Tile, Explored5, "invisible", new Random(0)));
        bool Explored4(int x, int y) => (x + y) % 2 == 1;    // the other 4
        Assert.Equal("invisible", AdvAmbientPicker.Pick(5, 5, 10, 10, Tile, Explored4, "invisible", new Random(0)));
    }

    [Fact]
    public void AmbientClampsAtTheMapEdge() =>
        Assert.Equal("sand", AdvAmbientPicker.Pick(0, 0, 4, 4, (x, y) => "sand", (x, y) => true, null, new Random(0)));
}

/// <summary>Reads the real sound-routing data from the user's install.</summary>
public class MusicDataTests
{
    static readonly Lazy<(PakFileSystem Fs, MusicData Data, GameInstall Install)> Ctx = new(() =>
    {
        var install = GameInstall.Locate();
        var fs = PakFileSystem.OpenInstall(install);
        return (fs, new MusicData(new XdbLoader(fs)), install);
    });

    [Fact]
    public void RaceTableHasEveryFactionWithCombatMusic()
    {
        var d = Ctx.Value.Data;
        foreach (var race in new[] { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD", "TOWN_NO_TYPE" })
        {
            var r = d.Race(race)!;
            Assert.Equal(race, r.Race);
            Assert.NotEmpty(r.Combat);
        }
        Assert.Equal(2, d.Race("TOWN_HEAVEN")!.Combat.Count);
        Assert.Equal(3, d.Race("TOWN_INFERNO")!.Combat.Count);
        Assert.NotNull(d.Race("TOWN_STRONGHOLD")!.Town);
        Assert.NotNull(d.Race("TOWN_HEAVEN")!.TownAmbientSoundSet);
    }

    [Fact]
    public void EveryMusicDescriptorPointsAtAShippedOggFile()
    {
        var (fs, d, install) = Ctx.Value;
        var missing = new List<string>();
        int n = 0;
        foreach (var p in fs.Enumerate("Sounds/_(Music)/").Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)))
        {
            if (d.Track(p) is not { } t) continue;
            n++;
            if (MusicData.ResolveFile(install.Root, t.FileName) == null) missing.Add($"{p} → {t.FileName}");
        }
        Assert.True(n > 100, $"only {n} music descriptors");
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void UiGameRootMusicsAndSoundsResolve()
    {
        var d = Ctx.Value.Data;
        Assert.EndsWith("a2-MainMenu.xdb", d.UiMusic("MAIN_MENU_MUSIC_ID")!.Path);
        Assert.NotNull(d.UiMusic("MP_MAIN_MENU_MUSIC_ID"));
        Assert.NotNull(d.UiMusic("CREDITS_SCREEN_MUSIC_ID"));
        Assert.NotNull(d.UiMusic("HERO_FIND_GRAIL_MUSIC"));
        Assert.EndsWith("Level Up.xdb", d.UiSound("HERO_LEVEL_UP_SOUND"));
        Assert.EndsWith("Invisible.xdb", d.UiAmbient("INVISIBLE_AMBIENT_SOUND"));
        Assert.EndsWith("standart_click.xdb", d.UiConstSound("ButtonClickSound"));
    }

    [Fact]
    public void TerrainTilesCarryAmbienceAndMusic()
    {
        var d = Ctx.Value.Data;
        var (amb, music) = d.Tile("MapObjects/_(AdvMapTile)/Dirt/Ground.xdb");
        Assert.EndsWith("TerrainAmbients/Dirt.xdb", amb);
        Assert.EndsWith("AdvMap-Terrain/Dirt.xdb", music!.Path);
        var set = d.Ambient(amb)!;
        Assert.NotNull(set.Loop);
    }

    [Fact]
    public void CampaignMissionStartMusic()
    {
        var mm = Ctx.Value.Data.Mission("Campaigns/A2C1.(Campaign).xdb", 0);
        Assert.Contains(mm.Start, t => t.Path.EndsWith("a2-Ornella.xdb", StringComparison.OrdinalIgnoreCase));
    }
}
