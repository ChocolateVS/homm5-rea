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

    /// <summary>Adventure rules data shared by every session (loaded on first use).</summary>
    public static AdventureDb SharedDb => _db ??= AdventureDb.Load(H5Reborn.Game.Instance.Db, H5Reborn.Game.Instance.Fs);

    /// <summary>Battle seed counter; saved with the game so seeds continue identically after loading.</summary>
    public ulong BattleSeq => _battleSeq;

    /// <summary>Rebuilds a session around a game restored from a save (game/src/Save).</summary>
    public static AdventureSession Restore(MapFiles files, H5.Adventure.Engine.Adventure game, ulong seed, ulong battleSeq) =>
        new(SharedDb, game.Map, game, Array.Empty<AdvEvent>(), seed) { Files = files, _battleSeq = battleSeq };

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

    /// <summary>A networked game: the host simulates; this session only shows the view and sends commands.</summary>
    public H5Reborn.AdvNet.RemoteAdventure? Remote { get; private init; }
    public bool IsRemote => Remote != null;
    /// <summary>Several humans share this machine (hot-seat): the view follows whichever human is to move.</summary>
    public bool IsHotSeat => Remote == null && S.Players.Count(p => p.Human) > 1;
    int _viewer = -1;

    /// <summary>
    /// The player whose side the screen shows: this machine's player in a network game, the human to move
    /// in hot-seat (the last one during computer turns), else the single human.
    /// </summary>
    public PlayerState Human
    {
        get
        {
            if (Remote != null) return S.Players.First(p => p.Index == Remote.MyPlayer);
            if (Game.Current.Human) _viewer = Game.Current.Index;
            return S.Players.FirstOrDefault(p => p.Index == _viewer) ?? S.Players.First(p => p.Human);
        }
    }
    public bool IsHumanTurn => Remote != null ? Remote.IsMyTurn && !S.Finished : Game.Current.Human && !S.Finished;

    /// <summary>Opens the adventure screen's session for a networked game (AdvNetScreens.OpenNetworkGame).</summary>
    public static AdventureSession FromRemote(H5Reborn.AdvNet.RemoteAdventure remote)
    {
        var lm = remote.Net.Replica.Map;
        return new AdventureSession(lm.Db, lm.Map, remote.Game, Array.Empty<AdvEvent>(), 0)
        {
            Files = lm.Files ?? MapFiles.FromPak(H5Reborn.Game.Instance.Fs, "Maps/Generated/map.xdb"),
            Remote = remote,
        };
    }

    /// <summary>A local game on a loaded map (stock, user or generated) with the given map players as humans (hot-seat).</summary>
    public static AdventureSession StartLocal(H5.AdvNet.LoadedMap lm, IEnumerable<int> humanMapPlayers, ulong seed, int difficulty)
    {
        var humans = humanMapPlayers.ToHashSet();
        var setup = new GameSetup { Seed = seed, Difficulty = difficulty };
        foreach (var p in lm.Map.Players.Where(p => p.Active))
            setup.Players.Add(new PlayerSetup { MapPlayer = p.Index, Human = humans.Contains(p.Index) });
        var (game, opening) = H5.Adventure.Engine.Adventure.NewGame(lm.Db, lm.Map, setup);
        GD.Print($"[H5] adventure {lm.Map.Id} local game, humans {string.Join(",", humans.Select(h => h + 1))}, seed {seed}");
        return new AdventureSession(lm.Db, lm.Map, game, opening, seed)
        {
            Files = lm.Files ?? MapFiles.FromPak(H5Reborn.Game.Instance.Fs, "Maps/Generated/map.xdb"),
        };
    }

    public bool TryDequeue(out AdvEvent ev, out int player)
    {
        if (Remote != null && _events.Count == 0 && Remote.TryDequeue(out var re))
        {
            // Host events carry no cause; treat them as the current player's.
            ev = re;
            player = Game.Current.Index;
            return true;
        }
        if (_events.TryDequeue(out var e)) { ev = e.Event; player = e.Player; return true; }
        ev = null!;
        player = -1;
        return false;
    }

    public int PendingEvents => _events.Count;

    /// <summary>
    /// Queues events produced outside a player command (map scripts) so they animate like any
    /// other; a trailing <see cref="ScriptResync"/> brings the view fully in line afterwards.
    /// </summary>
    public void EnqueueScripted(IEnumerable<AdvEvent> events)
    {
        foreach (var e in events) _events.Enqueue((e, -1));
        _events.Enqueue((new ScriptResync(), -1));
    }

    /// <summary>Applies a command for the current (human) player.</summary>
    public AdvResult Apply(AdvCommand cmd)
    {
        // Network game: the host applies it; the outcome arrives as an update.
        if (Remote != null) return Remote.Apply(cmd with { Player = Remote.MyPlayer });
        // Through the script host's sink when one is attached, so map triggers see the human's actions.
        var c = cmd with { Player = Game.Current.Index };
        var r = Game.CommandSink?.Invoke(c) ?? Game.Apply(c);
        if (!r.Ok) GD.Print($"[H5] adventure command rejected: {cmd.GetType().Name}: {r.Error}");
        else H5Reborn.Audio.AdventureEventAudio.OnHumanCommand(c);
        return r;
    }

    /// <summary>
    /// Plays every consecutive computer turn (stops when a human player is to move or
    /// the game ends). Battles between computer players, and battles a computer player
    /// starts against a human, are resolved with quick combat.
    /// </summary>
    public void RunComputerTurns(int maxTurns = 16)
    {
        if (Remote != null) return; // the host plays computer turns
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

/// <summary>Client-side marker queued after script-made events: resync every model with the state.</summary>
public sealed record ScriptResync : AdvEvent;
