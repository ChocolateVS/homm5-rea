using System.Text.Json;
using System.Text.Json.Serialization;

namespace H5.Combat;

// Commands are what a player (or the AI) asks for; events are what the authoritative
// engine reports back so clients can animate. Both serialise with a "$t" discriminator
// to match the network protocol in H5.Net.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(DeployCmd), "deploy")]
[JsonDerivedType(typeof(EndTacticsCmd), "end_tactics")]
[JsonDerivedType(typeof(ReserveCmd), "reserve")]
[JsonDerivedType(typeof(SplitStackCmd), "split_stack")]
[JsonDerivedType(typeof(AutoplaceCmd), "autoplace")]
[JsonDerivedType(typeof(MoveCmd), "move")]
[JsonDerivedType(typeof(AttackCmd), "attack")]
[JsonDerivedType(typeof(ShootCmd), "shoot")]
[JsonDerivedType(typeof(WaitCmd), "wait")]
[JsonDerivedType(typeof(DefendCmd), "defend")]
[JsonDerivedType(typeof(CastSpellCmd), "cast")]
[JsonDerivedType(typeof(UseAbilityCmd), "ability")]
[JsonDerivedType(typeof(HeroAttackCmd), "hero_attack")]
[JsonDerivedType(typeof(SurrenderCmd), "surrender")]
public abstract record BattleCommand
{
    /// <summary>Side issuing the command; the session layer fills it from the connection, never trusting the client.</summary>
    public int Side { get; init; }
}

/// <summary>Tactics phase: place a unit (top-left cell for large creatures) inside the deployment zone.</summary>
public sealed record DeployCmd(int UnitId, Cell Cell) : BattleCommand;
public sealed record EndTacticsCmd : BattleCommand;
/// <summary>Tactics: take a stack off the field back to the army bar (exe FUN_005779d0 drop on the bar, FUN_005784c0 double click).</summary>
public sealed record ReserveCmd(int UnitId) : BattleCommand;
/// <summary>
/// Tactics: move Count creatures from UnitId into IntoId (same creature), or into a new stack in the army bar when IntoId
/// is null (exe FUN_004dbd70). Moving a whole stack into another merges them.
/// </summary>
public sealed record SplitStackCmd(int UnitId, int Count, int? IntoId = null) : BattleCommand;
/// <summary>Tactics: auto place every stack of the side, army bar included (exe FUN_00578510).</summary>
public sealed record AutoplaceCmd : BattleCommand;
public sealed record MoveCmd(int UnitId, Cell To) : BattleCommand;
/// <summary>Melee attack; From is where the attacker stands to strike (null = attack from current position).</summary>
public sealed record AttackCmd(int UnitId, int TargetId, Cell? From = null) : BattleCommand;
public sealed record ShootCmd(int UnitId, int TargetId) : BattleCommand;
public sealed record WaitCmd(int ActorId) : BattleCommand;
public sealed record DefendCmd(int ActorId) : BattleCommand;
/// <summary>Spell cast by the active hero (ActorId = Actor.Hero(side)) or an active caster creature.</summary>
public sealed record CastSpellCmd(int ActorId, string Spell, int? TargetId = null, Cell? Cell = null) : BattleCommand;
public sealed record UseAbilityCmd(int ActorId, string Ability, int? TargetId = null, Cell? Cell = null) : BattleCommand;
public sealed record HeroAttackCmd(int TargetId) : BattleCommand;
/// <summary>
/// Leave the battle. Without <see cref="PayRansom"/> the hero flees (army lost, EndReason "fled");
/// with it the hero surrenders, paying the ransom to keep the troops (EndReason "surrender").
/// The ransom itself is settled by the adventure host.
/// </summary>
public sealed record SurrenderCmd : BattleCommand
{
    public bool PayRansom { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(PhaseChanged), "phase")]
[JsonDerivedType(typeof(Deployed), "deployed")]
[JsonDerivedType(typeof(Reserved), "reserved")]
[JsonDerivedType(typeof(StackChanged), "stack_changed")]
[JsonDerivedType(typeof(TurnStarted), "turn")]
[JsonDerivedType(typeof(MoraleProc), "morale")]
[JsonDerivedType(typeof(LuckProc), "luck")]
[JsonDerivedType(typeof(Moved), "moved")]
[JsonDerivedType(typeof(Teleported), "teleported")]
[JsonDerivedType(typeof(AttackStarted), "attack")]
[JsonDerivedType(typeof(Missed), "missed")]
[JsonDerivedType(typeof(Damaged), "damaged")]
[JsonDerivedType(typeof(Healed), "healed")]
[JsonDerivedType(typeof(UnitDied), "died")]
[JsonDerivedType(typeof(UnitSummoned), "summoned")]
[JsonDerivedType(typeof(SpellCast), "spell")]
[JsonDerivedType(typeof(SpellResisted), "resisted")]
[JsonDerivedType(typeof(EffectAdded), "effect_added")]
[JsonDerivedType(typeof(EffectRemoved), "effect_removed")]
[JsonDerivedType(typeof(AbilityTriggered), "ability")]
[JsonDerivedType(typeof(AtbChanged), "atb")]
[JsonDerivedType(typeof(ManaChanged), "mana")]
[JsonDerivedType(typeof(Waited), "waited")]
[JsonDerivedType(typeof(Defended), "defended")]
[JsonDerivedType(typeof(TurnSkipped), "skipped")]
[JsonDerivedType(typeof(FieldChanged), "field")]
[JsonDerivedType(typeof(BattleEnded), "ended")]
[JsonDerivedType(typeof(BuildingDamaged), "building_damaged")]
public abstract record BattleEvent;

public sealed record PhaseChanged(BattlePhase Phase) : BattleEvent;
public sealed record Deployed(int UnitId, Cell Cell) : BattleEvent;
/// <summary>Tactics: the stack went back to the army bar.</summary>
public sealed record Reserved(int UnitId) : BattleEvent;
/// <summary>Tactics: a stack's size changed by a split or merge (Count 0 = merged away); new stacks start in the army bar.</summary>
public sealed record StackChanged(int UnitId, string CreatureId, int Count, bool New) : BattleEvent;
public sealed record TurnStarted(int Actor, double Time) : BattleEvent;
public sealed record MoraleProc(int UnitId, bool Good) : BattleEvent;
public sealed record LuckProc(int UnitId, bool Good) : BattleEvent;
public sealed record Moved(int UnitId, IReadOnlyList<Cell> Path, bool Flying) : BattleEvent;
public sealed record Teleported(int UnitId, Cell From, Cell To) : BattleEvent;
/// <summary>An attack begins (for animation). Kind: melee, ranged, retaliation, hero...</summary>
public sealed record AttackStarted(int Attacker, int Target, AttackKind Kind, string? Ability = null) : BattleEvent;
public sealed record Missed(int Attacker, int Target, string Reason) : BattleEvent;
public sealed record Damaged(int Target, int Damage, int Killed, int CountLeft, int TopHealth, int Source, AttackKind Kind, string? By = null) : BattleEvent;
/// <summary>Hit points restored (By: the healing source, as passed to Battle.Heal).</summary>
public sealed record Healed(int Target, int Amount, int Resurrected, int CountNow, string? By = null) : BattleEvent;
public sealed record UnitDied(int UnitId) : BattleEvent;
public sealed record UnitSummoned(int UnitId, string CreatureId, int Count, int Side, Cell Cell, string By) : BattleEvent;
public sealed record SpellCast(int Caster, string Spell, int? Target, Cell? Cell, IReadOnlyList<int> Affected) : BattleEvent;
public sealed record SpellResisted(int Target, string Spell) : BattleEvent;
public sealed record EffectAdded(int UnitId, string Effect, double Duration, double Value) : BattleEvent;
public sealed record EffectRemoved(int UnitId, string Effect) : BattleEvent;
/// <summary>A siege building (SiegeState.Buildings id) lost hit points; Destroyed once it falls.</summary>
public sealed record BuildingDamaged(int Building, int Damage, int Health, bool Destroyed, string By) : BattleEvent;
public sealed record AbilityTriggered(int UnitId, string Ability, int? Target = null) : BattleEvent;
public sealed record AtbChanged(int Actor, double Atb) : BattleEvent;
public sealed record ManaChanged(int Actor, int Mana) : BattleEvent;
public sealed record Waited(int Actor) : BattleEvent;
public sealed record Defended(int Actor) : BattleEvent;
public sealed record TurnSkipped(int Actor, string Reason) : BattleEvent;
public sealed record FieldChanged(int FieldId, string Kind, IReadOnlyList<Cell> Cells, bool Removed) : BattleEvent;
public sealed record BattleEnded(int? Winner, string Reason) : BattleEvent;

public sealed class ApplyResult
{
    public bool Ok;
    public string? Error;
    public List<BattleEvent> Events = new();
    public static ApplyResult Reject(string reason) => new() { Ok = false, Error = reason };
}

public static class MessageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(BattleCommand c) => JsonSerializer.Serialize(c, Options);
    public static BattleCommand DeserializeCommand(string json) => JsonSerializer.Deserialize<BattleCommand>(json, Options)!;
    public static string Serialize(IEnumerable<BattleEvent> e) => JsonSerializer.Serialize(e.ToList(), Options);
    public static List<BattleEvent> DeserializeEvents(string json) => JsonSerializer.Deserialize<List<BattleEvent>>(json, Options)!;
}
