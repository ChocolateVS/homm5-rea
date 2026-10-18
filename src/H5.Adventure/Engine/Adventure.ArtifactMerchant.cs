using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Buy one of an artifact merchant's six artifacts: a town's Artifact Merchant (Academy TB_SPECIAL_3, the hero
/// in town) or the map's Artifact Merchant wagon (BUILDING_BLACK_MARKET, the hero standing at it).
/// Exe: CBuyBlackMarketArtifactCmd (vtable 0xe0e270, execute 0x432640).
/// </summary>
public sealed record BuyMerchantArtifactCmd(int HeroId, int Market, int Slot) : AdvCommand;

// Artifact merchants (exe CBlackMarket, vtable 0xe9663c).
public sealed partial class Adventure
{
    public const int MerchantSlots = 6;
    public const string ArtifactMerchantBuilding = "TB_SPECIAL_3";

    /// <summary>
    /// Stock rule of FUN_00ced030: slots 0–2 minor, 3–4 major, 5 relic artifacts, no artifact twice, never an
    /// artifact marked CanBeGeneratedToSell = false.
    /// </summary>
    static string MerchantSlotClass(int slot) => slot > 4 ? "ARTF_CLASS_RELIC" : slot > 2 ? "ARTF_CLASS_MAJOR" : "ARTF_CLASS_MINOR";

    bool IsArtifactMerchant(int market) =>
        S.Town(market) is { } t ? t.Type == "TOWN_ACADEMY" && t.Level(ArtifactMerchantBuilding) > 0
        : Map.Objects.ElementAtOrDefault(market)?.Shared.Type == "BUILDING_BLACK_MARKET" && !S.Obj(market).Removed;

    void Restock(int market)
    {
        var stock = new string?[MerchantSlots];
        for (int i = 0; i < MerchantSlots; i++)
        {
            var cls = MerchantSlotClass(i);
            var pool = Db.ArtifactClass.Where(kv => kv.Value == cls && !Db.ArtifactsNotForSale.Contains(kv.Key) && !stock.Contains(kv.Key))
                .Select(kv => kv.Key).Where(a => Map.ArtifactIds.Count == 0 || !Map.ArtifactIds.Contains(a))
                .OrderBy(a => a, StringComparer.Ordinal).ToList();
            stock[i] = pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
        }
        S.ArtifactMarkets[market] = stock;
    }

    /// <summary>The merchant's current stock (empty until stocked).</summary>
    public IReadOnlyList<string?> MerchantStock(int market) =>
        S.ArtifactMarkets.TryGetValue(market, out var stock) ? stock : Array.Empty<string?>();

    /// <summary>
    /// Stocks every merchant that has no stock yet: map merchants at map start (FUN_00a32b10) and town
    /// merchants when built (FUN_00cd4a40); engine-only, so all peers roll the same artifacts.
    /// </summary>
    public void StockArtifactMerchants()
    {
        foreach (var o in Map.Objects.Where(o => o.Shared.Type == "BUILDING_BLACK_MARKET"))
            if (!S.ArtifactMarkets.ContainsKey(o.Index)) Restock(o.Index);
        foreach (var t in S.Towns)
            if (!S.ArtifactMarkets.ContainsKey(t.Object) && IsArtifactMerchant(t.Object)) Restock(t.Object);
    }

    /// <summary>
    /// Town merchants restock at the start of each week. TODO(exe): the weekly restock call is not traced;
    /// map merchants keep their stock.
    /// </summary>
    void RestockTownArtifactMerchants()
    {
        foreach (var t in S.Towns)
            if (IsArtifactMerchant(t.Object)) Restock(t.Object);
    }

    /// <summary>Gold price (FUN_00ca6410 → FUN_00bce000): the artifact's CostOfGold, halved in the Week of Trade.</summary>
    public int MerchantPrice(string artifact)
    {
        int cost = Core.Artifacts.TryGetValue(artifact, out var a) ? a.CostOfGold : 0;
        return S.MoonWeek == "WEEK_OF_TRADE" ? cost / 2 : cost;
    }

    string? DoBuyMerchantArtifact(BuyMerchantArtifactCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != p.Index) return "not your hero";
        if (!IsArtifactMerchant(c.Market)) return "no artifact merchant";
        if (S.Town(c.Market) is { } t)
        {
            if (t.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        }
        else if (!HeroAt(h, O(c.Market))) return "hero must visit the merchant";
        var stock = MerchantStock(c.Market);
        if (c.Slot is < 0 || c.Slot >= stock.Count || stock[c.Slot] is not { } art) return "nothing for sale there";
        int price = MerchantPrice(art);
        if (p.Purse[Purse.Gold] < price) return "not enough gold";
        GiveOne(p, Purse.Gold, -price);
        S.ArtifactMarkets[c.Market][c.Slot] = null;
        GiveArtifact(h, art);
        return null;
    }
}
