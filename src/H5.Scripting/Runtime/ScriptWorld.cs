using System.Text.Json.Serialization;

namespace H5.Scripting.Runtime;

/// <summary>Objective state values as in the scripts (OBJECTIVE_* constants).</summary>
public static class ObjectiveStates
{
    public const int ScenarioInfo = 0, Unknown = 1, Active = 2, Completed = 3, Failed = 4;
}

public sealed class ObjectiveState
{
    public int State = ObjectiveStates.Unknown;
    public int Progress;
    public bool Visible = true;
}

/// <summary>A message box, question, dialog scene or cut-scene waiting for the player.</summary>
public sealed class UiRequest
{
    public int Id;
    /// <summary>message_box, question_box, dialog_scene, cut_scene, adv_map_dialog, talk_box, tutorial_message.</summary>
    public string Kind = "";
    /// <summary>Main resource: a text file path, scene xdb path, or dialog index.</summary>
    public string Resource = "";
    /// <summary>&lt;value&gt; substitutions for complex messages ({ "file.txt"; gold = 100 }).</summary>
    public Dictionary<string, string> Values = new();
    /// <summary>The message text as the native built it (FUN_006b5010: file loaded, &lt;value=&gt; substituted); "" for other kinds.</summary>
    public string Text = "";
    /// <summary>Lua function names (or expressions) invoked as name() on OK/yes and on no.</summary>
    public string Callback = "", CallbackNo = "";
    /// <summary>Player numbers (1-based) that see it; empty = the current/all human players.</summary>
    public List<int> Players = new();
    public string SaveName = "";
}

/// <summary>
/// Script-visible world state that the rules engine does not model: objectives, blocked
/// regions, triggers, disabled objects, game variables, AI hints, presentation settings.
/// Serialised with the Lua heap so saves and network lockstep reproduce scripted maps exactly.
/// </summary>
public sealed class ScriptWorld
{
    /// <summary>Objective states keyed by "name" (common, per player) as "name|player".</summary>
    public Dictionary<string, ObjectiveState> Objectives = new(StringComparer.Ordinal);
    /// <summary>Region name → players (1-based) for whom it is blocked (0 = every player, older saves).</summary>
    public Dictionary<string, HashSet<int>> BlockedRegions = new(StringComparer.Ordinal);
    /// <summary>Trigger key ("type|filter") → handler function name.</summary>
    public Dictionary<string, string> Triggers = new(StringComparer.Ordinal);
    /// <summary>Objects disabled by SetObjectEnabled (map index); the value is unused (see DisabledModes).</summary>
    public Dictionary<int, int> DisabledObjects = new();
    /// <summary>SetDisabledObjectMode: cursor mode per object index (DISABLED_DEFAULT..DISABLED_BLOCKED), independent of the enabled state.</summary>
    public Dictionary<int, int> DisabledModes = new();
    /// <summary>Global game variables (SetGameVar), shared across a campaign.</summary>
    public Dictionary<string, string> GameVars = new(StringComparer.Ordinal);
    /// <summary>Regions each hero is currently inside (hero id → region names), for enter/exit triggers.</summary>
    public Dictionary<int, List<string>> HeroRegions = new();
    public int GameBlocks;
    public List<UiRequest> PendingUi = new();
    public int NextUiId = 1;
    /// <summary>Miscellaneous per-entity settings the client or AI read (flashlights, tooltips, AI flags…).</summary>
    public Dictionary<string, string> Settings = new(StringComparer.Ordinal);
    public Dictionary<int, int> VisitedMarks = new();
    /// <summary>Saved combat results (GetSavedCombat*), newest last.</summary>
    public List<SavedCombat> SavedCombats = new();
    public double HeroesExpCoef = 1.0;
    /// <summary>Objects moved/rotated by scripts (map index → placement), re-applied when loading.</summary>
    public Dictionary<int, ObjectPlacement> ObjectPlacements = new();
    /// <summary>Objects created by scripts, in creation order (map indices continue after the map's own).</summary>
    public List<CreatedObject> CreatedObjects = new();
    /// <summary>A StartCombat waiting for its battle to be resolved (finish trigger, combat script).</summary>
    public PendingScriptCombat? PendingCombat;
    /// <summary>Monster mood/courage overrides (map index → "mood|courage").</summary>
    public Dictionary<int, string> MonsterMoods = new();

    [JsonIgnore] public bool GameBlocked => GameBlocks > 0;

    public static string ObjectiveKey(string name, int player) => player < 0 ? name : $"{name}|{player}";
}

public sealed class PendingScriptCombat
{
    public string Hero = "";
    public string FinishTrigger = "", CombatScript = "", Arena = "";
    /// <summary>StartCombat's allowQuickCombat (any non-nil value).</summary>
    public bool AllowQuickCombat;
}

public sealed class ObjectPlacement
{
    public int X, Y, Floor;
    public float Rot;
}

public sealed class CreatedObject
{
    public string Kind = "";
    public string Name = "";
    public string SharedPath = "";
    public int X, Y, Floor;
    public float Rot;
    public string? Creature, Artifact, Mood, Courage;
    public int Count;
}

/// <summary>
/// A saved combat (exe world vector +0x1b4, 16-byte records): the COMBAT_RESULT_* value and the armies,
/// [0] the winner and [1] the loser. The GetSavedCombatArmy* functions read the armies only when both
/// are recorded; an unknown index reads the world's empty default record (result 0, no armies).
/// </summary>
public sealed class SavedCombat
{
    public int Index;
    public int Result;
    public int Winner;               // engine side 0 attacker / 1 defender
    public List<SavedArmy> Armies = new();
}

public sealed class SavedArmy
{
    public int Player;               // script player id, 0 = PLAYER_NONE
    public string Hero = "";         // hero script name, "" = no hero
    public List<SavedStack> Stacks = new();
}

public sealed class SavedStack
{
    public int Creature;
    public int Count;
    public int Died;
}

/// <summary>A presentation-side effect of a script for the client (camera, sound, visual effects…).</summary>
public sealed record ScriptEvent(string Kind, IReadOnlyDictionary<string, string> Data)
{
    public override string ToString() => $"{Kind}({string.Join(", ", Data.Select(kv => $"{kv.Key}={kv.Value}"))})";
}
