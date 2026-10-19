using System.Text.Json;
using System.Text.Json.Serialization;
using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Commands are player intents; events describe what happened so clients can animate
// and update their views. Both use "$t" discriminators like H5.Combat and H5.Net.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(MoveHeroCmd), "move")]
[JsonDerivedType(typeof(EndTurnCmd), "end_turn")]
[JsonDerivedType(typeof(BuildCmd), "build")]
[JsonDerivedType(typeof(RecruitCmd), "recruit")]
[JsonDerivedType(typeof(HireHeroCmd), "hire")]
[JsonDerivedType(typeof(ChooseLevelUpCmd), "level_up")]
[JsonDerivedType(typeof(AnswerChoiceCmd), "answer")]
[JsonDerivedType(typeof(ResolveBattleCmd), "resolve_battle")]
[JsonDerivedType(typeof(DismissHeroCmd), "dismiss")]
[JsonDerivedType(typeof(BuyWarMachineCmd), "buy_war_machine")]
[JsonDerivedType(typeof(TransferStackCmd), "transfer")]
[JsonDerivedType(typeof(SwapStacksCmd), "swap")]
[JsonDerivedType(typeof(GarrisonHeroCmd), "garrison")]
[JsonDerivedType(typeof(MoveArtifactCmd), "move_artifact")]
[JsonDerivedType(typeof(CastAdventureSpellCmd), "cast_adventure_spell")]
[JsonDerivedType(typeof(BuildShipCmd), "build_ship")]
[JsonDerivedType(typeof(ExchangeResourcesCmd), "exchange")]
[JsonDerivedType(typeof(SellArtifactCmd), "sell_artifact")]
[JsonDerivedType(typeof(UpgradeCreatureCmd), "upgrade_creature")]
[JsonDerivedType(typeof(BuyFactoryWarMachineCmd), "buy_factory_war_machine")]
[JsonDerivedType(typeof(BuyCartographerMapCmd), "buy_cartographer_map")]
[JsonDerivedType(typeof(UseTreeOfKnowledgeCmd), "use_tree_of_knowledge")]
[JsonDerivedType(typeof(BuyMerchantArtifactCmd), "buy_merchant_artifact")]
[JsonDerivedType(typeof(TrainCreatureCmd), "train_creature")]
[JsonDerivedType(typeof(DigCmd), "dig")]
[JsonDerivedType(typeof(CompleteSeerQuestCmd), "complete_seer_quest")]
[JsonDerivedType(typeof(AcceptSeerQuestCmd), "accept_seer_quest")]
[JsonDerivedType(typeof(AnswerSphinxCmd), "answer_sphinx")]
public abstract record AdvCommand
{
    /// <summary>Issuing player; the session layer fills it from the connection.</summary>
    public int Player { get; init; }
}

/// <summary>Move a hero towards a tile along the cheapest path, as far as movement points allow.</summary>
public sealed record MoveHeroCmd(int HeroId, Tile To) : AdvCommand;
public sealed record EndTurnCmd : AdvCommand;
public sealed record BuildCmd(int TownObject, string Building) : AdvCommand;
/// <summary>Recruit from a town dwelling tier (1..7); Upgraded picks the upgraded creature.</summary>
public sealed record RecruitCmd(int TownObject, int Tier, int Count, int Variant = 0) : AdvCommand;
public sealed record HireHeroCmd(int TownObject, int TavernSlot) : AdvCommand;
/// <summary>Pick one option of the pending level-up: index into Skills then Perks.</summary>
public sealed record ChooseLevelUpCmd(int HeroId, int Option) : AdvCommand;
public sealed record AnswerChoiceCmd(bool Accept, int Option = 0) : AdvCommand;
/// <summary>Reports the outcome of the pending battle (run by H5.Combat). Survivors are full armies after combat.</summary>
public sealed record ResolveBattleCmd(int WinnerSide, List<ArmySlot> AttackerSurvivors, List<ArmySlot> DefenderSurvivors, bool AttackerFled = false) : AdvCommand
{
    /// <summary>Spell points the two heroes have left at the end of the battle (attacker, defender); null = unchanged.</summary>
    public int[]? HeroMana { get; init; }
}
public sealed record DismissHeroCmd(int HeroId) : AdvCommand;
public sealed record BuyWarMachineCmd(int TownObject, int HeroId, string Machine) : AdvCommand;
/// <summary>Move/split creatures between two armies at the same place (hero ↔ hero, hero ↔ town garrison).</summary>
public sealed record TransferStackCmd(int FromHero, int FromTown, int FromSlot, int ToHero, int ToTown, int ToSlot, int Count) : AdvCommand;
/// <summary>Exchanges two whole slots (possibly empty, possibly different creatures) of armies at the same place.</summary>
public sealed record SwapStacksCmd(int FromHero, int FromTown, int FromSlot, int ToHero, int ToTown, int ToSlot) : AdvCommand;
/// <summary>Moves a hero into a town's garrison row (Garrison = true) or out to the visiting row.</summary>
public sealed record GarrisonHeroCmd(int TownObject, int HeroId, bool Garrison) : AdvCommand;
/// <summary>
/// Moves an artifact a hero carries: between worn and backpack (ToHero = same hero) or to another hero
/// standing next to it (hero meeting). Worn artifacts must fit their slot (ring slot holds two).
/// </summary>
public sealed record MoveArtifactCmd(int HeroId, string Artifact, bool FromBackpack, int ToHero, bool ToBackpack) : AdvCommand;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(TurnStarted), "turn")]
[JsonDerivedType(typeof(DayStarted), "day")]
[JsonDerivedType(typeof(WeekStarted), "week")]
[JsonDerivedType(typeof(HeroMoved), "moved")]
[JsonDerivedType(typeof(ObjectVisited), "visited")]
[JsonDerivedType(typeof(ObjectRemoved), "removed")]
[JsonDerivedType(typeof(ObjectCaptured), "captured")]
[JsonDerivedType(typeof(ResourcesChanged), "resources")]
[JsonDerivedType(typeof(HeroStatChanged), "hero_stat")]
[JsonDerivedType(typeof(ArtifactGained), "artifact")]
[JsonDerivedType(typeof(ArtifactSold), "artifact_sold")]
[JsonDerivedType(typeof(CreatureUpgraded), "creature_upgraded")]
[JsonDerivedType(typeof(SpellLearned), "spell")]
[JsonDerivedType(typeof(ExperienceGained), "experience")]
[JsonDerivedType(typeof(LevelUpOffered), "level_up")]
[JsonDerivedType(typeof(SkillLearned), "skill")]
[JsonDerivedType(typeof(BattleRequested), "battle")]
[JsonDerivedType(typeof(BattleResolved), "battle_result")]
[JsonDerivedType(typeof(ChoiceOffered), "choice")]
[JsonDerivedType(typeof(BuildingBuilt), "built")]
[JsonDerivedType(typeof(CreaturesRecruited), "recruited")]
[JsonDerivedType(typeof(HeroHired), "hired")]
[JsonDerivedType(typeof(HeroRemoved), "hero_removed")]
[JsonDerivedType(typeof(ArmyChanged), "army")]
[JsonDerivedType(typeof(TilesRevealed), "revealed")]
[JsonDerivedType(typeof(PlayerEliminated), "eliminated")]
[JsonDerivedType(typeof(GameEnded), "ended")]
[JsonDerivedType(typeof(Message), "message")]
[JsonDerivedType(typeof(AdventureSpellCast), "adventure_spell")]
[JsonDerivedType(typeof(BoatCreated), "boat_created")]
[JsonDerivedType(typeof(HeroEmbarked), "embarked")]
[JsonDerivedType(typeof(HeroDisembarked), "disembarked")]
[JsonDerivedType(typeof(BoatMoved), "boat_moved")]
[JsonDerivedType(typeof(SphinxRiddlePosed), "sphinx_riddle")]
public abstract record AdvEvent;

public sealed record TurnStarted(int Player, int Day) : AdvEvent;
public sealed record DayStarted(int Day) : AdvEvent;
public sealed record WeekStarted(int Week, string MoonWeek) : AdvEvent;
public sealed record HeroMoved(int HeroId, IReadOnlyList<Tile> Path, int MovePointsLeft) : AdvEvent;
public sealed record ObjectVisited(int HeroId, int Object, string What) : AdvEvent;
public sealed record ObjectRemoved(int Object) : AdvEvent;
public sealed record ObjectCaptured(int Object, int Player) : AdvEvent;
public sealed record ResourcesChanged(int Player, int[] Delta, int[] Total) : AdvEvent;
public sealed record HeroStatChanged(int HeroId, string Stat, int Delta) : AdvEvent;
public sealed record ArtifactGained(int HeroId, string Artifact) : AdvEvent;
public sealed record SpellLearned(int HeroId, string Spell) : AdvEvent;
public sealed record ExperienceGained(int HeroId, long Amount, long Total) : AdvEvent;
public sealed record LevelUpOffered(int HeroId, int Level, string Attribute, IReadOnlyList<SkillEntry> Skills, IReadOnlyList<string> Perks) : AdvEvent;
public sealed record SkillLearned(int HeroId, string Skill, Mastery Mastery) : AdvEvent;
public sealed record BattleRequested(int AttackerHero, int DefenderHero, int DefenderObject, Tile Where, IReadOnlyList<ArmySlot> DefenderArmy) : AdvEvent;
public sealed record BattleResolved(int WinnerSide, long ExperienceAwarded) : AdvEvent;
public sealed record ChoiceOffered(string Kind, int HeroId, int Object, int Gold, int Experience) : AdvEvent;
public sealed record BuildingBuilt(int TownObject, string Building, int Level) : AdvEvent;
public sealed record CreaturesRecruited(int TownObject, string Creature, int Count) : AdvEvent;
public sealed record HeroHired(int HeroId, string DefId, int TownObject) : AdvEvent;
public sealed record HeroRemoved(int HeroId, string Reason) : AdvEvent;
public sealed record ArmyChanged(int HeroId, int TownObject) : AdvEvent;
public sealed record TilesRevealed(int Player, int Floor, int Count) : AdvEvent;
public sealed record PlayerEliminated(int Player) : AdvEvent;
public sealed record GameEnded(int? WinnerTeam, string Reason) : AdvEvent;
/// <summary>Informational message keyed by a text id (original text refs where known).</summary>
public sealed record Message(string Key, string Text) : AdvEvent;

public sealed class AdvResult
{
    public bool Ok;
    public string? Error;
    public List<AdvEvent> Events = new();
    public static AdvResult Reject(string reason) => new() { Ok = false, Error = reason };
}

public static class AdvJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public static string Serialize(AdvCommand c) => JsonSerializer.Serialize(c, Options);
    public static AdvCommand DeserializeCommand(string json) => JsonSerializer.Deserialize<AdvCommand>(json, Options)!;
    public static string Serialize(IEnumerable<AdvEvent> e) => JsonSerializer.Serialize(e.ToList(), Options);
    public static List<AdvEvent> DeserializeEvents(string json) => JsonSerializer.Deserialize<List<AdvEvent>>(json, Options)!;
}
