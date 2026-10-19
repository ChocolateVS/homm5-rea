using H5.Adventure.Engine;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Haven Training Grounds (UI/HavenTraining, CHavenTraining@NUI, opened by enter_special → FUN_006184f0 →
/// FUN_006580d0). Each stack's button shows the transformer quote (FUN_00657820): Ok → state 0 + T_BUTTON_GREEN,
/// 1/4 → disabled + T_BUTTON_DISABLED, 5/7 → state 1 + T_BUTTON_RED, 6 → state 2 + T_BUTTON_YELLOW, 8 → state 1 +
/// T_BUTTON_RED_NO_MONEY, 9 → state 1 + T_BUTTON_RED_LIMIT_REACHED, others disabled. A green button sends
/// <see cref="TrainCreatureCmd"/> at once (FUN_00657e50, FORTHILL_UPGRADE_CREATURES_SOUND); a red one plays
/// FORTHILL_NO_RESOURCES_SOUND (FUN_00657580).
/// </summary>
public sealed partial class HavenTrainingScreen : CreatureTransformScreen
{
    const string Texts = "UI/HavenTraining/Texts.(WindowRelatedTexts).xdb";
    const string CostLabel = "UI/HavenTraining/ArmyHolder/Creature/training_cost.txt";

    public HavenTrainingScreen(TownScreen owner) : base(owner, "UI/HavenTraining.(WindowScreen).xdb") { }

    /// <summary>
    /// The screen opens only when the garrison or the visiting hero can use it (transformer slot 0 =
    /// FUN_00cceb70 mode 0: the town has the Training Grounds and the hero is a Knight).
    /// </summary>
    public static bool CanOpen(TownScreen owner) =>
        owner.Town.Type == "TOWN_HEAVEN" && owner.Town.Level(H5.Adventure.Engine.Adventure.TrainingGrounds) >= 1 &&
        new[] { owner.GarrisonHero(), owner.VisitingHero() }.Any(h => h is { Class: "HERO_CLASS_KNIGHT" });

    static string? Tip(string id) => UiGameRoot.RelatedTextAt(Texts, id);

    protected override void RefreshCaption() => Ui.Find("smallcaption")?.SetValue("remain", Owner.Town.TrainingPoints.ToString());

    protected override TrainQuote Quote(int row, int slot) => Owner.Session.Game.TrainingQuote(Owner.Town, RowHero(row), slot);

    protected override (int State, bool Enabled, string? Tip) Button(TrainQuote q)
    {
        (int state, bool enabled, string? tip) = q.Status switch
        {
            TrainStatus.Ok => (0, true, Tip("T_BUTTON_GREEN")),
            TrainStatus.NoStack or TrainStatus.NotTrainable => (0, false, Tip("T_BUTTON_DISABLED")),
            TrainStatus.NoDwelling or TrainStatus.NotKnight => (1, true, Tip("T_BUTTON_RED")),
            TrainStatus.TopTier => (2, true, Tip("T_BUTTON_YELLOW")),
            TrainStatus.NoResources => (1, true, Tip("T_BUTTON_RED_NO_MONEY")),
            TrainStatus.LimitReached => (1, true, Tip("T_BUTTON_RED_LIMIT_REACHED")),
            _ => (0, false, null),
        };
        if (q.Target != null && q.Status is TrainStatus.Ok or TrainStatus.NoResources)
            tip = $"{tip}<br>{TownUi.Text(CostLabel, "Training cost:")} {MapBuildingScreen.CostText(q.Cost)}";
        return (state, enabled, tip);
    }

    /// <summary>
    /// Base click 0064b9b0: button state 0 (or 3) → the screen's action (+0x40 = 00657e50: validate, send
    /// <see cref="TrainCreatureCmd"/>, FORTHILL_UPGRADE_CREATURES_SOUND); state 1 (or 4) → +0x3c = 00657580
    /// (FORTHILL_NO_RESOURCES_SOUND); state 2 (yellow) does nothing. No confirmation box.
    /// </summary>
    protected override void Clicked(int row, int slot)
    {
        var hero = RowHero(row);
        var q = Quote(row, slot);
        int state = Button(q).State;
        if (state == 1) { Audio.AudioSystem.PlayNamed("FORTHILL_NO_RESOURCES_SOUND"); return; }
        if (state != 0 || q.Status != TrainStatus.Ok || hero == null) return;
        if (Apply(new TrainCreatureCmd(Owner.Town.Object, hero.Id, slot) { Player = Me.Index }))
            Audio.AudioSystem.PlayNamed("FORTHILL_UPGRADE_CREATURES_SOUND");
    }
}
