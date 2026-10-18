using System.Text.Json;
using H5.Adventure.Map;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    public sealed class SaveData
    {
        public string Lua { get; set; } = "";
        public string World { get; set; } = "";
    }

    static readonly JsonSerializerOptions WorldJson = new() { IncludeFields = true };

    /// <summary>Serialises everything script-side (Lua heap, threads, script world). Save it next to the AdvState.</summary>
    public string Save() => JsonSerializer.Serialize(new SaveData
    {
        Lua = LuaSnapshot.Capture(L).ToJson(),
        World = JsonSerializer.Serialize(World, WorldJson),
    });

    /// <summary>Deterministic fingerprint of the script state, for lockstep desync checks.</summary>
    public string ComputeHash()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Save());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8);
    }

    /// <summary>
    /// Restores a host created for the same map and a game whose AdvState was restored from the
    /// same save. Registers the API (without running any script), re-applies script-made map
    /// changes, then restores the Lua heap and threads.
    /// </summary>
    public void Load(string saved)
    {
        var data = JsonSerializer.Deserialize<SaveData>(saved)!;
        World = JsonSerializer.Deserialize<ScriptWorld>(data.World, WorldJson)!;
        BaseLibrary.Register(L, LoadScript, () => (uint)S.Rng.NextU64());
        RegisterAdventureApi();

        foreach (var co in World.CreatedObjects)
        {
            var o = new MapObject
            {
                Kind = co.Kind switch
                {
                    "monster" => ObjectKind.Monster, "artifact" => ObjectKind.Artifact, "treasure" => ObjectKind.Treasure,
                    "dwelling" => ObjectKind.Dwelling, _ => ObjectKind.Static,
                },
                Pos = new Tile(co.X, co.Y, co.Floor), Rot = co.Rot, Name = co.Name,
                SharedPath = co.SharedPath, Shared = _catalog.Get(co.SharedPath),
                Creature = co.Creature, ArtifactId = co.Artifact, Amount = co.Count, CustomAmount = co.Count > 0,
                Mood = co.Mood ?? "", Courage = co.Courage ?? "",
            };
            Game.ScriptAddObject(o, setup: false);
        }
        foreach (var (index, p) in World.ObjectPlacements)
            if (index < Map.Objects.Count) Game.RestorePlacement(index, new Tile(p.X, p.Y, p.Floor), p.Rot);
        foreach (var (index, mc) in World.MonsterMoods)
            if (index < Map.Objects.Count && mc.Split('|') is [var mood, var courage]) { Map.Objects[index].Mood = mood; Map.Objects[index].Courage = courage; }
        Game.RestoreRemovedBlocking();

        LuaSnapshot.FromJson(data.Lua).Restore(L);
    }
}
