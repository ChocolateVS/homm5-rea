using H5.Adventure.State;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    /// <summary>Town ability 0x1d (table 00f7c998 row: town type 6 = Dungeon, TB_SPECIAL_4, level ≥ 1): the Trade Guild.</summary>
    public static bool HasTradeGuild(TownState t) => t.Type == H5.Combat.Towns.Dungeon && t.Level("TB_SPECIAL_4") >= 1;

    /// <summary>
    /// FUN_00cce410: without ability 0x1d the town's extra resource is cleared (+0x1dc = −1, +0x1e0 = 0); with it the
    /// resource is drawn from [0, 5] (008e7840) on the seed H(world seed (world vfunc 0x124 = +0x50), town world id
    /// (+8), current week (world vfunc 0xb0 = +0x58)) (008e7930), and the amount is 2 for wood / ore, else 1.
    /// The town's world object id (+8) is taken as the town's map object index (not traced).
    /// </summary>
    public void RollTradeGuild(TownState t)
    {
        if (!HasTradeGuild(t)) { t.TradeGuildResource = -1; t.TradeGuildAmount = 0; return; }
        int r = ExeHashRandom.Range(ExeHashRandom.Hash(S.WorldSeed, t.Object, Db.MoonWeekIndex(S.MoonWeek)), 0, 5);
        t.TradeGuildResource = r;
        t.TradeGuildAmount = r is Purse.Wood or Purse.Ore ? 2 : 1;
    }

    /// <summary>
    /// FUN_00cd2b90 (the town's daily handler), owner part: TOWN_BONUS_MERCURY / _SULFUR / _GEMS / _CRYSTALS
    /// (0x1e..0x21, jump table 00cd2cc4) add 2 of that resource, and the extra income (building interface vcall 0x88 =
    /// 00cce3c0 with gold excluded) adds +0x1e0 of resource +0x1dc; both straight to the purse, beside the town income.
    /// </summary>
    void TownDailyExtras(TownState t, Purse inc)
    {
        switch (SpecializationBonus(t))
        {
            case "TOWN_BONUS_MERCURY": inc[Purse.Mercury] += 2; break;
            case "TOWN_BONUS_SULFUR": inc[Purse.Sulfur] += 2; break;
            case "TOWN_BONUS_GEMS": inc[Purse.Gem] += 2; break;
            case "TOWN_BONUS_CRYSTALS": inc[Purse.Crystal] += 2; break;
        }
        if (t.TradeGuildResource is >= 0 and < 7) inc[t.TradeGuildResource] += t.TradeGuildAmount;
    }
}
