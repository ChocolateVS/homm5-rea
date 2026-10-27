using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;
using H5.Scripting.Runtime;

namespace H5.Save.Tests;

public sealed class Fixture
{
    public readonly GameInstall Install = GameInstall.Locate();
    public readonly PakFileSystem Fs;
    public readonly GameDatabase Core;
    public readonly AdventureDb Db;

    public Fixture()
    {
        Fs = PakFileSystem.OpenInstall(Install);
        Core = GameDatabase.Load(Fs);
        Db = AdventureDb.Load(Core, Fs);
    }

    public (MapFiles Files, AdvMap Map) Open(string id)
    {
        var files = MapCatalog.Find(Fs, Install, id)!.Open(Fs);
        return (files, new MapLoader(Core, Fs).Load(files));
    }

    /// <summary>Reloads a map from scratch the way loading a save does (no shared AdvMap instance).</summary>
    public (MapFiles Files, AdvMap Map) Reopen(MapSpec spec)
    {
        var files = AdventureSave.OpenMap(spec, Fs);
        return (files, new MapLoader(Core, Fs).Load(files));
    }
}

[CollectionDefinition("save")]
public sealed class SaveCollection : ICollectionFixture<Fixture> { }

public class ArchiveTests
{
    static SaveArchive Sample()
    {
        var s = new SaveArchive { Header = new SaveHeader { Name = "quick", Day = 3, StateHash = "abc" } };
        s.SetText("adventure.json", "{\"x\":1}");
        s.Parts["thumbnail.png"] = new byte[] { 1, 2, 3 };
        return s;
    }

    [Fact]
    public void RoundTripsAndIsDeterministic()
    {
        var bytes = Sample().ToBytes();
        Assert.Equal(bytes, Sample().ToBytes());
        var back = SaveArchive.FromBytes(bytes);
        Assert.Equal("quick", back.Header.Name);
        Assert.Equal("{\"x\":1}", back.GetText("adventure.json"));
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Parts["thumbnail.png"]);
        Assert.Equal(3, SaveArchive.ReadHeader(bytes).Day);
    }

    [Fact]
    public void RejectsDamagedFiles()
    {
        var bytes = Sample().ToBytes();
        Assert.Throws<SaveCorruptException>(() => SaveArchive.FromBytes(bytes[..(bytes.Length / 2)]));
        Assert.Throws<SaveCorruptException>(() => SaveArchive.FromBytes(new byte[] { 1, 2, 3, 4 }));

        // Swap a part's content inside an otherwise valid zip: the manifest hash catches it.
        var tampered = Sample();
        var good = tampered.ToBytes();
        using var ms = new MemoryStream();
        ms.Write(good);
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("adventure.json")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("adventure.json").Open());
            w.Write("{\"x\":2}");
        }
        Assert.Throws<SaveCorruptException>(() => SaveArchive.FromBytes(ms.ToArray()));
    }

    [Fact]
    public void SlotNamingFollowsTheOriginal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "h5save_" + Guid.NewGuid().ToString("N"));
        try
        {
            var st = new DirectorySaveStorage(dir);
            Assert.Equal("save_0.sav", SaveSlots.NextFileName(st));
            var a = Sample();
            Assert.Equal("save_0.sav", SaveSlots.Write(st, a));
            var b = Sample(); b.Header.Name = "my game";
            Assert.Equal("save_1.sav", SaveSlots.Write(st, b));
            // Same name overwrites in place (quicksave/autosave behaviour).
            var c = Sample(); c.Header.Day = 9;
            Assert.Equal("save_0.sav", SaveSlots.Write(st, c));
            Assert.Equal(9, SaveSlots.FindByName(st, "QUICK")!.Header.Day);
            st.Delete("save_0.sav");
            Assert.Equal("save_0.sav", SaveSlots.NextFileName(st));
            File.WriteAllBytes(Path.Combine(dir, "save_5.sav"), new byte[] { 0 });
            var damaged = new List<string>();
            Assert.Single(SaveSlots.List(st, damaged));
            Assert.Equal(new[] { "save_5.sav" }, damaged);
            Assert.Equal("Autosave 12", SaveSlots.Format("Autosave <value=id>", "12"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

[Collection("save")]
public class AdventureSaveTests(Fixture f)
{
    static void Play(Adventure.Engine.Adventure g, int turns, ulong seed)
    {
        var bot = new SimpleAdventureBot();
        for (int i = 0; i < turns && !g.S.Finished; i++) bot.PlayTurn(g, seed + (ulong)i);
    }

    [Fact]
    public void SaveLoadContinuesIdenticallyOnAFreshMap()
    {
        var (_, map) = f.Open("A2M1");
        var g = Adventure.Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 21)).Game;
        Play(g, 10, 3);
        Assert.Null(AdventureSave.CannotSaveReason(g));

        var save = AdventureSave.Capture(new AdventureSave.CaptureInput { Game = g, Map = AdventureSave.SpecFor(map), Name = "test" });
        var bytes = save.ToBytes();

        var loaded = SaveArchive.FromBytes(bytes);
        var (_, freshMap) = f.Reopen(loaded.Header.Map);
        var copy = AdventureSave.Restore(loaded, f.Db, freshMap);
        Assert.Equal(g.S.ComputeHash(), copy.S.ComputeHash());
        Assert.Equal(g.S.Day, loaded.Header.Day);

        Play(g, 12, 77);
        Play(copy, 12, 77);
        Assert.Equal(g.S.ComputeHash(), copy.S.ComputeHash());
    }

    [Fact]
    public void RestoreRejectsAStateThatDoesNotMatchItsHeader()
    {
        var (_, map) = f.Open("A2M1");
        var g = Adventure.Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 5)).Game;
        var save = AdventureSave.Capture(new AdventureSave.CaptureInput { Game = g, Map = AdventureSave.SpecFor(map), Name = "x" });
        save.Header.StateHash = "0000000000000000";
        Assert.Throws<SaveCorruptException>(() => AdventureSave.Restore(save, f.Db, map));
    }

    [Fact]
    public void ScriptedMapSaveLoadContinuesIdentically()
    {
        // A campaign map with an active Lua script: threads are mid-sleep when we save.
        var (files, map) = f.Open("A2C1M1");
        var ids = new EnumTables(f.Fs);
        var catalog = new SharedCatalog(new XdbLoader(f.Fs));

        var g = Adventure.Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 9)).Game;
        var host = AdventureScripting.Create(g, files, ids, catalog);
        host.AutoAnswerUi = true;
        host.Start(map.ScriptPath, map.ScriptSource);
        var bot = new SimpleAdventureBot();
        void Step(Adventure.Engine.Adventure game, AdventureScripting h, int turns, ulong seed)
        {
            for (int t = 0; t < turns && !game.S.Finished; t++)
            {
                for (int guard = 0; h.World.GameBlocked && guard < 3000; guard++) h.Tick();
                bot.PlayTurn(game, seed * 1000003UL + (ulong)t);
                h.Tick(20);
            }
        }
        Step(g, host, 4, 1);

        var save = AdventureSave.Capture(new AdventureSave.CaptureInput
        {
            Game = g, Map = AdventureSave.SpecFor(map), Name = "scripted", Scripts = host.Save(), Kind = SaveKind.Campaign,
        });
        var loaded = SaveArchive.FromBytes(save.ToBytes());

        var (files2, map2) = f.Reopen(loaded.Header.Map);
        var g2 = AdventureSave.Restore(loaded, f.Db, map2);
        var host2 = AdventureScripting.Create(g2, files2, ids, catalog);
        host2.AutoAnswerUi = true;
        host2.Load(loaded.GetText(AdventureSave.ScriptsPart)!);
        Assert.Equal(host.ComputeHash(), host2.ComputeHash());
        Assert.Equal(g.S.ComputeHash(), g2.S.ComputeHash());

        Step(g, host, 4, 2);
        Step(g2, host2, 4, 2);
        Assert.Equal(g.S.ComputeHash(), g2.S.ComputeHash());
        Assert.Equal(host.ComputeHash(), host2.ComputeHash());
    }
}

/// <summary>Automatic save names (H5_Game.exe 007f9e10): ids 1..3, then the oldest is overwritten.</summary>
public class RotatingNameTests
{
    [Fact]
    public void UsesTheFirstFreeIdThenTheOldest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "h5save-rot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new DirectorySaveStorage(dir);
            const string template = "Autosave <value=id>";
            void Write(string name, DateTime when) =>
                SaveSlots.Write(storage, new SaveArchive { Header = new SaveHeader { Name = name, SavedUtc = when } });
            Assert.Equal("Autosave 1", SaveSlots.RotatingName(storage, template));
            Write("Autosave 1", new DateTime(2020, 1, 3));
            Assert.Equal("Autosave 2", SaveSlots.RotatingName(storage, template));
            Write("Autosave 2", new DateTime(2020, 1, 1));
            Write("Autosave 3", new DateTime(2020, 1, 2));
            Assert.Equal("Autosave 2", SaveSlots.RotatingName(storage, template));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
