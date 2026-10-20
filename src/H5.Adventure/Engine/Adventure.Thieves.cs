using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Thieves' guild information (NWorld::CPlayersThievesInfo, ctor 00504380, vtable 00e1a37c).

/// <summary>
/// One line of a thieves' guild category. <see cref="Player"/> is -1 when the entry is hidden (exe player 0);
/// <see cref="CountType"/>: -2 unknown ("???", CountTypeUnknown), -1 exact <see cref="Value"/>, 0..8 the
/// CountType%d / CrCountType%d word (00503db0); <see cref="Value"/> is 0 unless exact.
/// <see cref="Precision"/> is the reveal argument the exe hands the entry transform (-1 hidden, 0 player only,
/// 1 word, 2+ exact; best heroes use it as detail flags 005040d0: &gt;0, &gt;1, &gt;2, &gt;3).
/// </summary>
public sealed record ThievesEntry(int Player, int CountType, int Value, int Precision, string? Hero = null, string? Creature = null);

/// <summary>A category in the exe order; <see cref="Name"/> is the Thieves.(WindowRelatedTexts) key.</summary>
public sealed record ThievesCategory(string Name, IReadOnlyList<ThievesEntry> Entries);

/// <summary>
/// The guild's ranked data (built once when the screen opens, 00505120 → 00504380) and its reveal level.
/// Each category holds the top N players (N = 3, or the number of players still in the game when that is
/// less), sorted by value descending (comparators 00503d70 / 00505150, stable merge sort 00505280).
/// </summary>
public sealed class ThievesInfo
{
    /// <summary>Exe vfunc 0x30 (00503da0): price of one more line, in gold.</summary>
    public const int Cost = 250;

    /// <summary>Category names in the exe order (source lists +0x08 … +0x80, getters 0x00 … 0x28).</summary>
    public static readonly string[] Names =
        { "HeroesCount", "TownsCount", "BestHeroes", "GoldAmount", "WoodOreAmount", "RareResAmount", "Obelisks", "BestCreature", "ArtifactsCount", "Army", "GoldIncome" };

    internal readonly List<(int Player, int Value, string? Hero, string? Creature)>[] Full;
    /// <summary>Reveal thresholds (+0x114): 1, then running sums of the group sizes (see the constructor).</summary>
    public readonly IReadOnlyList<int> Thresholds;
    /// <summary>Lines revealed (+0x110).</summary>
    public int Level { get; private set; }

    internal ThievesInfo(List<(int, int, string?, string?)>[] full, int initial)
    {
        Full = full;
        int Count(int i) => full[i].Count;
        // 00504d57..005050a5: group sizes, each added (and pushed) only when > 0; then +1 steps for
        // artifacts and income.
        var t = new List<int> { 1 };
        int level = 1;
        void Step(int n) { if (n > 0) { level += n; t.Add(level); } }
        Step(Count(0) + Count(1) + Count(2));            // heroes, towns, best heroes
        Step(Count(3) + Count(4));                       // gold, wood+ore
        Step(Count(5) + Count(6) + Count(7));            // rare, obelisks, best creature
        Step(Count(8) + Count(9));                       // artifacts, army
        Step(Count(10));                                 // gold income
        if (Count(8) > 0) Step(1);
        if (Count(10) > 0) Step(1);
        Thresholds = t;
        // Clamp the initial index to [0, count-1]; level = T[i] - 2, then one reveal step (005041a0).
        int idx = Math.Clamp(initial, 0, t.Count - 1);
        Level = t[idx] - 2;
        Buy();
    }

    /// <summary>Exe vfunc 0x2c (00504020): another line can still be bought.</summary>
    public bool CanBuy => Thresholds.Count > 0 && Level < Thresholds[^1] - 1;

    /// <summary>Exe vfunc 0x34 (005041a0): one more line (the screen pays <see cref="Cost"/> separately).</summary>
    public bool Buy()
    {
        if (!CanBuy) return false;
        Level++;
        return true;
    }

    /// <summary>
    /// The categories as shown at the current level (005041a0 → 005056b0 / 00507660 / 005077d0 / 00505840):
    /// walking the categories in order with a cursor c (lines before this category), a category is shown only
    /// while c &lt; level; its precision is max(0, (last i with T[i]-1 ≤ level) - 1 - (last i with T[i]-1 ≤ c)),
    /// and of its n lines the lowest min(n, level - c) are revealed (the rest are hidden).
    /// </summary>
    public IReadOnlyList<ThievesCategory> Visible()
    {
        var res = new List<ThievesCategory>();
        int c = 0;
        for (int k = 0; k < Full.Length; k++)
        {
            var src = Full[k];
            int n = src.Count;
            var entries = new List<ThievesEntry>();
            if (c < Level && n > 0)
            {
                int a = -1, b = -1;
                for (int i = 0; i < Thresholds.Count; i++)
                {
                    if (Thresholds[i] - 1 <= c) a = i;
                    if (Thresholds[i] - 1 <= Level) b = i - 1;
                }
                int prec = Math.Max(0, b - a);
                int vis = Math.Min(n, Level - c);
                for (int j = 0; j < n; j++)
                    entries.Add(Transform(Names[k], src[j], n - j > vis ? -1 : prec));
            }
            c += n;
            if (entries.Count > 0) res.Add(new ThievesCategory(Names[k], entries));
        }
        return res;
    }

    static ThievesEntry Transform(string cat, (int Player, int Value, string? Hero, string? Creature) e, int arg)
    {
        switch (cat)
        {
            case "BestHeroes":   // 005040d0: hidden → no player, no hero; else detail flags from arg
                return arg < 0 ? new ThievesEntry(-1, -2, 0, arg) : new ThievesEntry(e.Player, -1, e.Value, arg, Hero: e.Hero);
            case "Army":         // 00503e60: only the player, hidden when arg < 0
                return new ThievesEntry(arg < 0 ? -1 : e.Player, -2, 0, arg);
            case "BestCreature": // 00504140: hidden → no player, no creature; else the count as 00503e10
                if (arg < 0) return new ThievesEntry(-1, -2, 0, arg);
                var cnt = Count(e.Player, e.Value, arg);
                return cnt with { Creature = e.Creature };
            default:
                return Count(e.Player, e.Value, arg);
        }
    }

    /// <summary>00503e10: arg &lt; 0 hides the player; arg ≤ 0 unknown value; 1 the word; ≥ 2 the exact value.</summary>
    static ThievesEntry Count(int player, int value, int arg) =>
        arg < 1 ? new ThievesEntry(arg < 0 ? -1 : player, -2, 0, arg)
        : arg == 1 ? new ThievesEntry(player, Adventure.ThievesCountWord(value), 0, arg)
        : new ThievesEntry(player, -1, value, arg);
}

public sealed partial class Adventure
{
    /// <summary>
    /// 00503db0: the count word index: &lt;5 0 (few), &lt;10 1, &lt;20 2, &lt;50 3, &lt;100 4, &lt;250 5, &lt;500 6,
    /// &lt;1000 7, else 8 (CountType0..8 / CrCountType0..8 in UI/Tavern/Thieves.(WindowRelatedTexts)).
    /// </summary>
    public static int ThievesCountWord(int v) =>
        v < 5 ? 0 : v < 10 ? 1 : v < 20 ? 2 : v < 50 ? 3 : v < 100 ? 4 : v < 250 ? 5 : v < 500 ? 6 : v < 1000 ? 7 : 8;

    /// <summary>
    /// Player vfunc 0x138 (004fb790, player +0x234): dens of thieves owned; the den's owner change
    /// (00bca3c0 via vfunc 0x134) adds ±1. The den visit (00b04c72) opens the guild at index this + 1.
    /// </summary>
    public int ThievesDensOwned(PlayerState p) =>
        Map.Objects.Count(o => o.Shared.Type == "BUILDING_DEN_OF_THIEVES" && S.Objects.TryGetValue(o.Index, out var os) && !os.Removed && os.Owner == p.Index);

    /// <summary>
    /// CPlayersThievesInfo ctor (00504380) for the initial reveal index <paramref name="initial"/> (den of thieves:
    /// <see cref="ThievesDensOwned"/> + 1; the town / map tavern pass 0 and get no guild, see 00637b50).
    /// For every player in the game (vfunc 0x6c == 1; 1..8): heroes (vfunc 0xc0, if &gt; 0), towns (0xc4, if &gt; 0),
    /// gold (purse[6]), wood + ore, mercury + crystal + sulfur + gems, obelisks visited (if the map has obelisks and
    /// &gt; 0), daily gold income (0xe8 +0x18), artifacts (sum over heroes of worn + carried, 00b4ea50; if &gt; 0),
    /// the best hero (max hero power 004eb050), the best creature stack (max stack power 004e9b90, count shown) and
    /// the army rank (max over its heroes of the summed stack powers).
    /// </summary>
    public ThievesInfo BuildThievesInfo(int initial)
    {
        var full = new List<(int, int, string?, string?)>[ThievesInfo.Names.Length];
        for (int i = 0; i < full.Length; i++) full[i] = new();
        var heroPower = new List<double>();
        var creaturePower = new List<double>();
        var armyPower = new List<double>();
        int active = 0;
        bool obelisks = ObeliskCount > 0;
        foreach (var p in S.Players.Where(p => p.Alive).OrderBy(p => p.Index))
        {
            active++;
            var heroes = S.Heroes.Where(h => h.Alive && h.Owner == p.Index).ToList();
            int towns = S.Towns.Count(t => t.Owner == p.Index);
            if (heroes.Count > 0) full[0].Add((p.Index, heroes.Count, null, null));
            if (towns > 0) full[1].Add((p.Index, towns, null, null));
            full[3].Add((p.Index, p.Purse[Purse.Gold], null, null));
            full[4].Add((p.Index, p.Purse[Purse.Wood] + p.Purse[Purse.Ore], null, null));
            full[5].Add((p.Index, p.Purse[Purse.Mercury] + p.Purse[Purse.Crystal] + p.Purse[Purse.Sulfur] + p.Purse[Purse.Gem], null, null));
            full[10].Add((p.Index, ExpectedIncome(p)[Purse.Gold], null, null));
            if (obelisks && ObelisksVisited(p) is var ob and > 0) full[6].Add((p.Index, ob, null, null));

            HeroState? best = null; double bestP = 0;
            Stack? bestStack = null; double bestStackP = 0;
            double bestArmy = 0; bool anyArmy = false;
            int artifacts = 0;
            foreach (var h in heroes)
            {
                // TODO(exe): hero power is FUN_004eb050 (hero combat value); ArmyPower stands in.
                double hp = ArmyPower(h.Army.Live(), h);
                if (best == null || bestP < hp) { best = h; bestP = hp; }
                double sum = 0;
                foreach (var s in h.Army.Live())
                {
                    // TODO(exe): stack power is FUN_004e9b90 (with the hero's modifiers) × count; creature Power × count stands in.
                    double sp = Core.Creatures.TryGetValue(s.Creature, out var cd) ? (double)cd.Power * s.Count : 0;
                    if (bestStack == null || bestStackP < sp) { bestStack = s; bestStackP = sp; }
                    sum += sp;
                }
                if (!anyArmy || bestArmy < sum) { bestArmy = sum; anyArmy = true; }
                artifacts += h.Artifacts.Count + h.Backpack.Count;
            }
            if (best != null) { full[2].Add((p.Index, best.Level, best.DefId, null)); heroPower.Add(bestP); }
            if (bestStack != null) { full[7].Add((p.Index, bestStack.Count, null, bestStack.Creature)); creaturePower.Add(bestStackP); }
            if (artifacts > 0) full[8].Add((p.Index, artifacts, null, null));
            if (anyArmy) { full[9].Add((p.Index, 0, null, null)); armyPower.Add(bestArmy); }
        }
        int n = active > 2 ? 3 : active;
        for (int k = 0; k < full.Length; k++)
        {
            var keys = k switch { 2 => heroPower, 7 => creaturePower, 9 => armyPower, _ => null };
            var order = Enumerable.Range(0, full[k].Count)
                .OrderByDescending(i => keys != null ? keys[i] : full[k][i].Item2).ToList();   // stable
            full[k] = order.Take(n).Select(i => full[k][i]).ToList();
        }
        return new ThievesInfo(full, initial);
    }
}
