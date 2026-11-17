using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Assets.Scenes;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Campaign bonus offer and application (H5_Game.exe 006b25e0 / 006b2270 offer, 006b29d0 → 005a5990 → command
/// 005ab370 / vtable 00e0f50c, execute 0042bcb0, bonus objects 00c65b10, player vf 0x230 = 00500e70).
/// </summary>
[Collection("adv")]
public class CampaignBonusTests(Fixture f)
{
    static CampaignBonus Resource(int gold) { var b = new CampaignBonus { Type = "E_BONUS_RESOURCE" }; b.Resources[6] = gold; return b; }

    /// <summary>004ff2a0 / 00500860 set player +0x694 to −1; 0042bcb0 stores the 1-based number through vf 0x230 first.</summary>
    [Fact]
    public void ChosenNumberIsStoredOnThePlayerAndStartsAtMinusOne()
    {
        var g = f.NewGame();
        int p = g.Current.Index;
        Assert.Equal(-1, g.Player(p)!.CampaignBonus);
        int gold = g.Player(p)!.Purse[6];
        Assert.True(g.ApplyCampaignBonus(p, Resource(750), 2));
        Assert.Equal(2, g.Player(p)!.CampaignBonus);
        Assert.Equal(gold + 750, g.Player(p)!.Purse[6]);
    }

    /// <summary>006b25e0 case 3: offered only with a positive amount; case 4: spell id 0 (SPELL_NONE) never; NONE never.</summary>
    [Fact]
    public void OfferNeedsAPositiveResourceAndAKnownPayload()
    {
        var g = f.NewGame();
        int p = g.Current.Index;
        Assert.False(g.CampaignBonusOffered(p, Resource(0)));
        Assert.True(g.CampaignBonusOffered(p, Resource(1)));
        Assert.False(g.CampaignBonusOffered(p, new CampaignBonus { Type = "E_BONUS_SPELL", Spell = "SPELL_NONE" }));
        Assert.True(g.CampaignBonusOffered(p, new CampaignBonus { Type = "E_BONUS_SPELL", Spell = "SPELL_MAGIC_ARROW" }));
        Assert.False(g.CampaignBonusOffered(p, new CampaignBonus { Type = "E_BONUS_ARTIFACT", Artifact = "ARTIFACT_NO_SUCH" }));
        Assert.False(g.CampaignBonusOffered(p, new CampaignBonus { Type = "E_BONUS_NONE" }));
    }

    /// <summary>
    /// 006b2270: the main town (vf 4) runs a not-free CUpgradeTownBuildingCmd (00e0d898 / 0042fd40) in test mode, so
    /// the bonus is offered only when the town's build check (00cd6a90) accepts it now — money and the daily build
    /// included. 00c65920 gives it free (vf 0x6c with free = 1: 00cd4a40 skips that check).
    /// </summary>
    [Fact]
    public void BuildingIsOfferedOnlyWhenTheMainTownCouldBuildItAndIsGivenFree()
    {
        var g = f.NewGame();
        int p = g.Current.Index;
        var town = g.MainTownOf(p)!;
        g.Current.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 999999));
        var type = town.MaxLevel.Keys.First(k => g.BuildCheck(town, k, g.Current.Purse) == BuildDenial.CanBuild);
        var b = new CampaignBonus { Type = "E_BONUS_BUILDING", Building = type };
        Assert.True(g.CampaignBonusOffered(p, b));
        town.BuiltToday = true;
        Assert.False(g.CampaignBonusOffered(p, b));
        int level = town.Level(type);
        var purse = g.Current.Purse.R.ToArray();
        Assert.True(g.ApplyCampaignBonus(p, b, 1));
        Assert.Equal(level + 1, town.Level(type));
        Assert.Equal(purse, g.Current.Purse.R);
    }

    /// <summary>00c657e0: the Target hero when the player has it (vf 0xbc), else the main hero (vf 0xc).</summary>
    [Fact]
    public void HeroBonusGoesToTheTargetWhenOwnedElseTheMainHero()
    {
        var g = f.NewGame("A2C1M1");
        int p = g.S.Players.First(x => g.Map.Players[x.Index].MainHeroUid != null).Index;
        var main = g.MainHeroOf(p)!;
        var mainObj = g.Map.Objects.First(o => o.Uid == g.Map.Players[p].MainHeroUid);
        Assert.Equal(mainObj.Hero!.Shared!.Id, main.DefId);
        var other = g.S.Heroes.FirstOrDefault(h => h.Alive && h.Owner != p);
        if (other != null) Assert.Same(main, g.CampaignBonusHero(p, other.DefId));
        var mine = g.S.Heroes.FirstOrDefault(h => h.Alive && h.Owner == p && h != main);
        if (mine != null) Assert.Same(mine, g.CampaignBonusHero(p, mine.DefId));
        Assert.Same(main, g.CampaignBonusHero(p, ""));
    }
}
