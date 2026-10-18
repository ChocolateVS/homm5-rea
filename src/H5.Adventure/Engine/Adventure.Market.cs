using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Exchange resources at a marketplace (town building TB_MARKETPLACE) or a Trading Post map object
/// (MarketObject = that object's index; the hero must be standing on it).
/// From/To are Purse indices (0 Wood … 6 Gold); Amount is how much of From the player offers.
/// </summary>
public sealed record ExchangeResourcesCmd(int From, int To, int Amount, int MarketObject = -1) : AdvCommand;

/// <summary>Sell an artifact carried by a hero visiting one of the player's towns that has a marketplace.</summary>
public sealed record SellArtifactCmd(int HeroId, string Artifact, bool FromBackpack = false) : AdvCommand;

/// <summary>A hero sold an artifact at a marketplace for <paramref name="Gold"/>.</summary>
public sealed record ArtifactSold(int HeroId, string Artifact, int Gold) : AdvEvent;

// Resource exchange exactly as H5_Game.exe 3.1 does it.
//
// Rates: FUN_008e7b80(give, marketplaces) copies one row of seven ints (one per resource received)
// from three static tables chosen by what is given — wood/ore (give < 2), mercury..gems (2..5), gold
// (6) — with the marketplace count clamped to 1..9. The tables are built at start-up by the static
// initialiser at 00dc64a0.. (rows 0xff7648 + 0x1c·(n-1) etc., seven pushed values per row, first
// argument = wood). Exchange: CExchangeResourcesCmd::Execute (FUN_004f4c10): for a non-gold target,
// received = offered / rate and the cost is received × rate (the remainder stays with the player);
// for gold, received = offered × rate and the cost is the whole offer.
//
// Marketplace count: each owned town with a marketplace counts once, a town whose specialisation has
// TOWN_BONUS_MARKETPLACE ("Merchant's Fair", fan manual p.244+) counts twice. A Trading Post trades at
// the 3-marketplace rates (fan manual "Adventure Map Locations": "the trade rates are the same as when
// owning 3 Marketplaces"). TODO(exe): the player's count getter (CPlayer vtable +0xb0, read at
// FUN_008e70d0) is not traced; the counting rule above follows the manual.
//
// Artifact selling: price = round(gold cost × Marketplace_ArtifactSellCostOfGoldCoef) — the
// adventure constant at RPGStats+0x1c8 (data 0.5) as used by the market screen tooltip FUN_00623160.
public sealed partial class Adventure
{
    static readonly int[][] RatesCommon =
    {
        new[] { 10, 10, 20, 20, 20, 20, 25 }, new[] { 7, 7, 14, 14, 14, 14, 37 }, new[] { 5, 5, 10, 10, 10, 10, 50 },
        new[] { 5, 5, 9, 9, 9, 9, 62 }, new[] { 4, 4, 7, 7, 7, 7, 75 }, new[] { 3, 3, 6, 6, 6, 6, 87 },
        new[] { 3, 3, 5, 5, 5, 5, 100 }, new[] { 3, 3, 5, 5, 5, 5, 112 }, new[] { 2, 2, 4, 4, 4, 4, 120 },
    };

    static readonly int[][] RatesRare =
    {
        new[] { 5, 5, 10, 10, 10, 10, 50 }, new[] { 4, 4, 7, 7, 7, 7, 75 }, new[] { 3, 3, 5, 5, 5, 5, 100 },
        new[] { 2, 2, 4, 4, 4, 4, 125 }, new[] { 2, 2, 4, 4, 4, 4, 150 }, new[] { 2, 2, 3, 3, 3, 3, 175 },
        new[] { 2, 2, 3, 3, 3, 3, 200 }, new[] { 2, 2, 3, 3, 3, 3, 225 }, new[] { 1, 1, 2, 2, 2, 2, 250 },
    };

    static readonly int[][] RatesGold =
    {
        new[] { 2500, 2500, 5000, 5000, 5000, 5000, 0 }, new[] { 1667, 1667, 3333, 3333, 3333, 3333, 0 },
        new[] { 1250, 1250, 2500, 2500, 2500, 2500, 0 }, new[] { 1000, 1000, 2000, 2000, 2000, 2000, 0 },
        new[] { 834, 834, 1667, 1667, 1667, 1667, 0 }, new[] { 715, 715, 1429, 1429, 1429, 1429, 0 },
        new[] { 625, 625, 1250, 1250, 1250, 1250, 0 }, new[] { 556, 556, 1111, 1111, 1111, 1111, 0 },
        new[] { 500, 500, 1000, 1000, 1000, 1000, 0 },
    };

    /// <summary>Marketplaces counted for the exchange rates (0 = the player cannot trade in towns).</summary>
    public int MarketplaceCount(PlayerState p)
    {
        int n = 0;
        foreach (var t in S.Towns)
        {
            if (t.Owner != p.Index || t.Level("TB_MARKETPLACE") < 1) continue;
            n += SpecializationBonus(t) == "TOWN_BONUS_MARKETPLACE" ? 2 : 1;
        }
        return n;
    }

    /// <summary>Rate row for giving <paramref name="give"/> with <paramref name="marketplaces"/> counted (FUN_008e7b80).</summary>
    public static int[] MarketRates(int give, int marketplaces)
    {
        if (give is < 0 or > 6 || marketplaces < 1) return new int[7];
        int row = Math.Min(marketplaces, 9) - 1;
        var table = give < 2 ? RatesCommon : give < 6 ? RatesRare : RatesGold;
        return (int[])table[row].Clone();
    }

    /// <summary>
    /// What an exchange yields: how much of <paramref name="to"/> is received and how much of
    /// <paramref name="from"/> it costs (FUN_004f4c10). Received is 0 when the offer is too small.
    /// </summary>
    public static (int Received, int Cost) MarketQuote(int from, int to, int amount, int marketplaces)
    {
        if (from == to || amount <= 0) return (0, 0);
        int rate = MarketRates(from, marketplaces)[to];
        if (rate <= 0) return (0, 0);
        if (to == Purse.Gold) return (amount * rate, amount);
        int received = amount / rate;
        return (received, received * rate);
    }

    /// <summary>Marketplace count that applies to an exchange (a Trading Post fixes it at 3).</summary>
    int ExchangeMarketplaces(PlayerState p, int marketObject, out string? error)
    {
        error = null;
        if (marketObject >= 0)
        {
            var o = Map.Objects.ElementAtOrDefault(marketObject);
            if (o == null || o.Shared.Type != "BUILDING_TRADING_POST" || S.Obj(marketObject).Removed) { error = "not a trading post"; return 0; }
            if (!S.Heroes.Any(h => h.Alive && h.Owner == p.Index && HeroAt(h, o))) { error = "no hero at the trading post"; return 0; }
            return 3;
        }
        int n = MarketplaceCount(p);
        if (n < 1) error = "no marketplace";
        return n;
    }

    string? DoExchange(ExchangeResourcesCmd c)
    {
        var p = Current;
        if (c.From is < 0 or > 6 || c.To is < 0 or > 6 || c.From == c.To) return "bad resources";
        int markets = ExchangeMarketplaces(p, c.MarketObject, out var err);
        if (err != null) return err;
        if (c.Amount <= 0 || c.Amount > p.Purse[c.From]) return "not enough resources";
        var (received, cost) = MarketQuote(c.From, c.To, c.Amount, markets);
        if (received <= 0) return "offer too small";
        var d = new int[7];
        d[c.From] = -cost;
        d[c.To] = received;
        p.Purse[c.From] -= cost;
        p.Purse[c.To] += received;
        Emit(new ResourcesChanged(p.Index, d, (int[])p.Purse.R.Clone()));
        return null;
    }

    /// <summary>Gold paid for an artifact at a marketplace.</summary>
    public int ArtifactSellPrice(string artifact)
    {
        if (!Core.Artifacts.TryGetValue(artifact, out var def)) return 0;
        double coef = Core.Stats.Get("adventure/Marketplace_ArtifactSellCostOfGoldCoef", 0.5);
        return (int)Math.Round(def.CostOfGold * coef, MidpointRounding.ToEven);
    }

    string? DoSellArtifact(SellArtifactCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        var town = h.InTown >= 0 ? S.Town(h.InTown) : null;
        if (town == null || town.Owner != h.Owner || town.Level("TB_MARKETPLACE") < 1) return "no marketplace here";
        var list = c.FromBackpack ? h.Backpack : h.Artifacts;
        if (!list.Contains(c.Artifact)) return "artifact not carried";
        int price = ArtifactSellPrice(c.Artifact);
        if (price <= 0) return "cannot sell this artifact";
        list.Remove(c.Artifact);
        Emit(new ArtifactSold(h.Id, c.Artifact, price));
        GiveOne(Current, Purse.Gold, price);
        return null;
    }
}
