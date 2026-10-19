using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Sylvan Avenger's Guild: replace the hero's favoured enemies. Exe: CSetFavoriteEnemiesCmd (vtable 0xe0dfc4,
/// built by FUN_00648ba0, execute 00427a30) → hero +0x25c (IAvenger) slot +8 = 009a8e70: copy the list, then the
/// lenient list check 009a8d30(…, 0). Empty entries are "".
/// </summary>
public sealed record SetFavoriteEnemiesCmd(int HeroId, List<string> Enemies) : AdvCommand;

// Favoured enemies (CAvenger, vtable 0xe63054; created with the hero when it has HERO_SKILL_AVENGER, FUN_00b4cf30).
public sealed partial class Adventure
{
    /// <summary>Town abilities 10 / 11 of the table at 00f7c998: Preserve TB_SPECIAL_0 (Avenger's Guild) level 1 / 2.</summary>
    public const string AvengersGuild = "TB_SPECIAL_0";

    void AddKills(HeroState h, IEnumerable<(string Creature, int Count)> killed)
    {
        foreach (var (cr, n) in killed)
            if (n > 0) h.Kills[cr] = (int)Math.Min(int.MaxValue, (long)h.Kills.GetValueOrDefault(cr) + n);
    }

    static bool IsUpgradeCreature(CreatureDef c) =>
        c.BaseCreature is { Length: > 0 } b && b != "CREATURE_UNKNOWN" && b != c.Id;

    /// <summary>
    /// Maximum list length (IAvenger slot +0x14 = 009a8d20): the hero's HERO_SKILL_AVENGER mastery (hero +0x174(0x10)).
    /// </summary>
    public static int FavoriteEnemiesMax(HeroState h) => (int)h.MasteryOf("HERO_SKILL_AVENGER");

    /// <summary>
    /// Kills counted for a creature line (009a8be0): the creature's kills plus those of every upgrade (creature
    /// +0x104 list), each the sum of two counters (hero kill counter + the avenger's own counter created by
    /// FUN_00a84630 — TODO(exe): the second counter's writer is not traced; it is not kept here).
    /// </summary>
    public int FavoriteEnemyKills(HeroState h, string creature)
    {
        int n = h.Kills.GetValueOrDefault(creature);
        if (Core.Creatures.TryGetValue(creature, out var c))
            foreach (var u in c.Upgrades) n += h.Kills.GetValueOrDefault(u);
        return n;
    }

    /// <summary>
    /// IAvenger slot +0x10 = 009a8be0: a creature may be a favoured enemy when it is a base creature (creature
    /// +0x100 = 0) and the kills of its line reach 2 × its WeeklyGrowth (creature +0xa8).
    /// </summary>
    public bool FavoriteEnemyAllowed(HeroState h, string creature)
    {
        if (creature.Length == 0 || !Core.Creatures.TryGetValue(creature, out var c) || IsUpgradeCreature(c)) return false;
        return c.WeeklyGrowth * 2 <= FavoriteEnemyKills(h, creature);
    }

    /// <summary>
    /// List check 009a8d30. Strict (the screen's cycling and OK, FUN_00647ae0 / 00647bb0): false when the list is
    /// longer than the maximum or any entry before the trailing empties is not allowed or repeated. Lenient (the
    /// command): the list is cut to the maximum, disallowed entries and later duplicates are emptied in place.
    /// </summary>
    public bool CheckFavoriteEnemies(HeroState h, List<string> list, bool strict)
    {
        int max = FavoriteEnemiesMax(h);
        if (list.Count > max)
        {
            if (strict) return false;
            list.RemoveRange(max, list.Count - max);
        }
        int end = list.Count;
        while (end > 0 && list[end - 1].Length == 0) end--;
        for (int i = 0; i < end; i++)
        {
            if (!FavoriteEnemyAllowed(h, list[i]))
            {
                if (strict) return false;
                list[i] = "";
            }
            for (int j = i + 1; j < end; j++)
                if (list[j] == list[i])
                {
                    if (strict) return false;
                    list[j] = "";
                }
        }
        return true;
    }

    /// <summary>Whether the creature counts as one of the hero's favoured enemies (IAvenger 009a8ba0: upgrade → base).</summary>
    public bool IsFavoriteEnemy(HeroState h, string creature)
    {
        string id = Core.Creatures.TryGetValue(creature, out var c) && IsUpgradeCreature(c) ? c.BaseCreature : creature;
        return id.Length > 0 && h.FavoriteEnemies.Contains(id);
    }

    string? DoSetFavoriteEnemies(SetFavoriteEnemiesCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        // 00427a30: the hero must have its IAvenger (only heroes with the Avenger skill).
        if (h.MasteryOf("HERO_SKILL_AVENGER") == Mastery.None) return "the hero has no favoured enemies";
        var list = (c.Enemies ?? new()).Select(e => e ?? "").ToList();
        CheckFavoriteEnemies(h, list, strict: false);
        h.FavoriteEnemies = list;
        return null;
    }
}
