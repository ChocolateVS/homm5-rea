using System.Text.Json;
using System.Text.Json.Serialization;
using H5.Core.Data;

namespace H5.Combat;

// Battle state is plain serialisable data (public fields, no references between
// objects other than integer ids). Everything the rules need at runtime that is not
// static game data lives here, so a state can be cloned, hashed, saved and sent
// over the network. Static data (creature/spell definitions) stays in GameDatabase.

public sealed class KnownSpell
{
    public string Spell = "";
    public Mastery Mastery;
}

/// <summary>A timed or counted modifier on a unit (spell, ability effect, buff/debuff).</summary>
public sealed class Effect
{
    public string Id = "";
    /// <summary>Remaining duration in battle time units (one unit = one turn of an Initiative-10 creature). Infinity = until removed.</summary>
    public double Remaining = double.PositiveInfinity;
    /// <summary>Duration the effect was (last) applied with, for the "Turns: x/y" display (exe tooltip maxval).</summary>
    public double Duration;
    /// <summary>Remaining own actions, or -1 when not action-limited.</summary>
    public int Actions = -1;
    public double Value;
    public double Value2;
    public int SourceSide = -1;
    public int SourceUnit = -1;
    public int CasterLevel;
    public double Power;
    public Mastery Mastery;
    public bool Positive;
    public bool Dispellable = true;
    /// <summary>Magic school/level of the spell that created it (0 for non-spell effects), used by Cleansing and immunity.</summary>
    public int SpellLevel;
    public string School = "";
}

public sealed class Unit
{
    public int Id;
    public int Side;
    public UnitKind Kind;
    public string CreatureId = "";
    public string Name = "";
    public string Town = "";
    public int Tier;
    public int Level;
    public bool Upgraded;

    // Base stats (creature data + permanent hero bonuses like Vitality), before temporary effects.
    public int BaseAttack, BaseDefence, BaseMinDamage, BaseMaxDamage, BaseHealth, BaseSpeed;
    public double BaseInitiative;
    public int MaxShots;
    public int Shots;
    public bool Flying;
    public int Size = 1;
    public int Power;
    public int WeeklyGrowth;
    public List<string> Abilities = new();
    public List<KnownSpell> Spells = new();
    public int Mana, MaxMana;
    public string Element1 = Elements.None, Element2 = Elements.None;

    // Dynamic state
    public Cell Pos;
    public Cell StartPos;
    public int Count;
    public int InitialCount;
    /// <summary>Hit points of the top creature of the stack.</summary>
    public int TopHealth;
    public double Atb;
    public int RetaliationsLeft = 1;
    public bool Defending;
    public bool Summoned;
    public bool IsClone;
    public int CellsWalked;
    public bool HasMovedFromStart;
    public int Frags;
    public double FrenzyMultiplier = 1;
    public int RagePoints;
    public int RageLevel;
    public List<Effect> Effects = new();
    /// <summary>Crafted effects of the creature artifact the stack carries (Artificer), or -1 for none.</summary>
    public int ArtifactEffects = -1;
    /// <summary>Uses of limited abilities ("once per combat" etc.), keyed by ability id.</summary>
    public Dictionary<string, int> AbilityUses = new();
    public bool HasActed;
    public bool Removed;   // fled, unsummoned, or a destroyed obstacle object

    [JsonIgnore] public bool Alive => Count > 0 && !Removed;
    [JsonIgnore] public bool IsWarMachine => Kind == UnitKind.WarMachine;
    [JsonIgnore] public bool IsObject => Kind == UnitKind.Obstacle;
    [JsonIgnore] public bool IsLarge => Size >= 2;
    [JsonIgnore] public int TotalHealth => Count <= 0 ? 0 : (Count - 1) * MaxHealthCache + TopHealth;
    /// <summary>Per-creature max HP including effect bonuses; refreshed by the engine when stats change.</summary>
    public int MaxHealthCache;

    /// <summary>
    /// Ability test with the original's dynamic rules (creature HasAbility override, exe FUN_008a34c0):
    /// Phantom images and Rune of Etherealness grant Incorporeal; Phantom images lose Flesh and Blood;
    /// Vampirism makes the target Undead; Rage of the Forest suppresses Entangling Roots; a rooted
    /// Take Roots creature holds its ground. Hero-specialisation grants are added at setup.
    /// </summary>
    public bool Has(string ability) => ability switch
    {
        "ABILITY_INCORPOREAL" => IsClone || HasEffect("FX_RUNE_ETHEREALNESS") || Abilities.Contains(ability),
        "ABILITY_FLESH_AND_BLOOD" => !IsClone && Abilities.Contains(ability),
        "ABILITY_UNDEAD" => HasEffect("SPELL_VAMPIRISM") || Abilities.Contains(ability),
        "ABILITY_ENTANGLING_ROOTS" => !HasEffect("FX_RAGE_OF_FOREST") && Abilities.Contains(ability),
        "ABILITY_HOLD_GROUND" => Abilities.Contains(ability) || Abilities.Contains("ABILITY_TAKE_ROOTS") && HasEffect("FX_TAKE_ROOTS"),
        _ => Abilities.Contains(ability),
    };
    public Effect? GetEffect(string id) => Effects.Find(e => e.Id == id);
    public bool HasEffect(string id) => Effects.Exists(e => e.Id == id);

    public IEnumerable<Cell> Cells()
    {
        for (int dx = 0; dx < Size; dx++)
        for (int dy = 0; dy < Size; dy++)
            yield return new Cell(Pos.X + dx, Pos.Y + dy);
    }

    public IEnumerable<Cell> CellsAt(Cell p)
    {
        for (int dx = 0; dx < Size; dx++)
        for (int dy = 0; dy < Size; dy++)
            yield return new Cell(p.X + dx, p.Y + dy);
    }

    public bool Occupies(Cell c) => c.X >= Pos.X && c.X < Pos.X + Size && c.Y >= Pos.Y && c.Y < Pos.Y + Size;

    /// <summary>Footprint centre, for distances and directions.</summary>
    public (double X, double Y) Center => (Pos.X + (Size - 1) / 2.0, Pos.Y + (Size - 1) / 2.0);

    public override string ToString() => $"#{Id} {Count}x{Name} s{Side}@{Pos}";
}

public sealed class HeroState
{
    public bool Present;
    public int Side;
    public string HeroId = "";
    public string Name = "";
    public string Class = "";
    public string Town = "";
    public string Specialization = "";
    public int Level = 1;
    public int Attack, Defence, SpellPower, Knowledge;
    public int Mana, MaxMana;
    public int Morale, Luck;
    public double Atb;
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new();
    public List<string> Spells = new();
    public List<string> Artifacts = new();
    public List<string> FavoriteEnemies = new();
    /// <summary>Equipped pieces per artifact set effect id.</summary>
    public Dictionary<string, int> SetPieces = new();
    /// <summary>Altars of Primal Elements owned (Elemental Chains, ElementalChainBonusPerAltarOfChaos per altar); set by the adventure hand-off.</summary>
    public int ElementalAltars;
    /// <summary>Creatures boosted by the hero's "+1 Attack/Defense per two levels" specialisation.</summary>
    public List<string> SpecFamily = new();
    public Dictionary<string, int> AbilityUses = new();
    /// <summary>Unit targeted by Retaliation Strike / Mark of the Damned / Mark of the Necromancer etc. (per ability id).</summary>
    public Dictionary<string, int> Links = new();
    public string? ImbuedSpell;
    public bool Fled;

    public Mastery SkillLevel(string skillId) => Skills.Find(s => s.SkillId == skillId)?.Mastery ?? Mastery.None;
    public bool HasPerk(string perkId) => Perks.Contains(perkId);
}

/// <summary>A non-unit battlefield feature: firewall, fire trap mine, thundercloud, corpse marker.</summary>
public sealed class FieldObject
{
    public int Id;
    public string Kind = "";      // FIREWALL, MINE, STORM, LIQUID_FLAME, SNARE
    public int Side;              // owner
    public List<Cell> Cells = new();
    public double Remaining = double.PositiveInfinity;
    public double Damage;
    public double Power;
    public Mastery Mastery;
    public int SourceUnit = -1;
    public bool HiddenFromEnemy;
}

public sealed class Corpse
{
    public int UnitId;
    public Cell Pos;
}

public sealed class BattleState
{
    public int Width = 12;
    public int Height = 10;
    public List<Cell> Obstacles = new();
    /// <summary>Combat arena scene name (Scenes/CombatArenas/&lt;Arena&gt;.xdb) the battle takes place on; null = none chosen.</summary>
    public string? Arena;
    /// <summary>Terrain type of the battlefield (CombatArenaTable.Terrain*: 0 dirt, 1 grass, 2 sand, 3 snow, 4 lava,
    /// 5 subterranean, 6 water, 7 dwarven, 8–9 orcish); drives terrain-dependent elementals.</summary>
    public int Terrain = 1;
    /// <summary>Obstacle models and footprints produced by the original placement algorithm (for rendering).</summary>
    public List<ObstaclePlacement> ObstaclePlacements = new();
    public List<Unit> Units = new();
    /// <summary>Town fortifications, moat and towers when the battle is a siege; null for field battles.</summary>
    public SiegeState? Siege;
    public HeroState[] Heroes = { new() { Side = 0 }, new() { Side = 1 } };
    /// <summary>
    /// Bonuses for all creatures of a side that do not depend on a hero being present: town buildings and
    /// town specialisations for the troops guarding (or assaulting) a town (fan manual p.244, DefaultStats
    /// TownBuildingBonuses). Filled by the adventure layer; zero in duels.
    /// </summary>
    public SideBonus[] SideBonuses = { new(), new() };
    public List<FieldObject> Fields = new();
    public Rng Rng = new(1);
    public BattlePhase Phase = BattlePhase.Tactics;
    public bool[] TacticsDone = { false, false };
    /// <summary>Retreat (flee and surrender) forbidden for a side in this battle (exe CCombatArmy reason 7), e.g. by a map script.</summary>
    public bool[] RetreatDenied = { false, false };
    public int DeployColumns0 = 2, DeployColumns1 = 2;
    public double Time;
    /// <summary>Current actor (unit id, or Actor.Hero(side)); null while no one is acting.</summary>
    public int? Active;
    public int? Winner;
    public string EndReason = "";
    public int NextId;
    public int NextFieldId;
    public int TurnCounter;
    /// <summary>Global hit/miss streak against incorporeal stacks (fan manual p.176): +n hits, -n misses.</summary>
    public int IncorporealStreak;
    /// <summary>Which sides are controlled by the built-in AI (server sets this for vs-AI games).</summary>
    public bool[] AiControlled = { false, false };

    public Unit? Unit(int id) => Units.Find(u => u.Id == id);
    public IEnumerable<Unit> Living => Units.Where(u => u.Alive);
    public IEnumerable<Unit> LivingOf(int side) => Units.Where(u => u.Alive && u.Side == side);
    public Unit? At(Cell c) => Units.Find(u => u.Alive && !u.IsWarMachine && u.Occupies(c))
                               ?? Units.Find(u => u.Alive && u.IsWarMachine && u.Occupies(c));
    public bool InBounds(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;

    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static BattleState Deserialize(string json) => JsonSerializer.Deserialize<BattleState>(json, Json)!;
    public BattleState Clone() => Deserialize(Serialize());

    /// <summary>Stable 64-bit FNV-1a hash of the canonical serialisation, as hex, for desync detection.</summary>
    public string ComputeHash()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        ulong h = 14695981039346656037UL;
        foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
        return h.ToString("x16");
    }
}

/// <summary>Flat stat modifiers applied to every creature of one side (see <see cref="BattleState.SideBonuses"/>).</summary>
public sealed class SideBonus
{
    public int Attack, Defence, Morale, Luck;
}
