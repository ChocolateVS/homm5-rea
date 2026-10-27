using H5.Adventure.State;
using H5.Adventure.Map;

namespace H5.Adventure.Engine;

/// <summary>
/// Hire a hero at a map tavern (exe CHireHeroInTavernCmd, ctor 006390f0: player, tavern, tile, hero; sent by the
/// tavern screen's action button, 006350b0). <paramref name="TavernSlot"/> indexes the player's two tavern heroes
/// (player vfunc 0x14c(0/1)).
/// </summary>
public sealed record HireHeroInTavernCmd(int HeroId, int Tavern, int TavernSlot) : AdvCommand;

/// <summary>
/// One more thieves' guild line (screen 00635440): the exe sends a set-resource command (005d5440, vtable 00e0db8c,
/// execute 004273f0: player resource 6 = gold − 250); here the price is deducted by the engine instead of setting
/// the client's computed total. The reveal level itself lives in the screen (vfunc 0x34).
/// </summary>
public sealed record PayThievesGuildCmd : AdvCommand;

// Map tavern (BUILDING_TAVERN) hiring.
public sealed partial class Adventure
{
    public const string MapTavern = "BUILDING_TAVERN";
    public const string DenOfThieves = "BUILDING_DEN_OF_THIEVES";

    /// <summary>
    /// Hero hire price, exe 00b4ced0(hero, player, town): 500 x (the player's heroes + 4), i.e. 2500 with one hero,
    /// +500 per further hero; hiring in a town whose specialization (town vfunc 0xdc = 00cd3640, STownSpecialization
    /// +0x5c) is TOWN_BONUS_HEROES (0x25) pays (heroes + 4) x 4500 / 10. The map tavern (004f46a0 / 004f5700), the AI
    /// offer gold gate (0086b2db) and the surrender path (00c9758a) pass no town.
    /// </summary>
    public int HireCost(PlayerState p, TownState? town = null)
    {
        int n = S.Heroes.Count(h => h.Alive && h.Owner == p.Index) + 4;
        return town != null && SpecializationBonus(town) == "TOWN_BONUS_HEROES" ? n * 4500 / 10 : n * 500;
    }

    /// <summary>
    /// The map tavern's hiring status, exe 004f46a0: 3 = gold below <see cref="HireCost"/>, 5 = too many heroes
    /// (heroes ≥ max − SetPlayerHeroesCountNotForHire), 0 = may hire. The tavern screen shows hire_result_no_money /
    /// hire_result_max_heroes for 3 / 5 and hire_result_no_place when no free tile is left (00636d20).
    /// </summary>
    public int MapTavernHireStatus(PlayerState p)
    {
        if (p.Purse[Purse.Gold] < HireCost(p)) return 3;
        if (S.Heroes.Count(h => h.Alive && h.Owner == p.Index) >= HiringHeroLimit(p)) return 5;
        return 0;
    }

    /// <summary>
    /// Tiles a hero hired at the map tavern may appear on: the free tiles around the visiting hero (the exe collects
    /// them when the hero enters, 00873bf0 → tavern vfunc 0x8c, and each hire takes the LAST one of the list,
    /// 006350b0 → +0x1a8 − 4). The exe's collection order is not traced (GAP): here the neighbours in move order.
    /// </summary>
    public List<Tile> MapTavernFreeTiles(HeroState visitor)
    {
        var list = new List<Tile>();
        foreach (var (dx, dy) in Dirs)
        {
            var n = visitor.Pos.Offset(dx, dy);
            var f = Map.Floor(n.Floor);
            if (f != null && f.InBounds(n.X, n.Y) && !f.IsBlocked(n.X, n.Y) && !f.IsWater(n.X, n.Y) && HeroAt(n) == null && !ObjectsAt(n).Any())
                list.Add(n);
        }
        return list;
    }

    /// <summary>
    /// CHireHeroInTavernCmd execute 004f5700: refused unless 004f46a0 is 0 (gold, hero count); the player hires and
    /// pays the price (player vfunc 0x15c(hero, price)) and the hero is placed on the command's tile (008e36c0,
    /// refused when the tile is occupied, vfunc 0x100).
    /// </summary>
    string? DoPayThievesGuild(PayThievesGuildCmd c)
    {
        var p = Current;
        // 00635440 only sends it when gold − price ≥ 0.
        if (p.Purse[Purse.Gold] - ThievesInfo.Cost < 0) return "not enough gold";
        GiveOne(p, Purse.Gold, -ThievesInfo.Cost);
        return null;
    }

    string? DoHireHeroInTavern(HireHeroInTavernCmd c)
    {
        var p = Current;
        var visitor = S.Hero(c.HeroId);
        if (visitor == null || !visitor.Alive || visitor.Owner != p.Index) return "not your hero";
        if (c.Tavern < 0 || c.Tavern >= Map.Objects.Count || O(c.Tavern).Shared.Type != MapTavern) return "not a tavern";
        if (c.TavernSlot < 0 || c.TavernSlot >= p.Tavern.Length || p.Tavern[c.TavernSlot] is not { } defId) return "no hero in that slot";
        switch (MapTavernHireStatus(p))
        {
            case 3: return "not enough gold";
            case 5: return "too many heroes";
        }
        var tiles = MapTavernFreeTiles(visitor);
        if (tiles.Count == 0) return "no place for hero";
        var tile = tiles[^1];
        int cost = HireCost(p);
        var h = HireHero(defId, p.Index, tile, out bool returning);
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints;
        if (!returning) S.Heroes.Add(h);
        S.HeroPool.Remove(defId);
        foreach (var other in S.Players) for (int i = 0; i < other.Tavern.Length; i++) if (other.Tavern[i] == defId) other.Tavern[i] = null;
        GiveOne(p, Purse.Gold, -cost);
        Emit(new HeroHired(h.Id, defId, -1));
        RevealAround(h);
        return null;
    }
}
