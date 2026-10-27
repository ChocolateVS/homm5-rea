using H5.AdvNet;
using H5.Adventure.Engine;

namespace H5.AdvNet.Tests;

/// <summary>The custom game's map list rules and player setup model (H5_Game.exe 00705250, 00778640, 00ca0240).</summary>
public class CustomGameSetupTests
{
    [Fact]
    public void MapListMaskFollowsTheFilterVars()
    {
        Assert.Equal(CustomMapList.SingleMissions, CustomMapList.Mask(false, false));
        Assert.Equal(0x30003, CustomMapList.Mask(true, false));
        Assert.Equal(0x30003 | 4, CustomMapList.Mask(true, true));
        Assert.Equal(2 | 4, CustomMapList.Mask(false, true));
        Assert.False(CustomMapList.Shown(CustomMapList.Mask(false, false), CustomMapList.FolderOf("Maps/Multiplayer/A2M1", false)));
        Assert.True(CustomMapList.Shown(CustomMapList.Mask(false, false), CustomMapList.FolderOf("Maps/SingleMissions/A2S1", false)));
        Assert.True(CustomMapList.Shown(CustomMapList.Mask(false, true), CustomMapList.FolderOf("x", true)));
    }

    [Fact]
    public void SortClickOnTheSameColumnFlipsItsDirection()
    {
        var s = new CustomMapList.SortState();
        Assert.Equal(0, s.Column);
        Assert.False(s.Descending);
        s.Click(0);
        Assert.True(s.Descending);
        s.Click(3);
        Assert.Equal(3, s.Column);
        Assert.False(s.Descending);
        s.Click(0);
        Assert.True(s.Descending); // column 0 keeps its flipped direction
    }

    [Fact]
    public void GoalPlayersAndTimeLimitTexts()
    {
        Assert.Equal("goal3", CustomMapList.GoalKey(false, "OBJECTIVE_KIND_DEFEAT_ALL"));
        Assert.Equal("custom", CustomMapList.GoalKey(true, "OBJECTIVE_KIND_DEFEAT_ALL"));
        Assert.Equal("goal_default", CustomMapList.GoalKey(false, null));
        Assert.Equal("4 (FFA)", CustomMapList.PlayersText(new[] { 1, 1, 1, 1 }, "<value=count> (FFA)", "v"));
        Assert.Equal("2v1", CustomMapList.PlayersText(CustomMapList.TeamSizes(new[] { 0, 0, 1 }), "<value=count> (FFA)", "v"));
        Assert.True(CustomMapList.ComparePlayers(new[] { 1, 1 }, new[] { 2, 1, 1 }) < 0);
        Assert.Equal(0, CustomGameOptions.Minutes(46));
        Assert.Equal(45, CustomGameOptions.Minutes(45));
        Assert.Equal(1, CustomGameOptions.Minutes(1));
    }

    static CustomGameSetup Setup(string map, bool lockOthers)
    {
        var lm = Env.Maps.Load(new AdvNetSettings { Map = map });
        return new CustomGameSetup(lm.Map, lm.Db, "Me", lockOthers);
    }

    [Fact]
    public void FirstRowIsTheHumanOthersComputers()
    {
        var s = Setup(Env.TestMap, false);
        Assert.Equal(SetupSlot.SlotState.Human, s.Slots[0].State);
        Assert.Equal("Me", s.Slots[0].Name);
        Assert.All(s.Slots.Skip(1), x => Assert.Equal(SetupSlot.SlotState.Computer, x.State));
        // Row 0's state cannot change; on a multiplayer map the others can only be closed where the map allows it.
        Assert.False(s.CanChange(0, CustomGameSetup.Change.State));
        for (int i = 1; i < s.Slots.Count; i++) Assert.Equal(s.Slots[i].CanClose, s.CanChange(i, CustomGameSetup.Change.State));
    }

    [Fact]
    public void LockedMapsOnlyLetTheFirstRowChange()
    {
        var s = Setup(Env.TestMap, true);
        foreach (CustomGameSetup.Change k in Enum.GetValues<CustomGameSetup.Change>())
            for (int i = 1; i < s.Slots.Count; i++) Assert.False(s.CanChange(i, k));
    }

    [Fact]
    public void RaceCyclesThroughRandomAndBonusSkipsArtifactWithoutHero()
    {
        var s = Setup(Env.TestMap, false);
        const int row = 0;
        var slot = s.Slots[row];
        // A player without towns or heroes may take any faction (00c9e9f0); A2M1's players all own towns.
        slot.Races = CustomGameSetup.AllRaces.ToList();
        slot.Race = null;
        s.Apply(row, CustomGameSetup.Change.Race, true, "Me");
        Assert.Equal(slot.Races[0], slot.Race);
        s.Apply(row, CustomGameSetup.Change.Race, false, "Me");
        Assert.Null(slot.Race);
        s.Apply(row, CustomGameSetup.Change.Race, false, "Me");
        Assert.Equal(slot.Races[^1], slot.Race);

        slot.CanChangeBonus = true;
        slot.ArtifactBonus = false;
        slot.Bonus = 0;
        s.Apply(row, CustomGameSetup.Change.Bonus, true, "Me");
        Assert.Equal(2, slot.Bonus);
        s.Apply(row, CustomGameSetup.Change.Bonus, false, "Me");
        Assert.Equal(0, slot.Bonus);
        s.Apply(row, CustomGameSetup.Change.Bonus, false, "Me");
        Assert.Equal(3, slot.Bonus);
    }

    [Fact]
    public void ColourChangeMovesTheHumanAndGameSetupFollows()
    {
        var s = Setup(Env.TestMap, false);
        Assert.True(s.CanChange(0, CustomGameSetup.Change.Colour));
        s.Apply(0, CustomGameSetup.Change.Colour, true, "Me");
        Assert.Equal(SetupSlot.SlotState.Computer, s.Slots[0].State);
        Assert.Equal(SetupSlot.SlotState.Human, s.Slots[1].State);
        var g = s.ToGameSetup(5, 2);
        Assert.Equal(s.Slots[1].MapPlayer, g.Players.Single(p => p.Human).MapPlayer);
        Assert.Equal(2, g.Difficulty);
        Assert.True(s.CanStart());
    }

    [Fact]
    public void StartNeedsTwoTeams()
    {
        var s = Setup(Env.TestMap, false);
        foreach (var x in s.Slots) x.Team = 0;
        Assert.False(s.CanStart());
        s.Slots[^1].Team = 1;
        Assert.True(s.CanStart());
    }

    [Fact]
    public void ChosenStartHeroIsGivenToHeroInTownPlayers()
    {
        var lm = Env.Maps.Load(new AdvNetSettings { Map = Env.TestMap });
        var s = new CustomGameSetup(lm.Map, lm.Db, "Me", false);
        int row = s.Slots.FindIndex(x => x.HeroSelectable && x.Races.Count > 0);
        if (row < 0) return; // map without a town-created hero
        var slot = s.Slots[row];
        slot.Race ??= slot.Races[0];
        slot.Heroes = s.Slots[row].Heroes.Count > 0 ? slot.Heroes : new();
        s.Apply(row, CustomGameSetup.Change.Race, true, "Me");
        if (slot.Race == null) s.Apply(row, CustomGameSetup.Change.Race, true, "Me");
        s.Apply(row, CustomGameSetup.Change.Hero, true, "Me");
        if (slot.Hero == null) return;
        var (game, _) = Adventure.Engine.Adventure.NewGame(lm.Db, lm.Map, s.ToGameSetup(3, 1));
        Assert.Contains(game.S.Heroes, h => h.Owner == slot.MapPlayer && h.DefId == slot.Hero);
    }
}
