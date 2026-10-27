using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Buy one of an artifact merchant's six artifacts: a town's Artifact Merchant (Academy TB_SPECIAL_3 or the Dungeon
/// Trade Guild, the hero in town) or the map's Artifact Merchant wagon (BUILDING_BLACK_MARKET, the hero standing at it).
/// Exe: CBuyBlackMarketArtifactCmd (vtable 0xe0e270, execute 0x432640).
/// </summary>
public sealed record BuyMerchantArtifactCmd(int HeroId, int Market, int Slot) : AdvCommand;

// Artifact merchants (exe CBlackMarket, vtable 0xe9663c).
public sealed partial class Adventure
{
    public const int MerchantSlots = 6;
    public const string ArtifactMerchantBuilding = "TB_SPECIAL_3";

    /// <summary>
    /// Stock rule of FUN_00ced030 (CBlackMarket vtable 0xe9663c slot 14): slot i draws from the map's artifact
    /// generator (world vfunc 0x38 → 009cf360) with class lo = hi = 0 for slots 0–2, 1 for 3–4, 2 for slot 5;
    /// the artifacts already drawn for this stock are excluded and a relic slot takes only artifacts not generated
    /// yet (param 5 = class 2). The artifact the slot held before goes back to the generator (009cf320).
    /// So the map's artifact list (artifactIDs) is the allowed set, not a ban list.
    /// </summary>
    static int MerchantSlotClass(int slot) => slot > 4 ? 2 : slot > 2 ? 1 : 0;

    bool IsArtifactMerchant(int market) =>
        S.Town(market) is { } t ? TownHasArtifactMerchant(t)
        : Map.Objects.ElementAtOrDefault(market)?.Shared.Type == "BUILDING_BLACK_MARKET" && !S.Obj(market).Removed;

    /// <summary>
    /// FUN_00cce140: town ability 0x17 (Academy TB_SPECIAL_3) or 0x1d (Dungeon TB_SPECIAL_4, the Trade Guild). With it
    /// 00cd4a40 creates the town's CBlackMarket (+0x1c8, town vf 0x10c = 00cd8ed0) on any build when there is none yet,
    /// and the town screen (0061b760 cases 0x17 / 0x1d → 00618a90) opens the merchant screen.
    /// </summary>
    public static bool TownHasArtifactMerchant(TownState t) =>
        (t.Type == "TOWN_ACADEMY" && t.Level(ArtifactMerchantBuilding) > 0) || HasTradeGuild(t);

    void Restock(int market)
    {
        var old = S.ArtifactMarkets.TryGetValue(market, out var prev) ? prev : null;
        var stock = new string?[MerchantSlots];
        var drawn = new List<string>();
        for (int i = 0; i < MerchantSlots; i++)
        {
            int c = MerchantSlotClass(i);
            // TODO(exe): the exe seeds the draw per slot (world vfunc 0x124(i, seed) → 008e7a40); one game RNG here.
            var art = GenerateArtifact(c, c, firstPassOnly: c == 2, exclude: drawn);
            if (old != null && i < old.Length && old[i] is { } was) ReleaseArtifact(was);
            stock[i] = art;
            if (art != null) drawn.Add(art);
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
        if (S.Town(c.Market) is { } town) TradeGuildArtifactBought(town, art);   // 00432640: market owner vf 0x8c
        return null;
    }
}
