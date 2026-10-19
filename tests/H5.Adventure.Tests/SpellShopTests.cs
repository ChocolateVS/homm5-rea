using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Arcane Library (BUILDING_SPELL_SHOP): CSpellShop stock 00a35520, price 00a34690, CBuySpellShopSpellCmd 004299b0.</summary>
[Collection("adv")]
public class SpellShopTests(Fixture f)
{
    [Fact]
    public void StockIsThreeFourthAndThreeFifthCircleSpellsPricedInTheSchoolResource()
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var o = g.Map.Objects.First(x => x.Kind == ObjectKind.Building && x.ActiveTiles.Any() && AdventureDb.BankFor(x.Shared.Type) == null);
        var old = o.Shared;
        o.Shared = new SharedDef { Type = Engine.Adventure.SpellShop, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
        g.StockSpellShops();
        var stock = g.S.Obj(o.Index).ShopSpells!;
        Assert.Equal(6, stock.Length);
        Assert.Equal(6, stock.Distinct().Count());
        Assert.All(stock.Take(3), id => Assert.Equal(4, f.Core.Spell(id!)!.Level));
        Assert.All(stock.Skip(3), id => Assert.Equal(5, f.Core.Spell(id!)!.Level));
        Assert.All(stock, id => Assert.Contains(f.Core.Spell(id!)!.School, new[] { "MAGIC_SCHOOL_DARK", "MAGIC_SCHOOL_LIGHT", "MAGIC_SCHOOL_DESTRUCTIVE", "MAGIC_SCHOOL_SUMMONING" }));
        var fire = Engine.Adventure.SpellShopCost(f.Core.Spells.Values.First(s => s.Level == 5 && s.School == "MAGIC_SCHOOL_DESTRUCTIVE"));
        Assert.Equal(25, fire.Sulfur);
        Assert.Equal(0, fire.Gold);

        // Buying: the hero needs the school mastery the learn rule asks for; the price is paid and the entry cleared.
        var spell = stock[0]!;
        var def = f.Core.Spell(spell)!;
        string skill = def.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => "HERO_SKILL_DESTRUCTIVE_MAGIC", "MAGIC_SCHOOL_DARK" => "HERO_SKILL_DARK_MAGIC",
            "MAGIC_SCHOOL_LIGHT" => "HERO_SKILL_LIGHT_MAGIC", _ => "HERO_SKILL_SUMMONING_MAGIC",
        };
        h.Skills.RemoveAll(s => s.SkillId == skill);
        h.Skills.Add(new H5.Core.Data.SkillEntry(skill, H5.Core.Data.Mastery.Expert));
        h.Spells.Remove(spell);
        g.Current.Purse = new Purse(new H5.Core.Data.Resources(0, 0, 20, 20, 20, 20, 0));
        var r = g.Apply(new BuySpellShopSpellCmd(h.Id, o.Index, 0) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(spell, h.Spells);
        Assert.Null(g.S.Obj(o.Index).ShopSpells![0]);
        Assert.Equal(5, g.Current.Purse.R.Sum(v => v) - 60);
        Assert.False(g.Apply(new BuySpellShopSpellCmd(h.Id, o.Index, 0) { Player = h.Owner }).Ok);
    }
}
