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
    /// FUN_00cd0190 (town vf 0x8c), called by CBuyBlackMarketArtifactCmd (execute 00432640) after the artifact is given:
    /// with ability 0x1d, by the artifact's class (record +0x24, FUN_00ca63d0): minor gold +25 and, when the drawn
    /// resource is wood or ore, amount +1; major gold +50, amount +1; relic gold +100, amount +2. The gold (+0x1d8) stays
    /// and is paid with the town income (00cd43f0 → vf 0x88); the amount lasts until the next draw (00cce410).
    /// </summary>
    public void TradeGuildArtifactBought(TownState t, string artifact)
    {
        if (!HasTradeGuild(t) || !Db.ArtifactClass.TryGetValue(artifact, out var cls)) return;
        bool woodOre = t.TradeGuildResource is Purse.Wood or Purse.Ore;
        switch (cls)
        {
            case "ARTF_CLASS_MINOR": t.TradeGuildGold += 25; if (woodOre) t.TradeGuildAmount += 1; break;
            case "ARTF_CLASS_MAJOR": t.TradeGuildGold += 50; t.TradeGuildAmount += 1; break;
            case "ARTF_CLASS_RELIC": t.TradeGuildGold += 100; t.TradeGuildAmount += 2; break;
        }
    }

    /// <summary>
    /// FUN_00cd2b90 = IAdvMapWeeklyUpdate vf 0 (CAdvMapTown +0x108, vtable 00e95824), run for every town by the world's
    /// new week (009949d0 → 00993cd0, after the growth). With an owner (world vf 0x68): a copy of the owner's purse gets
    /// TOWN_BONUS_MERCURY / _SULFUR / _GEMS / _CRYSTALS (0x1e..0x21, jump table 00cd2cc4) +2, the Mystic Pond (vf 0x84 =
    /// 00cce350) and the Trade Guild resource (vf 0x88 = 00cce3c0, gold excluded), and becomes the purse (player vf
    /// 0xdc). Then, owned or not, the Trade Guild draws the coming week's resource (00cce410) and the training points
    /// are reset to their limit (+0x2d0 = FUN_00cd1850).
    /// </summary>
    void TownWeeklyUpdate(TownState t)
    {
        if (Player(t.Owner) is { } p)
        {
            var inc = new Purse();
            switch (SpecializationBonus(t))
            {
                case "TOWN_BONUS_MERCURY": inc[Purse.Mercury] += 2; break;
                case "TOWN_BONUS_SULFUR": inc[Purse.Sulfur] += 2; break;
                case "TOWN_BONUS_GEMS": inc[Purse.Gem] += 2; break;
                case "TOWN_BONUS_CRYSTALS": inc[Purse.Crystal] += 2; break;
            }
            MysticPond(t, inc);
            if (t.TradeGuildResource is >= 0 and < 7) inc[t.TradeGuildResource] += t.TradeGuildAmount;
            if (inc.R.Any(v => v != 0))
            {
                for (int i = 0; i < 7; i++) p.Purse[i] += inc[i];
                Emit(new ResourcesChanged(p.Index, inc.R, (int[])p.Purse.R.Clone()));
            }
        }
        RollTradeGuild(t);
        t.TrainingPoints = TrainingPointsMax(t);
    }

    /// <summary>
    /// FUN_00cce350 (town vf 0x84): with ability 0xc (table 00f7c998: Preserve TB_SPECIAL_2 level 1, the Mystic Pond)
    /// resource r = 008e7840(H(world seed, town +8, week) via 008e7930, 0, 5), +10 of it for wood / ore, else +5.
    /// </summary>
    public void MysticPond(TownState t, Purse inc)
    {
        if (t.Type != H5.Combat.Towns.Sylvan || t.Level("TB_SPECIAL_2") < 1) return;
        int r = ExeHashRandom.Range(ExeHashRandom.Hash(S.WorldSeed, t.Object, Db.MoonWeekIndex(S.MoonWeek)), 0, 5);
        inc[r] += r is Purse.Wood or Purse.Ore ? 10 : 5;
    }
}
