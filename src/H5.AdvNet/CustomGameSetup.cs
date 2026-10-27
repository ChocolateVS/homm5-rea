using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;

namespace H5.AdvNet;

/// <summary>
/// Map list rules of the stock custom-game list (H5_Game.exe 00705130 / 00705250 / 007745e0): which map
/// folders are listed, the sort column and its direction.
/// </summary>
public static class CustomMapList
{
    /// <summary>Folder bits of the list's mask (00705de0): 1 Maps/Multiplayer, 2 Maps/SingleMissions, 4 generated maps.</summary>
    public const int Multiplayer = 1, SingleMissions = 2, Generated = 4;

    /// <summary>
    /// 007745e0: SingleMissions always; "custom_game_show_mp_maps" adds the multiplayer maps (mask 0x30003),
    /// "custom_game_show_rmg_maps" the generated maps (| 4). Both vars default to 0 (00775a50).
    /// </summary>
    public static int Mask(bool showMp, bool showRmg) => (showMp ? 0x30003 : SingleMissions) | (showRmg ? Generated : 0);

    /// <summary>The folder bit of a catalogue id. User maps and stock maps outside both folders count as multiplayer maps.</summary>
    public static int FolderOf(string id, bool generated) =>
        generated ? Generated
        : id.Contains("SingleMissions", StringComparison.OrdinalIgnoreCase) ? SingleMissions
        : Multiplayer;

    public static bool Shown(int mask, int folder) => (mask & folder) != 0;

    /// <summary>
    /// The folder bit of a map by the path of its map.xdb (00705de0 lists "Maps/Multiplayer", "Maps/SingleMissions" and
    /// FUN_007c2cc0's "Maps/RMG"); 0 = not listed (e.g. campaign maps under Maps/Scenario).
    /// </summary>
    public static int FolderOfPath(string mapXdbPath)
    {
        var p = mapXdbPath.Replace((char)92, '/').TrimStart('/');
        if (p.StartsWith("Maps/Multiplayer/", StringComparison.OrdinalIgnoreCase)) return Multiplayer;
        if (p.StartsWith("Maps/SingleMissions/", StringComparison.OrdinalIgnoreCase)) return SingleMissions;
        if (p.StartsWith("Maps/RMG/", StringComparison.OrdinalIgnoreCase)) return Generated;
        return 0;
    }

    /// <summary>OBJECTIVE_KIND_* in enum order (the exe's string table); index N is UI/MPCreate/Maps/Goals "goalN".</summary>
    public static readonly string[] ObjectiveKinds =
    {
        "OBJECTIVE_KIND_SCENARIO_INFO", "OBJECTIVE_KIND_MANUAL", "OBJECTIVE_KIND_STAY_UNDEFEATED", "OBJECTIVE_KIND_DEFEAT_ALL",
        "OBJECTIVE_KIND_CAPTURE_ALL_TOWNS", "OBJECTIVE_KIND_BUILD_GRAAL", "OBJECTIVE_KIND_ASSEMBLE_ARMY", "OBJECTIVE_KIND_COLLECT_RESOURCES",
        "OBJECTIVE_KIND_HERO_MUST_SURVIVE", "OBJECTIVE_KIND_CAPTURE_ANY_TOWN_IN_WEEK", "OBJECTIVE_KIND_CAPTURE_OBJECT",
        "OBJECTIVE_KIND_DEFEAT_NEUTRALS", "OBJECTIVE_KIND_DEFEAT_HERO", "OBJECTIVE_KIND_BUILD_BUILDING_IN_TOWN",
        "OBJECTIVE_KIND_OBTAIN_ARTIFACT", "OBJECTIVE_KIND_OPEN_BORDER_GUARD", "OBJECTIVE_KIND_ADVANCE_HEROES",
        "OBJECTIVE_KIND_KILL_CREATURES", "OBJECTIVE_KIND_DEFEND_OBJECT", "OBJECTIVE_KIND_CAPTURE_ANY_TOWN",
        "OBJECTIVE_KIND_CAPTURE_9_TOWNS", "OBJECTIVE_KIND_STAY_WITH_HEROES", "OBJECTIVE_KIND_BUILD_TOWN_LEVEL",
    };

    /// <summary>
    /// The goal column's key (00706380): "custom" when the map has a CustomGoal text, else "goal%d" for an objective
    /// kind, else "goal_default" (00705800). Which objective the exe takes is not traced; the first primary one is used.
    /// </summary>
    public static string GoalKey(bool customGoal, string? firstPrimaryKind)
    {
        if (customGoal) return "custom";
        int i = firstPrimaryKind == null ? -1 : Array.IndexOf(ObjectiveKinds, firstPrimaryKind);
        return i >= 0 ? $"goal{i}" : "goal_default";
    }

    /// <summary>
    /// The players column (00707330): team sizes joined by "players_separator" ("v"), or "ffa_format" with the
    /// player count when every team has one player.
    /// </summary>
    public static string PlayersText(IReadOnlyList<int> teamSizes, string ffaFormat, string separator)
    {
        if (teamSizes.Count == 0) return "";
        if (teamSizes.All(t => t == 1)) return ffaFormat.Replace("<value=count>", teamSizes.Count.ToString());
        return string.Join(separator, teamSizes);
    }

    /// <summary>Team sizes of the active players, in first-seen team order.</summary>
    public static List<int> TeamSizes(IEnumerable<int> teams)
    {
        var order = new List<int>();
        var count = new Dictionary<int, int>();
        foreach (var t in teams)
        {
            if (!count.ContainsKey(t)) { order.Add(t); count[t] = 0; }
            count[t]++;
        }
        return order.Select(t => count[t]).ToList();
    }

    /// <summary>00704d90: fewer teams sort first, then fewer players in total.</summary>
    public static int ComparePlayers(IReadOnlyList<int> a, IReadOnlyList<int> b) =>
        a.Count != b.Count ? a.Count.CompareTo(b.Count) : a.Sum().CompareTo(b.Sum());

    /// <summary>
    /// 00705250: clicking the current sort column flips its direction (one flag per column), clicking another
    /// column selects it with that column's remembered direction. Columns: 0 name, 1 goal, 2 players, 3 size.
    /// </summary>
    public sealed class SortState
    {
        public int Column { get; private set; }
        readonly bool[] _descending = new bool[4];
        public bool Descending => _descending[Column];
        public bool IsDescending(int column) => _descending[column];

        public void Click(int column)
        {
            if (column < 0 || column > 3) return;
            if (column == Column) _descending[column] = !_descending[column];
            else Column = column;
        }
    }
}

/// <summary>The custom game's option bars (00774f80 / 007746c0 / 007747a0).</summary>
public static class CustomGameOptions
{
    /// <summary>TimeLimitBar: range 1..47 step 1, initial 46 (00774f80); 46 and above mean "unlimited" (T_UNLIMITED).</summary>
    public const int TimeLimitMin = 1, TimeLimitMax = 46, TimeLimitDefault = 46;

    /// <summary>Minutes for a bar position: positions above 45 are no limit (0) — 007746c0 `if (0x2d &lt; v) v = 0`.</summary>
    public static int Minutes(int barValue) => barValue > 45 ? 0 : Math.Max(TimeLimitMin, barValue);

    /// <summary>DifficultyBar: 4 positions (008478d0 returns 4), initial position 1 (00a33770).</summary>
    public const int DifficultyCount = 4, DifficultyDefault = 1;
}

/// <summary>A row of the player-setup list: state, colour slot, faction, starting hero and starting bonus.</summary>
public sealed class SetupSlot
{
    /// <summary>Model states (00c9dd70): 0 computer, 1 open, 2 human, 3 closed.</summary>
    public enum SlotState { Computer = 0, Open = 1, Human = 2, Closed = 3 }

    public int MapPlayer;
    public SlotState State;
    public string Name = "";
    /// <summary>Selected faction; null = random.</summary>
    public string? Race;
    /// <summary>Selected starting hero (HeroDef id); null = random.</summary>
    public string? Hero;
    /// <summary>0 random, 1 random artifact, 2 gold, 3 resource (UI/MPWait/PlayersList/Item/bonus0..3).</summary>
    public int Bonus;
    public List<string> Races = new();
    public List<string> Heroes = new();
    public bool CanBeHuman, CanBeComputer, CanClose, CanChangeBonus, ArtifactBonus, HeroSelectable;
    public int Team;
    public string Colour = "";
}

/// <summary>
/// The single-player player-setup model behind UI/CustomGameWait (CCustomGameWaitController 00778640 over the
/// game-setup model 00ca0240, vtable 00e92744). Row 0 is the human (the profile's name); the other rows are
/// computers that can be closed only where the map allows it (CanBeDisabled). On single-mission and generated
/// maps (map list types 2 and 4, 007746c0) every row but the first is locked.
/// </summary>
public sealed class CustomGameSetup
{
    public enum Change { State = 1, Colour = 2, Race = 3, Hero = 4, Bonus = 5 }

    /// <summary>Faction order of the race table (ETownType order, as the engine's playable races).</summary>
    public static readonly string[] AllRaces =
        { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };

    readonly AdvMap _map;
    readonly Dictionary<string, List<string>> _heroesByRace = new();
    public List<SetupSlot> Slots { get; } = new();
    /// <summary>The wait controller's flag (+0x28): rows other than the first are locked.</summary>
    public bool LockOthers { get; }
    readonly Dictionary<int, List<SetupSlot.SlotState>> _states = new();
    readonly Dictionary<int, int> _stateIndex = new();

    public CustomGameSetup(AdvMap map, AdventureDb db, string profileName, bool lockOthers)
    {
        _map = map;
        LockOthers = lockOthers;
        BuildHeroPool(db);
        foreach (var mp in map.Players.Where(p => p.Active))
        {
            bool hasHero = map.Objects.Any(o => o.Kind == ObjectKind.Hero && o.Owner == mp.Index) || mp.MainHeroUid != null || mp.HeroInTown;
            var s = new SetupSlot
            {
                MapPlayer = mp.Index, Team = mp.Team, Colour = ColourOf(mp), CanBeHuman = mp.CanBeHuman, CanBeComputer = mp.CanBeComputer,
                CanClose = mp.CanBeDisabled, CanChangeBonus = mp.CanChangeBonus, ArtifactBonus = hasHero,
                // 00ca0240: the start hero is chosen only for players whose hero is created in the main town.
                HeroSelectable = mp.HeroInTown && !map.Objects.Any(o => o.Kind == ObjectKind.Hero && o.Owner == mp.Index),
                Races = RacesFor(mp),
                // 00ca07c5: DefaultBonus ARTIFACT only when the player has a hero, GOLD, RESOURCE, else random.
                Bonus = mp.DefaultBonus switch
                {
                    "PLAYER_BONUS_ARTIFACT" => hasHero ? 1 : 0,
                    "PLAYER_BONUS_GOLD" => 2,
                    "PLAYER_BONUS_RESOURCE" => 3,
                    _ => 0,
                },
            };
            // 00c9e9f0: a player with exactly one possible faction has it fixed.
            if (s.Races.Count == 1) s.Race = s.Races[0];
            s.Heroes = HeroesFor(s);
            Slots.Add(s);
        }
        // 00778640: state lists {human, closed (if it may be disabled), computer}; row 0 human, the others computer.
        for (int i = 0; i < Slots.Count; i++)
        {
            var list = new List<SetupSlot.SlotState> { SetupSlot.SlotState.Human };
            if (Slots[i].CanClose) list.Add(SetupSlot.SlotState.Closed);
            list.Add(SetupSlot.SlotState.Computer);
            _states[i] = list;
            SetStateIndex(i, i == 0 ? 0 : list.IndexOf(SetupSlot.SlotState.Computer), profileName);
        }
    }

    /// <summary>Player colours by player number (UI/MPWait/PlayersList/Item TeamColors order).</summary>
    public static readonly string[] Colours =
        { "PCOLOR_RED", "PCOLOR_BLUE", "PCOLOR_GREEN", "PCOLOR_YELLOW", "PCOLOR_ORANGE", "PCOLOR_TEAL", "PCOLOR_PURPLE", "PCOLOR_BROWN" };

    /// <summary>00ca0240: the slot's colour is the player number unless the map gives the player a colour.</summary>
    static string ColourOf(MapPlayer mp) =>
        Colours.Contains(mp.Colour) ? mp.Colour : Colours[Math.Clamp(mp.Index, 0, Colours.Length - 1)];

    void BuildHeroPool(AdventureDb db)
    {
        // 00c9f4a0 (HEROES_ANY): heroes not placed on the map (objects, garrisons, reserve heroes).
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in _map.Objects)
        {
            if (o.Hero?.Shared?.Id is { Length: > 0 } h) used.Add(h);
            if (o.GarrisonHero?.Shared?.Id is { Length: > 0 } g) used.Add(g);
        }
        foreach (var p in _map.Players) foreach (var r in p.ReserveHeroes) used.Add(r);
        foreach (var d in db.Core.Heroes.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            if (used.Contains(d.Id) || d.Class.Length == 0 || !AllRaces.Contains(d.Town)) continue;
            if (d.Id.StartsWith("TEST", StringComparison.OrdinalIgnoreCase)) continue;
            if (_map.AvailableHeroes.Count > 0 ? !_map.AvailableHeroes.Contains(d.Id) : db.IsScenarioHero(d)) continue;
            if (!_heroesByRace.TryGetValue(d.Town, out var l)) _heroesByRace[d.Town] = l = new();
            l.Add(d.Id);
        }
    }

    /// <summary>
    /// 00c9e9f0: the factions a player may take — its towns' types (a random town allows all), else its heroes'
    /// factions (a random hero allows all), else its reserve heroes' factions, else every faction.
    /// </summary>
    List<string> RacesFor(MapPlayer mp)
    {
        var set = new HashSet<string>();
        var towns = _map.Objects.Where(o => o.Kind == ObjectKind.Town && o.Owner == mp.Index).ToList();
        if (towns.Count > 0)
            foreach (var t in towns)
                if (AllRaces.Contains(t.TownType ?? "")) set.Add(t.TownType!); else set.UnionWith(AllRaces);
        else
        {
            var heroes = _map.Objects.Where(o => o.Kind == ObjectKind.Hero && o.Owner == mp.Index).ToList();
            if (heroes.Count > 0)
                foreach (var h in heroes)
                    if (AllRaces.Contains(h.Hero?.Town ?? "")) set.Add(h.Hero!.Town); else set.UnionWith(AllRaces);
            else
                foreach (var r in mp.ReserveHeroInstances)
                    if (AllRaces.Contains(r.Town)) set.Add(r.Town);
        }
        if (set.Count == 0) set.UnionWith(AllRaces);
        return AllRaces.Where(set.Contains).ToList();
    }

    List<string> HeroesFor(SetupSlot s) =>
        s.Race != null && _heroesByRace.TryGetValue(s.Race, out var l) ? l : new List<string>();

    void SetStateIndex(int row, int index, string profileName)
    {
        _stateIndex[row] = index;
        var s = Slots[row];
        s.State = _states[row][index];
        // 00778000: human takes the profile name, closed an empty one, computer the row's colour name.
        s.Name = s.State == SetupSlot.SlotState.Human ? profileName : "";
    }

    /// <summary>007782f0: whether a row's left/right buttons of a kind are enabled.</summary>
    public bool CanChange(int row, Change kind)
    {
        if (row < 0 || row >= Slots.Count) return false;
        var s = Slots[row];
        bool locked = LockOthers && row != 0;
        return kind switch
        {
            Change.State => !LockOthers && row != 0 && s.CanClose,
            Change.Colour => !locked && s.State != SetupSlot.SlotState.Closed && NextColourRow(row, 1) != row,
            Change.Race => !locked && s.Races.Count > 1,
            Change.Hero => !locked && s.HeroSelectable && s.Race != null && s.Heroes.Count > 0,
            Change.Bonus => !locked && s.CanChangeBonus,
            _ => false,
        };
    }

    /// <summary>Applies a row button (00778500): <paramref name="right"/> = the "_right" button.</summary>
    public void Apply(int row, Change kind, bool right, string profileName)
    {
        if (!CanChange(row, kind)) return;
        var s = Slots[row];
        int dir = right ? 1 : -1;
        switch (kind)
        {
            case Change.State:
            {
                // 00777db0: row 0 stays human; the others cycle over the list without its first (human) entry.
                int n = _states[row].Count, i = _stateIndex[row] + dir;
                if (i >= n) i = 1;
                if (i < 1) i = n - 1;
                SetStateIndex(row, i, profileName);
                break;
            }
            case Change.Colour:
            {
                // 00c9e660: the row swaps its map player (colour) with the row holding the next colour.
                int other = NextColourRow(row, dir);
                var a = Slots[row];
                var b = Slots[other];
                (a.State, b.State) = (b.State, a.State);
                (a.Name, b.Name) = (b.Name, a.Name);
                (a.Race, b.Race) = (b.Race, a.Race);
                (a.Hero, b.Hero) = (b.Hero, a.Hero);
                (a.Bonus, b.Bonus) = (b.Bonus, a.Bonus);
                (_stateIndex[row], _stateIndex[other]) = (_stateIndex[other], _stateIndex[row]);
                foreach (var x in new[] { a, b }) Revalidate(x);
                break;
            }
            case Change.Race:
            {
                // 00c9df40: positions -1 (random) .. n-1 with wrap.
                int i = s.Race == null ? -1 : s.Races.IndexOf(s.Race);
                i += dir;
                if (i < -1) i = s.Races.Count - 1;
                if (i > s.Races.Count - 1) i = -1;
                s.Race = i == -1 ? null : s.Races[i];
                s.Hero = null;
                s.Heroes = HeroesFor(s);
                break;
            }
            case Change.Hero:
            {
                int i = s.Hero == null ? -1 : s.Heroes.IndexOf(s.Hero);
                i += dir;
                if (i < -1) i = s.Heroes.Count - 1;
                if (i > s.Heroes.Count - 1) i = -1;
                s.Hero = i == -1 ? null : s.Heroes[i];
                break;
            }
            case Change.Bonus:
            {
                // 00c9d860: 0..3 with wrap; the artifact bonus (1) is skipped for players without a hero.
                int b = s.Bonus, tries = 0;
                do
                {
                    b += dir;
                    if (b < 0) b = 3; else if (b > 3) b = 0;
                } while (!s.ArtifactBonus && b == 1 && ++tries < 4);
                s.Bonus = b;
                break;
            }
        }
    }

    void Revalidate(SetupSlot s)
    {
        if (s.Race != null && !s.Races.Contains(s.Race)) s.Race = null;
        if (s.Races.Count == 1) s.Race = s.Races[0];
        s.Heroes = HeroesFor(s);
        if (s.Hero != null && (!s.HeroSelectable || !s.Heroes.Contains(s.Hero))) s.Hero = null;
        if (!s.CanChangeBonus && s.Bonus == 1 && !s.ArtifactBonus) s.Bonus = 0;
        if (!s.ArtifactBonus && s.Bonus == 1) s.Bonus = 0;
    }

    /// <summary>
    /// 00c9d5b0: steps through (team, colour 1..8) from the row's own pair — colour first, the team advancing when the
    /// colour wraps — to the first other slot that is a computer able to take this row's player: a human only moves
    /// to a slot that can be human, a computer only to one that can be a computer but not human. None: the row itself.
    /// </summary>
    int NextColourRow(int row, int dir)
    {
        var me = Slots[row];
        var teams = Slots.Select(x => x.Team).Distinct().OrderBy(t => t).ToList();
        int team = teams.IndexOf(me.Team), colour = ColourNumber(me);
        for (int guard = 0; guard < 8 * teams.Count + 1; guard++)
        {
            colour += dir;
            if (colour < 1) { colour = 8; if (--team < 0) team = teams.Count - 1; }
            else if (colour > 8) { colour = 1; if (++team >= teams.Count) team = 0; }
            int target = Slots.FindIndex(x => ColourNumber(x) == colour && x.Team == teams[team]);
            if (target < 0) continue;
            if (target == row) return row;
            var t = Slots[target];
            if (t.State != SetupSlot.SlotState.Computer) continue;
            bool ok = me.State == SetupSlot.SlotState.Human ? t.CanBeHuman : t.CanBeComputer && !t.CanBeHuman;
            if (ok) return target;
        }
        return row;
    }

    static int ColourNumber(SetupSlot s) => Array.IndexOf(Colours, s.Colour) + 1;

    /// <summary>00c9d940: the game can start when the players that are not closed belong to at least two teams.</summary>
    public bool CanStart()
    {
        int team = int.MinValue;
        foreach (var s in Slots.Where(s => s.State != SetupSlot.SlotState.Closed))
        {
            if (team == int.MinValue) team = s.Team;
            else if (team != s.Team) return true;
        }
        return false;
    }

    /// <summary>The engine's start setup: closed players are left out.</summary>
    public GameSetup ToGameSetup(ulong seed, int difficulty)
    {
        var g = new GameSetup { Seed = seed, Difficulty = difficulty };
        foreach (var s in Slots.Where(s => s.State != SetupSlot.SlotState.Closed))
            g.Players.Add(new PlayerSetup
            {
                MapPlayer = s.MapPlayer, Human = s.State == SetupSlot.SlotState.Human,
                Race = s.Race, StartHero = s.Hero, Bonus = s.Bonus,
            });
        return g;
    }
}
