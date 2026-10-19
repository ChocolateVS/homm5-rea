using System.Text.Json;
using System.Text.Json.Serialization;
using H5.Adventure.Map;
using H5.Combat;
using H5.Core.Data;

namespace H5.Adventure.State;

// Mutable, fully serialisable adventure game state. Static map data (terrain,
// object definitions) is NOT copied here: objects are referenced by their map
// index so a state + the map file reproduce the game exactly.

/// <summary>Resource vector in the original order: Wood, Ore, Mercury, Crystal, Sulfur, Gem, Gold.</summary>
public sealed class Purse
{
    public const int Wood = 0, Ore = 1, Mercury = 2, Crystal = 3, Sulfur = 4, Gem = 5, Gold = 6;
    public static readonly string[] Names = { "Wood", "Ore", "Mercury", "Crystal", "Sulfur", "Gem", "Gold" };

    public int[] R = new int[7];

    public Purse() { }
    public Purse(Resources r) => R = new[] { r.Wood, r.Ore, r.Mercury, r.Crystal, r.Sulfur, r.Gem, r.Gold };

    public int this[int i] { get => R[i]; set => R[i] = value; }
    public bool CanAfford(Resources c) => R[0] >= c.Wood && R[1] >= c.Ore && R[2] >= c.Mercury && R[3] >= c.Crystal && R[4] >= c.Sulfur && R[5] >= c.Gem && R[6] >= c.Gold;
    public void Add(Resources c, int mul = 1)
    {
        R[0] += c.Wood * mul; R[1] += c.Ore * mul; R[2] += c.Mercury * mul; R[3] += c.Crystal * mul;
        R[4] += c.Sulfur * mul; R[5] += c.Gem * mul; R[6] += c.Gold * mul;
    }
    public static Resources Scale(Resources c, int mul) =>
        new(c.Wood * mul, c.Ore * mul, c.Mercury * mul, c.Crystal * mul, c.Sulfur * mul, c.Gem * mul, c.Gold * mul);
    public override string ToString() => string.Join(" ", R.Select((v, i) => $"{Names[i]}={v}"));
}

/// <summary>A creature stack in an army or garrison (7 slots, nulls allowed).</summary>
public sealed class Stack
{
    public string Creature = "";
    public int Count;
    public Stack() { }
    public Stack(string creature, int count) { Creature = creature; Count = count; }
    public override string ToString() => $"{Count} {Creature}";
}

public static class Army
{
    public const int Slots = 7;
    public static Stack?[] Empty() => new Stack?[Slots];
    public static IEnumerable<Stack> Live(this Stack?[] a) => a.Where(s => s is { Count: > 0 })!;
    public static int StackCount(this Stack?[] a) => a.Count(s => s is { Count: > 0 });

    /// <summary>Adds creatures, merging into an existing stack of the same type. Returns false if no slot is free.</summary>
    public static bool TryAdd(this Stack?[] a, string creature, int count)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] is { } s && s.Creature == creature && s.Count > 0) { s.Count += count; return true; }
        for (int i = 0; i < a.Length; i++)
            if (a[i] is not { Count: > 0 }) { a[i] = new Stack(creature, count); return true; }
        return false;
    }

    public static List<ArmySlot> ToSlots(this Stack?[] a) => a.Live().Select(s => new ArmySlot(s.Creature, s.Count)).ToList();
}

public sealed class PlayerState
{
    public int Index;
    public int Team;
    public string Race = "";
    public string Colour = "";
    public bool Human;
    public bool Alive = true;
    public Purse Purse = new();
    /// <summary>Explored tiles per floor (bit per tile, row-major).</summary>
    public List<bool[]> Explored = new();
    /// <summary>Hero definition ids offered in this player's taverns this week (2 slots).</summary>
    public string?[] Tavern = new string?[2];
    /// <summary>Days since the player lost their last town (-1 while they own a town).</summary>
    public int DaysWithoutTown = -1;
    /// <summary>Objects already visited by any hero of this player (e.g. Eye/Hut of Magi reveals, Keymaster tents).</summary>
    public HashSet<int> VisitedObjects = new();
    public HashSet<int> Keys = new();

    // ---- map-script AI directives (CPlayerAIInformation; set by the AI natives, see AdvApi.Ai.cs) ----
    /// <summary>SetAIPlayerAttractor: object priorities for all this AI player's heroes (vf 0x1e4/0x1e8).</summary>
    public List<AiAttractor> AiAttractors = new();
    /// <summary>DenyAIHeroesFlee: enemy hero names ("" = any enemy) this player's AI heroes never flee from.</summary>
    public List<string> AiNoFleeFrom = new();
    /// <summary>EnableAIHeroHiring(…, false): towns (map object index) whose tavern the AI does not hire from.</summary>
    public List<int> AiHiringDisabledTowns = new();
    /// <summary>AllowHeroHiringByRaceForAI / AllowHiringOfHeroForAI (AI info +0x13c).</summary>
    public HeroFilter AiHireFilter = new();
    /// <summary>AllowPlayerTavernRace / AllowPlayerTavernHero: heroes generated in this player's taverns (player +0x644).</summary>
    public HeroFilter TavernFilter = new();
    /// <summary>SetPlayerHeroesCountNotForHire (player +0x610): hiring allowed while heroes &lt; max − this.</summary>
    public int HeroesNotForHire;
    /// <summary>DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes: without towns the AI only gets a turn while one of its heroes is AI-enabled.</summary>
    public bool AiNoTurnWithoutTownsAndActiveHeroes;
}

/// <summary>An AI attractor entry {object, priority -1..2} (exe list of {objId, priority}, FUN_00b4e880).</summary>
public sealed class AiAttractor
{
    /// <summary>Script name of the object (map object or hero).</summary>
    public string Object = "";
    public int Priority;

    /// <summary>FUN_00b4e880: an existing entry is overwritten (priority ≠ 0) or removed (0); a new one is appended.</summary>
    public static void Set(List<AiAttractor> list, string obj, int priority)
    {
        int i = list.FindIndex(a => a.Object == obj);
        if (i >= 0)
        {
            if (priority != 0) list[i].Priority = priority;
            else list.RemoveAt(i);
            return;
        }
        list.Add(new AiAttractor { Object = obj, Priority = priority });
    }

    /// <summary>FUN_00b4d5c0: the entry's priority, 0 when there is none.</summary>
    public static int Get(List<AiAttractor> list, string obj) => list.Find(a => a.Object == obj)?.Priority ?? 0;
}

/// <summary>
/// CHeroesFilter: hero races (TOWN_*) denied, hero script names explicitly allowed (override the race
/// rule) and explicitly denied. Check FUN_00c069d0; mutators FUN_00c06b20/c06a60 (races) and
/// FUN_00c06be0/c06b50 (names). Lists keep the exe's order and duplicates.
/// </summary>
public sealed class HeroFilter
{
    public static readonly string[] Races =
        { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };

    public List<string> DeniedRaces = new();
    public List<string> AllowedHeroes = new();
    public List<string> DeniedHeroes = new();

    /// <summary>FUN_00c069d0: a denied name is rejected; a denied race is rejected unless the name is allowed.</summary>
    public bool Accepts(string hero, string race) =>
        !DeniedHeroes.Contains(hero) && (!DeniedRaces.Contains(race) || AllowedHeroes.Contains(hero));

    /// <summary>FUN_00c06b20: deny adds the race when absent, allow removes it.</summary>
    public void SetRace(string race, bool allow)
    {
        if (allow) DeniedRaces.Remove(race);
        else if (!DeniedRaces.Contains(race)) DeniedRaces.Add(race);
    }

    /// <summary>FUN_00c06a60: allow clears the race list, deny appends every race.</summary>
    public void SetAllRaces(bool allow)
    {
        if (allow) DeniedRaces.Clear();
        else DeniedRaces.AddRange(Races);
    }

    /// <summary>FUN_00c06be0: deny → into the denied list, out of the allowed one; allow → the reverse.</summary>
    public void SetHero(string hero, bool allow)
    {
        var (to, from) = allow ? (AllowedHeroes, DeniedHeroes) : (DeniedHeroes, AllowedHeroes);
        from.Remove(hero);
        if (!to.Contains(hero)) to.Add(hero);
    }

    /// <summary>FUN_00c06b50: the hero follows the race rule again.</summary>
    public void ClearHero(string hero)
    {
        DeniedHeroes.Remove(hero);
        AllowedHeroes.Remove(hero);
    }
}

public sealed class HeroState
{
    public int Id;
    public string DefId = "";
    public string Name = "";
    public int Owner = -1;
    public Tile Pos;
    public string Class = "";
    public string Town = "";
    public string Specialization = "";
    public int Level = 1;
    public long Experience;
    public int Attack, Defence, SpellPower, Knowledge;
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new();
    public List<string> Spells = new();
    public List<string> Artifacts = new();
    /// <summary>Carried but not worn (hero screen backpack); worn ones are <see cref="Artifacts"/>.</summary>
    public List<string> Backpack = new();
    public Stack?[] Army = State.Army.Empty();
    public bool Ballista, FirstAidTent, AmmoCart;
    public int Mana, MovePoints, MaxMovePoints;
    public bool InBoat;
    /// <summary>The boat (<see cref="AdvState.Boats"/>) the hero is sailing, -1 on land.</summary>
    public int BoatId = -1;
    /// <summary>Town (map object index) the hero is currently visiting or garrisoned in, -1 if none.</summary>
    public int InTown = -1;
    public bool Garrisoned;
    /// <summary>Morale/luck bonuses that last until the next battle (fan manual p.261-264).</summary>
    public int NextBattleMorale, NextBattleLuck;
    /// <summary>Permanent morale/luck changes made by map scripts (ChangeHeroStat STAT_MORALE/STAT_LUCK).</summary>
    public int ScriptMorale, ScriptLuck;
    /// <summary>Objects that may be visited once per hero (Mercenary Camp, Dolmen…).</summary>
    public HashSet<int> VisitedOnce = new();
    /// <summary>Objects visited this week / today (Stables, Eldritch Well…), cleared by the turn cycle.</summary>
    public HashSet<int> VisitedWeek = new(), VisitedDay = new();
    /// <summary>Movement bonus objects visited since the last battle (Oasis, Fountain of Youth, Tattered Flag).</summary>
    public HashSet<int> VisitedSinceBattle = new();
    /// <summary>Stables bonus active for the rest of the week.</summary>
    public bool StablesThisWeek;
    /// <summary>Pending level-up choices; resolved one at a time with ChooseLevelUpCmd.</summary>
    public List<LevelUpOffer> PendingLevelUps = new();
    public bool Alive = true;
    /// <summary>EnableHeroAI (CHero AI settings +0): an AI player's hero the AI moves; false leaves it to scripts.</summary>
    public bool AiEnabled = true;
    /// <summary>SetAIHeroAttractor (settings +0x14): per-hero object priorities, checked before the player's.</summary>
    public List<AiAttractor> AiAttractors = new();
    /// <summary>DenyAIHeroFlee (settings +0x20): enemy hero names ("" = any enemy) this AI hero never flees from.</summary>
    public List<string> AiNoFleeFrom = new();
    /// <summary>SetAIHeroFleeControl (settings +0x2c): ignore the player's DenyAIHeroesFlee list.</summary>
    public bool AiFleeControl;


    public Mastery MasteryOf(string skill) => Skills.FirstOrDefault(s => s.SkillId == skill)?.Mastery ?? Mastery.None;
    public bool HasPerk(string perk) => Perks.Contains(perk);
    public bool HasArtifact(string art) => Artifacts.Contains(art);
    public override string ToString() => $"{Name}#{Id} p{Owner + 1} {Pos} L{Level}";
}

/// <summary>The choices offered on a level up (fan manual p.294): up to 2 skills and 2 perks.</summary>
public sealed class LevelUpOffer
{
    public int NewLevel;
    public string Attribute = "";        // the primary stat gained (applied immediately)
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new();
}

public sealed class TownState
{
    /// <summary>Map object index of the town.</summary>
    public int Object;
    public string Name = "";
    public string Type = "";
    public int Owner = -1;
    public Tile Pos;
    /// <summary>Built level per building type (TB_*), 0 = not built.</summary>
    public Dictionary<string, int> Built = new();
    /// <summary>Maximum level allowed per building type by the map (BLD_UPG_n).</summary>
    public Dictionary<string, int> MaxLevel = new();
    public bool BuiltToday;
    /// <summary>Recruitable creatures per dwelling tier (index 0 = tier 1). Base and upgrades share the pool.</summary>
    public int[] Available = new int[7];
    public Stack?[] Garrison = Army.Empty();
    /// <summary>Mage guild spells per circle (index 0 = level 1).</summary>
    public List<List<string>> MageGuild = new() { new(), new(), new(), new(), new() };
    public string? Specialization;
    /// <summary>Haven training points left this week (exe CAdvMapTown +0x2d0, see Adventure.TownSpecials).</summary>
    public int TrainingPoints;
    /// <summary>BlockTownGarrisonForAI: AI heroes do not exchange creatures with this garrison.</summary>
    public bool AiGarrisonBlocked;
    /// <summary>AllowHeroHiringByRaceInTown / AllowHiringOfHeroInTown (town +0x224), for every player.</summary>
    public HeroFilter HireFilter = new();

    public int Level(string type) => Built.TryGetValue(type, out var l) ? l : 0;
    /// <summary>Town level = number of buildings built, counting each upgrade (fan manual p.224 "Town Level").</summary>
    public int DevLevel => Built.Values.Sum();
}

/// <summary>Dynamic state of a visitable map object (by map index).</summary>
public sealed class ObjState
{
    public int Object;
    public int Owner = -1;
    public bool Removed;
    /// <summary>Remaining guards / neutral stacks (monsters, guarded mines, banks).</summary>
    public List<Stack> Guards = new();
    /// <summary>Pre-rolled contents (treasure amounts, resource kind, bank variant…).</summary>
    public int Amount;
    public int ResourceKind = -1;
    public int Variant = -1;
    /// <summary>Day number when the object becomes available again (banks, weekly buildings).</summary>
    public int RechargeDay;
    public int TimesLooted;
    /// <summary>Visited-by players (for "once per player" objects) and weekly flags.</summary>
    public HashSet<int> VisitedBy = new();
    public int LastVisitWeek;
    /// <summary>Creatures available in an external dwelling.</summary>
    public int[] Available = Array.Empty<int>();
    public string? ArtifactId;
    public string? SpellId;
    public string? Creature;
    /// <summary>Seer Hut quest completed (AllowMultipleCompletions is not modelled: once).</summary>
    public bool QuestDone;
    /// <summary>Players that took the Seer Hut quest (Accept on the first visit).</summary>
    public HashSet<int> QuestTakenBy = new();
    /// <summary>Sphinx answered (CAdvMapSphinx +0xd0): later visits get the refuse message.</summary>
    public bool SphinxUsed;
    /// <summary>The riddle a Sphinx posed on the current visit, waiting for the answer.</summary>
    public PendingRiddle? Riddle;
    /// <summary>Skill a Witch Hut teaches (rolled on first visit).</summary>
    public string? SkillId;
}

/// <summary>A battle the engine is waiting for (run by H5.Combat, reported back with ResolveBattleCmd).</summary>
public sealed class PendingBattle
{
    public int AttackerHero;
    /// <summary>Defender: a hero id, a town object, or a neutral/guarded object.</summary>
    public int DefenderHero = -1;
    public int DefenderObject = -1;
    public Tile Where;
    public List<Stack> DefenderArmy = new();
    public bool Siege;
}

/// <summary>A choice the engine is waiting for (neutral join offer, treasure chest gold/exp…).</summary>
/// <summary>A posed Sphinx riddle: answers in display order and the position of the right one.</summary>
public sealed class PendingRiddle
{
    public int Hero;
    public string Question = "";
    public string[] Answers = Array.Empty<string>();
    public int Correct;
    public int Experience, Gold;
    public string? Artifact;
}

public sealed class PendingChoice
{
    public string Kind = "";          // "join_free", "join_paid", "chest", "witch_hut"…
    public int Hero;
    public int Object = -1;
    public int Gold, Experience;
    public List<Stack> Creatures = new();
    public string? Skill;
}

public sealed class AdvState
{
    public string MapId = "";
    public int Day = 1;
    public int CurrentPlayer;
    public string MoonWeek = "WEEK_OF_NOTHING";
    public int Difficulty = 1;            // 0 easy .. 3 heroic
    public bool Campaign;
    public Rng Rng = new(1);
    public List<PlayerState> Players = new();
    public List<HeroState> Heroes = new();
    /// <summary>Heroes that exist but are not on the map: player reserves (DeployReserveHero), kept out of taverns.</summary>
    public List<HeroState> OffMapHeroes = new();
    public List<TownState> Towns = new();
    public Dictionary<int, ObjState> Objects = new();
    /// <summary>Artifact merchant stock (6 slots, null = sold) by town or map object index.</summary>
    public Dictionary<int, string?[]> ArtifactMarkets = new();
    /// <summary>Where the Tear of Asha is buried (null when the map places no Graal) and whether it was dug up.</summary>
    public Tile? Grail;
    public bool GrailFound;
    /// <summary>Tiles already dug (holes stay on the map; digging there again says GRAAL_HAS_HOLE).</summary>
    public HashSet<Tile> Holes = new();
    /// <summary>Hero definition ids not yet in play (tavern pool).</summary>
    public List<string> HeroPool = new();
    public PendingBattle? Battle;
    public PendingChoice? Choice;
    /// <summary>Random Sphinx riddles not drawn yet (indices into RandomGroup; CRiddlesPool).</summary>
    public List<int> RiddlePool = new();
    public int NextHeroId = 1;
    public bool Finished;
    public int? WinnerTeam;
    /// <summary>Ships on the map: pre-placed by the editor, built at shipyards, or summoned.</summary>
    public List<BoatState> Boats = new();
    public int NextBoatId = 1;

    [JsonIgnore] public int DayOfWeek => (Day - 1) % 7 + 1;
    [JsonIgnore] public int Week => (Day - 1) / 7 + 1;
    [JsonIgnore] public int WeekOfMonth => (Week - 1) % 4 + 1;
    [JsonIgnore] public int Month => (Day - 1) / 28 + 1;

    public HeroState? Hero(int id) => Heroes.Find(h => h.Id == id && h.Alive);
    public TownState? Town(int obj) => Towns.Find(t => t.Object == obj);
    public ObjState Obj(int index)
    {
        if (!Objects.TryGetValue(index, out var o)) Objects[index] = o = new ObjState { Object = index };
        return o;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static AdvState Deserialize(string json) => JsonSerializer.Deserialize<AdvState>(json, Json)!;
    public AdvState Clone() => Deserialize(Serialize());

    /// <summary>Stable FNV-1a hash of the canonical serialisation (desync detection), as BattleState does.</summary>
    public string ComputeHash()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        ulong h = 14695981039346656037UL;
        foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
        return h.ToString("x16");
    }
}
