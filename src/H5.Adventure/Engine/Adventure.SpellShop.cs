using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Arcane Library (BUILDING_SPELL_SHOP, building type 0x75): buy spell number <paramref name="Entry"/> (0..5) of the
/// shop's stock. Exe: CBuySpellShopSpellCmd (vtable 0xe0ed8c, built by the BuySpell screen's buy_spell handler
/// 007bad30 → 004e9830, execute 004299b0).
/// </summary>
public sealed record BuySpellShopSpellCmd(int HeroId, int Object, int Entry) : AdvCommand;

public sealed partial class Adventure
{
    public const string SpellShop = "BUILDING_SPELL_SHOP";

    /// <summary>The magic schools a shop stocks, in the exe's order (DAT_00f48c14: dark, light, destructive, summoning).</summary>
    static readonly string[] SpellShopSchools = { "MAGIC_SCHOOL_DARK", "MAGIC_SCHOOL_LIGHT", "MAGIC_SCHOOL_DESTRUCTIVE", "MAGIC_SCHOOL_SUMMONING" };

    /// <summary>
    /// A shop spell's price (FUN_00a34690): circle 4 → 15, circle 5 → 25 of the school's resource — destructive sulfur,
    /// dark mercury, light crystal, summoning gems; nothing else.
    /// </summary>
    public static Resources SpellShopCost(SpellDef? spell)
    {
        if (spell == null || spell.Level is not (4 or 5)) return new Resources(0, 0, 0, 0, 0, 0, 0);
        int amount = spell.Level == 4 ? 15 : 25;
        int kind = spell.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => Purse.Sulfur,
            "MAGIC_SCHOOL_DARK" => Purse.Mercury,
            "MAGIC_SCHOOL_LIGHT" => Purse.Crystal,
            "MAGIC_SCHOOL_SUMMONING" => Purse.Gem,
            _ => -1,
        };
        return kind < 0 ? new Resources(0, 0, 0, 0, 0, 0, 0) : Res(kind, amount);
    }

    /// <summary>
    /// Restocks every Arcane Library: at map start (IInitializableObject, 00ce26b0 → new CSpellShop) and on each new
    /// month (IAdvMapMonthlyUpdate, 00ce2690). CSpellShop 00a35520: count, per circle-4/5 spell of the four schools, the
    /// copies in the players' towns' mage guilds, plus one for being a spell of that school and circle; then for circle
    /// 4 and then 5, three times: take the spells of that circle with the lowest count, pick one at random and remove
    /// it from the pool. Six entries, three per circle.
    /// </summary>
    public void StockSpellShops()
    {
        foreach (var o in Map.Objects.Where(o => o.Shared.Type == SpellShop))
            S.Obj(o.Index).ShopSpells = RollSpellShop();
    }

    string?[] RollSpellShop()
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in S.Towns.Where(t => t.Owner >= 0))
            for (int lvl = 4; lvl <= 5; lvl++)
                foreach (var id in t.MageGuild[lvl - 1])
                    if (Core.Spell(id) is { } d && SpellShopSchools.Contains(d.School))
                        count[id] = count.GetValueOrDefault(id) + 1;
        foreach (var school in SpellShopSchools)
            foreach (var d in Core.Spells.Values.Where(s => s.Stock && s.ManaCost > 0 && s.School == school && s.Level is 4 or 5))
                count[d.Id] = count.GetValueOrDefault(d.Id) + 1;
        var stock = new List<string?>();
        for (int lvl = 4; lvl <= 5; lvl++)
            for (int k = 0; k < 3; k++)
            {
                var level = count.Where(kv => Core.Spell(kv.Key)?.Level == lvl).ToList();
                if (level.Count == 0) { stock.Add(null); continue; }
                int min = level.Min(kv => kv.Value);
                var pool = level.Where(kv => kv.Value == min).Select(kv => kv.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();
                var pick = pool[S.Rng.Next(pool.Count)];
                count.Remove(pick);
                stock.Add(pick);
            }
        return stock.ToArray();
    }

    /// <summary>
    /// 004299b0: the entry must hold a spell (bought entries are cleared, 00a34be0) the hero does not know and can learn
    /// (hero vfuncs 0xdc / 0xe4), the player must afford its price (009d0720: every resource); the hero learns it
    /// (vfunc 0xe8) and the price is paid (0xe0). On success the exe flies TT_SPELL_LEARNED over the hero.
    /// </summary>
    string? DoBuySpellShopSpell(BuySpellShopSpellCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (c.Object < 0 || c.Object >= Map.Objects.Count || O(c.Object).Shared.Type != SpellShop) return "not a spell shop";
        var os = S.Obj(c.Object);
        if (os.ShopSpells is not { } stock || c.Entry < 0 || c.Entry >= stock.Length || stock[c.Entry] is not { } spell) return "no such spell";
        if (h.Spells.Contains(spell)) return "spell already known";
        if (!CanLearnSpell(h, spell)) return "cannot learn this spell";
        var cost = SpellShopCost(Core.Spell(spell));
        var p = Current;
        if (!p.Purse.CanAfford(cost)) return "not enough resources";
        LearnSpell(h, spell, force: false);
        p.Purse.Add(cost, -1);
        Emit(new ResourcesChanged(p.Index, new Purse(Purse.Scale(cost, -1)).R, (int[])p.Purse.R.Clone()));
        stock[c.Entry] = null;
        return null;
    }
}
