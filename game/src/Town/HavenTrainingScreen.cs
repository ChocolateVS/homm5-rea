using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Haven Training Grounds (UI/HavenTraining, CHavenTraining@NUI, opened by enter_special → FUN_006184f0 →
/// FUN_006580d0). Row 0 is the garrison hero (heroicon0), row 1 the visiting hero (heroicon1); each stack's
/// button shows the transformer quote (FUN_00657820): Ok → state 0 + T_BUTTON_GREEN, 1/4 → disabled +
/// T_BUTTON_DISABLED, 5/7 → state 1 + T_BUTTON_RED, 6 → state 2 + T_BUTTON_YELLOW, 8 → state 1 +
/// T_BUTTON_RED_NO_MONEY, 9 → state 1 + T_BUTTON_RED_LIMIT_REACHED, others disabled. A green button asks for
/// confirmation and sends <see cref="TrainCreatureCmd"/> (FUN_00657e50, FORTHILL_UPGRADE_CREATURES_SOUND);
/// a red one plays FORTHILL_NO_RESOURCES_SOUND (FUN_00657580).
/// </summary>
public sealed partial class HavenTrainingScreen : TownSubScreen
{
    const string Texts = "UI/HavenTraining/Texts.(WindowRelatedTexts).xdb";

    public HavenTrainingScreen(TownScreen owner) : base(owner, "UI/HavenTraining.(WindowScreen).xdb")
    {
        Ui.SetVisible("dndicon", false);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < Army.Slots; i++)
            {
                int r = row, slot = i;
                if (Ui.Find($"armyholder{row}/creature{i}/button") is { } b) b.Clicked += _ => Train(r, slot);
            }
    }

    /// <summary>
    /// The screen opens only when the garrison or the visiting hero can use it (transformer slot 0 =
    /// FUN_00cceb70 mode 0: the town has the Training Grounds and the hero is a Knight).
    /// </summary>
    public static bool CanOpen(TownScreen owner) =>
        owner.Town.Type == "TOWN_HEAVEN" && owner.Town.Level(H5.Adventure.Engine.Adventure.TrainingGrounds) >= 1 &&
        new[] { owner.GarrisonHero(), owner.VisitingHero() }.Any(h => h is { Class: "HERO_CLASS_KNIGHT" });

    HeroState? RowHero(int row) => row == 0 ? Owner.GarrisonHero() : Owner.VisitingHero();

    Stack?[]? RowArmy(int row) => row == 0 ? Owner.GarrisonHero()?.Army ?? Owner.Town.Garrison : Owner.VisitingHero()?.Army;

    static string? Tip(string id) => UiGameRoot.RelatedTextAt(Texts, id);

    public override void Refresh()
    {
        base.Refresh();
        var game = Owner.Session.Game;
        Ui.Find("smallcaption")?.SetValue("remain", Owner.Town.TrainingPoints.ToString());
        for (int row = 0; row < 2; row++)
        {
            var hero = RowHero(row);
            var icon = Ui.Find($"heroicon{row}");
            if (icon != null)
            {
                icon.Visible = hero != null;
                if (hero != null) TownUi.SetPicture(icon, TownUi.HeroFace(hero, large: false));
            }
            var army = RowArmy(row);
            Ui.Find($"armyholder{row}")!.Visible = army != null;
            for (int i = 0; i < Army.Slots; i++)
            {
                var s = army?[i];
                var cell = Ui.Find($"armyholder{row}/creature{i}/icon");
                TownUi.SetPicture(cell, s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
                (cell?.FindChild("NumberOfCreatures", false, false) as UiNode)?.SetValue("NumberOfCreatures", s is { Count: > 0 } ? s.Count.ToString() : "");
                Ui.SetVisible($"armyholder{row}/creature{i}/selection", false);
                if (Ui.Find($"armyholder{row}/creature{i}/button") is not { } b) continue;
                b.Visible = s is { Count: > 0 };
                if (s is not { Count: > 0 }) continue;
                var q = game.TrainingQuote(Owner.Town, hero, i);
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
                b.Enabled = enabled;
                b.SetState(state);
                if (q.Target != null && q.Status is TrainStatus.Ok or TrainStatus.NoResources)
                    tip = $"{tip}<br>{TownUi.Text("UI/HavenTraining/ArmyHolder/Creature/training_cost.txt", "Training cost:")} {MapBuildingScreen.CostText(q.Cost)}";
                b.SetTooltip(tip);
            }
        }
    }

    void Train(int row, int slot)
    {
        var hero = RowHero(row);
        var q = Owner.Session.Game.TrainingQuote(Owner.Town, hero, slot);
        if (q.Status != TrainStatus.Ok || hero == null)
        {
            Audio.AudioSystem.PlayNamed("FORTHILL_NO_RESOURCES_SOUND");
            return;
        }
        string name = Game.Instance.Db.Creatures.TryGetValue(q.Target!, out var td) ? td.Name : q.Target!;
        var text = $"{TownUi.Text("UI/HavenTraining/ArmyHolder/Creature/training_cost.txt", "Training cost:")} {MapBuildingScreen.CostText(q.Cost)}";
        AdvDialogs.YesNo(this, name, text, TownUi.CreatureIcon(q.Target!), yes =>
        {
            if (!yes) return;
            if (Apply(new TrainCreatureCmd(Owner.Town.Object, hero.Id, slot) { Player = Me.Index }))
                Audio.AudioSystem.PlayNamed("FORTHILL_UPGRADE_CREATURES_SOUND");
        });
    }
}
