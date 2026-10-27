using System.Text.Json;
using System.Text.Json.Nodes;
using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;
using H5.Rmg;

namespace H5.AdvNet;

// Room settings and per-player lobby options of a networked adventure game. They mirror the
// original multiplayer game-setup screen (UI/MPCreate: map, difficulty, time limit, quick combat,
// ghost mode, dynamic battles, animation speed, autosave) and the custom-game RMG panel.

/// <summary>Random map request: everyone generates the same map from the same options and seed.</summary>
public sealed class RmgSettings
{
    public string Template = "";
    /// <summary>Size index 0..6 (tiny .. impossible).</summary>
    public int Size = 1;
    public int Players = 2;
    public bool Underground;
    /// <summary>0 = none, 2 = water (original encoding).</summary>
    public int Water;
    /// <summary>0 weak .. 4 impossible.</summary>
    public int Monsters = 2;
    /// <summary>Non-zero; the host fills it when the room is created.</summary>
    public int Seed;
    /// <summary>The RMG screen's EP_GRAAL_ON_MAP check box.</summary>
    public bool Grail;

    public RmgOptions ToOptions() => new()
    {
        Template = Template, Size = Size, Players = Players, Underground = Underground,
        WaterAmount = Water, MonsterLevel = Monsters, Seed = Seed == 0 ? 1 : Seed, Grail = Grail,
    };
}

public sealed class AdvNetSettings
{
    /// <summary>Map catalogue id (e.g. "Maps/Multiplayer/A2M1" or "user:MyMap"); null when <see cref="Rmg"/> is set.</summary>
    public string? Map { get; set; }
    public RmgSettings? Rmg { get; set; }
    /// <summary>0 easy .. 3 heroic.</summary>
    public int Difficulty { get; set; } = 1;
    /// <summary>Turn time limit in minutes for human players (0 = no limit).</summary>
    public int TimeLimitMinutes { get; set; }
    /// <summary>"All combats will play out automatically with only the results being shown."</summary>
    public bool QuickCombat { get; set; }
    public bool GhostMode { get; set; }
    /// <summary>Lobby "Simultaneous Turns" (room key simultaneus_turns, 00712d40): humans move at once until their areas meet (docs/rules/SIMULTANEOUS.md).</summary>
    public bool SimultaneousTurns { get; set; }
    /// <summary>
    /// The host runs H5_Game_PEST.exe (stock 3.1 + the 16-byte PEST patch; the 5.5 target, <c>TargetExe.Pest</c>): simultaneous
    /// turns, once on, never switch to standard turns (docs/rules/SIMULTANEOUS.md §7). Not a lobby control.
    /// </summary>
    public bool Pest { get; set; }
    /// <summary>
    /// The host's MMH55 player options that differ from the install's scripts/H55-Settings.lua (H5Reborn options screen,
    /// MMH 5.5 target only): global name → number, appended to that file for the game's scripts. Null: the file as is.
    /// </summary>
    public Dictionary<string, string>? ScriptSettings { get; set; }
    /// <summary>Dynamic battles: 0 off, 1 slow, 2 normal, 3 fast (DefaultStats DynamicBattle_SpeedCoeffs 2/1.5/1).</summary>
    public int DynamicBattles { get; set; }
    public int AnimationSpeed { get; set; } = 1;
    public bool Autosave { get; set; } = true;
    /// <summary>Test/dev only: side 1's first hero starts next to side 0's (immediate human-versus-human battle).</summary>
    public bool DevStartAdjacent { get; set; }
    /// <summary>
    /// Room made from a network save: the host's id of the save registered in <see cref="NetSaveLoads"/>; the game
    /// resumes from it instead of starting the map (CMultiplayerHostLoadLogic).
    /// </summary>
    public string? Load { get; set; }
    /// <summary>The saved human slots (map player and saved name) players choose from in the wait screen.</summary>
    public List<NetSaveSlot>? LoadSlots { get; set; }

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AdvNetSettings From(JsonObject? o) =>
        o == null ? new AdvNetSettings() : o.Deserialize<AdvNetSettings>(Json) ?? new AdvNetSettings();

    public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;

    public string? Validate()
    {
        if (Map == null && Rmg == null) return "Choose a map.";
        if (Rmg != null && string.IsNullOrWhiteSpace(Rmg.Template)) return "Choose a random map template.";
        if (Difficulty is < 0 or > 3) return "Invalid difficulty.";
        if (TimeLimitMinutes is < 0 or > 240) return "Invalid time limit.";
        if (DynamicBattles is < 0 or > 3) return "Invalid dynamic battle speed.";
        return null;
    }
}

/// <summary>Per-player lobby choices: which map player (colour/slot) to take and, for random races, the race.</summary>
public sealed class AdvPlayerOptions
{
    public int MapPlayer { get; set; } = -1;
    public string? Race { get; set; }

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static AdvPlayerOptions From(JsonObject? o) =>
        o == null ? new AdvPlayerOptions() : o.Deserialize<AdvPlayerOptions>(Json) ?? new AdvPlayerOptions();
    public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;
}

/// <summary>A loaded map plus what is needed to run it (files for map scripts; null for random maps).</summary>
public sealed record LoadedMap(AdventureDb Db, AdvMap Map, MapFiles? Files, byte[]? UserMapBytes);

/// <summary>
/// Opens maps for networked games on both host and clients. Stock maps come from the paks,
/// user .h5m maps from the install's Maps folder (or from bytes the host sends when a client
/// lacks the file), random maps are generated from the shared RMG options and seed.
/// </summary>
public sealed class AdvMapSource
{
    readonly GameDatabase _core;
    readonly PakFileSystem _fs;
    readonly GameInstall? _install;
    readonly string _tempDir;
    AdventureDb? _db;
    RmgDatabase? _rmg;

    public AdvMapSource(GameDatabase core, PakFileSystem fs, GameInstall? install, string? tempDir = null)
    {
        _core = core;
        _fs = fs;
        _install = install;
        _tempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "H5Reborn", "netmaps");
    }

    public AdventureDb Db => _db ??= AdventureDb.Load(_core, _fs);
    public RmgDatabase Rmg => _rmg ??= RmgDatabase.Load(new XdbLoader(_fs));
    public PakFileSystem Fs => _fs;

    public IReadOnlyList<MapCatalog.MapRef> Catalog() => MapCatalog.All(_fs, _install);

    /// <summary>Loads the map a room's settings name. <paramref name="userMapBytes"/> is the host's copy of a user map.</summary>
    public LoadedMap Load(AdvNetSettings s, byte[]? userMapBytes = null)
    {
        var db = Db;
        if (s.Rmg != null)
        {
            var res = new RmgGenerator(Rmg, s.Rmg.ToOptions()).Generate();
            var map = res.ToAdvMap(new SharedCatalog(new XdbLoader(_fs)));
            return new LoadedMap(db, map, null, null);
        }
        var r = MapCatalog.Find(_fs, _install, s.Map!);
        if (r == null && userMapBytes != null && s.Map!.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(_tempDir);
            var name = string.Concat(s.Map["user:".Length..].Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            var path = Path.Combine(_tempDir, $"{name}-{MapHash.Bytes(userMapBytes)}.h5m");
            if (!File.Exists(path)) File.WriteAllBytes(path, userMapBytes);
            r = new MapCatalog.MapRef(s.Map, path, true);
        }
        if (r == null) throw new FileNotFoundException($"Map '{s.Map}' is not installed.");
        var files = r.Open(_fs);
        var loaded = new MapLoader(_core, _fs).Load(files);
        byte[]? bytes = r.IsUserMap ? File.ReadAllBytes(r.Source) : null;
        return new LoadedMap(db, loaded, files, bytes);
    }
}

/// <summary>Fingerprints of maps so peers can verify they loaded/generated the same map.</summary>
public static class MapHash
{
    public static string Of(AdvMap m)
    {
        ulong h = 14695981039346656037UL;
        void Add(string s) { foreach (var c in s) { h ^= c; h *= 1099511628211UL; } h ^= 0xff; h *= 1099511628211UL; }
        void AddI(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
        Add(m.Id); AddI(m.TilesX); AddI(m.TilesY);
        foreach (var o in m.Objects) { AddI(o.Index); AddI((int)o.Kind); AddI(o.Pos.X); AddI(o.Pos.Y); AddI(o.Pos.Floor); Add(o.SharedPath); }
        foreach (var f in m.Floors)
        {
            if (f == null) { AddI(-1); continue; }
            AddI(f.Width); AddI(f.Height);
            for (int i = 0; i < f.Blocked.Length; i++) if (f.Blocked[i]) AddI(i);
        }
        foreach (var p in m.Players) { AddI(p.Index); AddI(p.Active ? 1 : 0); AddI(p.Team); Add(p.Race); }
        return h.ToString("x16");
    }

    public static string Bytes(byte[] b)
    {
        ulong h = 14695981039346656037UL;
        foreach (var x in b) { h ^= x; h *= 1099511628211UL; }
        return h.ToString("x16");
    }
}
