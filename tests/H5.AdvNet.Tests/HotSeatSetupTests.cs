using H5.AdvNet;

namespace H5.AdvNet.Tests;

/// <summary>UI/HotSeatWait's setup (CHotSeatLogic 0077c5c0 / 0077bc20 / 0077b760 / 0077baa0 over the model 00ca0240).</summary>
public class HotSeatSetupTests
{
    static CustomGameSetup Setup()
    {
        var lm = Env.Maps.Load(new AdvNetSettings { Map = Env.TestMap });
        return CustomGameSetup.ForHotSeat(lm.Map, lm.Db);
    }

    [Fact]
    public void FirstTwoHumanCapableSlotsStartHuman()
    {
        var s = Setup();
        int left = 2;
        foreach (var x in s.Slots)
        {
            var expected = left > 0 && x.CanBeHuman ? SetupSlot.SlotState.Human
                : !x.CanBeComputer ? (x.CanBeHuman ? SetupSlot.SlotState.Human : SetupSlot.SlotState.Closed)
                : SetupSlot.SlotState.Computer;
            if (left > 0 && x.CanBeHuman) left--;
            Assert.Equal(expected, x.State);
        }
        var humans = s.Slots.Where(x => x.State == SetupSlot.SlotState.Human).ToList();
        Assert.True(humans.Count >= 2);
        // 00c9dd70 (keep flag 0): a human slot whose bonus can change starts with the random bonus.
        Assert.All(humans.Where(x => x.CanChangeBonus), x => Assert.Equal(0, x.Bonus));
        Assert.True(s.CanStartHotSeat());
    }

    [Fact]
    public void StateArrowCyclesComputerHumanClosed()
    {
        var s = Setup();
        int row = s.Slots.FindIndex(x => x.State == SetupSlot.SlotState.Computer && x.CanBeHuman);
        Assert.True(row >= 0);
        var slot = s.Slots[row];
        slot.CanClose = true;
        Assert.True(s.CanChange(row, CustomGameSetup.Change.State));
        s.Apply(row, CustomGameSetup.Change.State, true, "");
        Assert.Equal(SetupSlot.SlotState.Human, slot.State);
        s.Apply(row, CustomGameSetup.Change.State, false, "");
        Assert.Equal(SetupSlot.SlotState.Closed, slot.State);
        s.Apply(row, CustomGameSetup.Change.State, true, "");
        Assert.Equal(SetupSlot.SlotState.Computer, slot.State);
        // Without CanClose the human goes back to computer.
        slot.CanClose = false;
        s.Apply(row, CustomGameSetup.Change.State, true, "");
        s.Apply(row, CustomGameSetup.Change.State, true, "");
        Assert.Equal(SetupSlot.SlotState.Computer, slot.State);
    }

    [Fact]
    public void BonusArrowsFollowTheSlotAndReachTheGameSetup()
    {
        var s = Setup();
        int row = 0;
        var slot = s.Slots[row];
        slot.CanChangeBonus = false;
        Assert.False(s.CanChange(row, CustomGameSetup.Change.Bonus));
        slot.CanChangeBonus = true;
        slot.ArtifactBonus = true;
        slot.Bonus = 0;
        Assert.True(s.CanChange(row, CustomGameSetup.Change.Bonus));
        s.Apply(row, CustomGameSetup.Change.Bonus, true, "");
        Assert.Equal(1, slot.Bonus);
        s.Apply(row, CustomGameSetup.Change.Bonus, false, "");
        s.Apply(row, CustomGameSetup.Change.Bonus, false, "");
        Assert.Equal(3, slot.Bonus);
        var g = s.ToGameSetup(1, 1);
        Assert.Equal(3, g.Players.Single(p => p.MapPlayer == slot.MapPlayer).Bonus);
    }
}
