using H5.Core.Data;
using H5.Core.IO;
using H5.Combat;

namespace H5.Combat.Tests;

/// <summary>Loads the rules database once from the user's installed game (tests require the game install).</summary>
public sealed class GameDataFixture : IDisposable
{
    public PakFileSystem Fs { get; }
    public GameDatabase Db { get; }

    public GameDataFixture()
    {
        Fs = PakFileSystem.OpenInstall(GameInstall.Locate());
        Db = GameDatabase.Load(Fs);
    }

    public void Dispose() => Fs.Dispose();

    /// <summary>Battle without heroes, one stack per side, on an empty field.</summary>
    public Battle Duel(string a, int countA, string b, int countB, ulong seed = 1, Cell? posA = null, Cell? posB = null)
    {
        var s = BattleSetup.Create(Db,
            new ArmySetup { Army = { new ArmySlot(a, countA) } },
            new ArmySetup { Army = { new ArmySlot(b, countB) } },
            seed, obstacles: Array.Empty<Cell>(), skipTactics: true);
        if (posA is Cell pa) s.Units[0].Pos = s.Units[0].StartPos = pa;
        if (posB is Cell pb) s.Units[1].Pos = s.Units[1].StartPos = pb;
        return new Battle(Db, s);
    }
}

[CollectionDefinition("game")]
public sealed class GameCollection : ICollectionFixture<GameDataFixture> { }
