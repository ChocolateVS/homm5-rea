using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Buy a rumour in a town tavern (exe CRerollRumourCmd, vtable 00e197b4, ctor 00639170, sent by the tavern screen's
/// rumours page action 00635320 with the screen's price +0x194 and the town's rumour list +0x254).
/// </summary>
public sealed record BuyRumourCmd(int TownObject) : AdvCommand;

// Tavern rumours.
public sealed partial class Adventure
{
    /// <summary>Rumour price: RPGStats +0x54 (00b4c260) = DefaultStats RumorCost (100 gold in 3.1 and MMH 5.5).</summary>
    public int RumourCost => (int)Db.Stats.Get("RumorCost", 100);

    /// <summary>
    /// CRerollRumourCmd execute 004f4750: the player pays the price in gold (player vfunc 0xe0 with a gold purse
    /// 00423c20; nothing happens when it fails), then the rumour list is rerolled (its vfunc 0, given the tavern) and
    /// the screen shows every entry (FIRST_RUMOR / RUMORS_FORMAT, 006372f3). The town's tavern screen gets the list
    /// from the town (player vfunc 0x90 → town → vfunc 0x60).
    /// GAP: the reroll (CRumours, generator 008e85b0 with the RUMOUR_FORMAT_PLAYER_* / _OBJECT_* rumours about players
    /// and map objects, weights 100, duplicate test 008ea5d0, map rumours MapRumours 00a0b4b0) is not traced; here one
    /// common rumour (CommonGameRumours, TownType TOWN_NO_TYPE or the town's type) is drawn by its Weight and appended.
    /// </summary>
    string? DoBuyRumour(BuyRumourCmd c)
    {
        var t = S.Town(c.TownObject);
        var p = Current;
        if (t == null || t.Owner != p.Index) return "not your town";
        if (t.Level("TB_TAVERN") <= 0) return "no tavern";
        int cost = RumourCost;
        if (p.Purse[Purse.Gold] < cost) return "not enough gold";
        GiveOne(p, Purse.Gold, -cost);
        if (DrawRumour(t) is { } text) t.Rumours.Add(text);
        return null;
    }

    /// <summary>A weighted draw from the common rumours told in this town type (see <see cref="DoBuyRumour"/>, GAP).</summary>
    string? DrawRumour(TownState t)
    {
        var pool = Db.CommonRumours.Where(r => r.Weight > 0 && (r.TownType is "" or "TOWN_NO_TYPE" || r.TownType == t.Type)).ToList();
        int total = pool.Sum(r => r.Weight);
        if (total <= 0) return null;
        int roll = S.Rng.Next(total);
        foreach (var r in pool)
        {
            if (roll < r.Weight) return r.Text;
            roll -= r.Weight;
        }
        return pool[^1].Text;
    }
}
