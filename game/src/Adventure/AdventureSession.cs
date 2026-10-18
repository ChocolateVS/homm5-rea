using Godot;
using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;

namespace H5Reborn.Adventure;

/// <summary>
/// A local adventure game: the rules engine (H5.Adventure) plus who controls each
/// player. Human players act through the adventure screen; computer players are
/// driven by <see cref="SimpleAdventureBot"/> (a stand-in until the original AI is
/// ported). Every event the engine emits, whoever caused it, is queued for the
/// screen to animate in order.
/// </summary>
public sealed class AdventureSession
{
    public AdventureDb Db { get; }
    public AdvMap Map { get; }
    public H5.Adventure.Engine.Adventure Game { get; }
    public AdvState S => Game.S;
    public ulong Seed { get; }

    readonly Queue<(AdvEvent Event, int Player)> _events = new();
    readonly H5.Adventure.AI.OriginalAdventureAI _bot = new();
    ulong _battleSeq;

    AdventureSession(AdventureDb db, AdvMap map, H5.Adventure.Engine.Adventure game, IEnumerable<AdvEvent> opening, ulong seed)
    {
        Db = db;
        Map = map;
        Game = game;
        Seed = seed;
        foreach (var e in opening) _events.Enqueue((e, -1));
        // Capture everything, including the bots' turns, so the map shows what happened.
        Game.Observer = (cmd, result) =>
        {
            if (!result.Ok) return;
            foreach (var e in result.Events) _events.Enqueue((e, cmd.Player));
        };
    }

    static AdventureDb? _db;

    /// <summary>Loads a map (catalogue id, short name or .h5m path) and starts a game with one human player.</summary>
    public static AdventureSession Start(string mapQuery, ulong seed, int humanMapPlayer = -1, int difficulty = 1)
    {
        var fs = H5Reborn.Game.Instance.Fs;
        var core = H5Reborn.Game.Instance.Db;
        _db ??= AdventureDb.Load(core, fs);
        var mapRef = MapCatalog.Find(fs, H5Reborn.Game.Instance.Install, mapQuery)
                     ?? throw new FileNotFoundException($"No adventure map matches '{mapQuery}'");
        var files = mapRef.Open(fs);
        var map = new MapLoader(core, fs).Load(files);

        var setup = new GameSetup { Seed = seed, Difficulty = difficulty };
        var active = map.Players.Where(p => p.Active).ToList();
        int human = humanMapPlayer >= 0 ? humanMapPlayer : active.FirstOrDefault(p => p.CanBeHuman)?.Index ?? active[0].Index;
        foreach (var p in active)
            setup.Players.Add(new PlayerSetup { MapPlayer = p.Index, Human = p.Index == human });
        var (game, opening) = H5.Adventure.Engine.Adventure.NewGame(_db, map, setup);
        GD.Print($"[H5] adventure {map.Id} \"{map.Name}\" {map.TilesX}x{map.TilesY}, human player {human + 1}, seed {seed}");
        return new AdventureSession(_db, map, game, opening, seed) { Files = files };
    }

    /// <summary>The map's own files (map-relative hrefs, user .h5m contents layered over the game data).</summary>
    public MapFiles Files { get; private init; } = null!;

    /// <summary>The (first) human player's state; the screen shows the map from their side.</summary>
    public PlayerState Human => S.Players.First(p => p.Human);
    public bool IsHumanTurn => Game.Current.Human && !S.Finished;

    public bool TryDequeue(out AdvEvent ev, out int player)
    {
        if (_events.TryDequeue(out var e)) { ev = e.Event; player = e.Player; return true; }
        ev = null!;
        player = -1;
        return false;
    }

    public int PendingEvents => _events.Count;

    /// <summary>Applies a command for the current (human) player.</summary>
    public AdvResult Apply(AdvCommand cmd)
    {
        var r = Game.Apply(cmd with { Player = Game.Current.Index });
        if (!r.Ok) GD.Print($"[H5] adventure command rejected: {cmd.GetType().Name}: {r.Error}");
        return r;
    }

    /// <summary>
    /// Plays every consecutive computer turn (stops when a human player is to move or
    /// the game ends). Battles between computer players, and battles a computer player
    /// starts against a human, are resolved with quick combat.
    /// </summary>
    public void RunComputerTurns(int maxTurns = 16)
    {
        for (int i = 0; i < maxTurns && !S.Finished && !Game.Current.Human; i++)
            _bot.PlayTurn(Game, Seed ^ (ulong)(S.Day * 977 + S.CurrentPlayer * 31) ^ ++_battleSeq);
    }

    // ---------------------------------------------------------------- battles

    /// <summary>Builds the pending battle (side 0 = attacking hero) for the combat screen.</summary>
    public BattleState CreatePendingBattle(out ulong seed)
    {
        seed = Seed ^ (0x9E3779B97F4A7C15UL * ++_battleSeq);
        return Game.CreatePendingBattle(seed);
    }

    /// <summary>Feeds a finished battle back to the adventure engine.</summary>
    public AdvResult ResolveBattle(BattleState finished, bool attackerFled)
    {
        List<ArmySlot> Survivors(int side) => finished.Units
            .Where(u => u.Side == side && u.Alive && !u.Summoned && !u.IsWarMachine)
            .GroupBy(u => u.CreatureId).Select(g => new ArmySlot(g.Key, g.Sum(u => u.Count))).ToList();
        int winner = finished.Winner ?? 1;
        return Apply(new ResolveBattleCmd(winner, Survivors(0), Survivors(1), attackerFled));
    }

    public AdvResult QuickCombat() => Apply(H5.Adventure.Engine.QuickCombat.Resolve(Game, Seed ^ ++_battleSeq, H5.Combat.AI.BattleAiRegistry.Create("original")));
}
