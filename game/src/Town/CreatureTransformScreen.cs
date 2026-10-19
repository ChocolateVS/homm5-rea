using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Shared layout of the town creature-transformer screens (CTransformCreatureScreenBase@NUI: UI/HavenTraining,
/// UI/NecropolisTransformCreature): row 0 = garrison hero (heroicon0) or the town garrison, row 1 = visiting hero
/// (heroicon1); every stack has a button whose state, enabling and tooltip come from the transformer quote.
/// </summary>
public abstract partial class CreatureTransformScreen : TownSubScreen
{
    protected CreatureTransformScreen(TownScreen owner, string xdb) : base(owner, xdb)
    {
        Ui.SetVisible("dndicon", false);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < Army.Slots; i++)
            {
                int r = row, slot = i;
                if (Ui.Find($"armyholder{row}/creature{i}/button") is { } b) b.Clicked += _ => Clicked(r, slot);
            }
    }

    protected HeroState? RowHero(int row) => row == 0 ? Owner.GarrisonHero() : Owner.VisitingHero();

    protected Stack?[]? RowArmy(int row) => row == 0 ? Owner.GarrisonHero()?.Army ?? Owner.Town.Garrison : Owner.VisitingHero()?.Army;

    /// <summary>The transformer quote for a row's stack.</summary>
    protected virtual TrainQuote Quote(int row, int slot) => new(TrainStatus.NoStack, null, 0, new H5.Core.Data.Resources(0, 0, 0, 0, 0, 0, 0));

    /// <summary>Button state, enabling and tooltip for a quote (the screen's status switch).</summary>
    protected virtual (int State, bool Enabled, string? Tip) Button(TrainQuote q) => (0, false, null);

    /// <summary>Button of a row's stack; the transformer screens map their quote, others override.</summary>
    protected virtual (int State, bool Enabled, string? Tip) ButtonFor(int row, int slot) => Button(Quote(row, slot));

    protected abstract void Clicked(int row, int slot);

    protected virtual void RefreshCaption() { }

    public override void Refresh()
    {
        base.Refresh();
        RefreshCaption();
        for (int row = 0; row < 2; row++)
        {
            var hero = RowHero(row);
            if (Ui.Find($"heroicon{row}") is { } icon)
            {
                icon.Visible = hero != null;
                if (hero != null) TownUi.SetPicture(icon, TownUi.HeroFace(hero, large: false));
            }
            var army = RowArmy(row);
            if (Ui.Find($"armyholder{row}") is { } holder) holder.Visible = army != null;
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
                var (state, enabled, tip) = ButtonFor(row, i);
                b.Enabled = enabled;
                b.SetState(state);
                b.SetTooltip(tip);
            }
        }
    }
}

/// <summary>
/// Necropolis Shrine of the Netherworld (UI/NecropolisTransformCreature, CNecropolisTransformCreature@NUI,
/// enter_special → FUN_00619e80 → FUN_0064ec40). Status switch FUN_0064e860: 0 → state 0 enabled; 4 → state 2
/// disabled + DENY_REASON_IMPOSSIBLE; 5 → state 4 disabled + DENY_REASON_NO_DWELLING_TEXT; others state 0
/// disabled. Click (base 0064b9b0 → +0x40 = FUN_0064e630): validate and send <see cref="TransformCreatureCmd"/>, no
/// confirmation and no sound.
/// </summary>
public sealed partial class NecropolisTransformScreen : CreatureTransformScreen
{
    const string Texts = "UI/NecropolisTransformCreature/relatedTexts.(WindowRelatedTexts).xdb";

    public NecropolisTransformScreen(TownScreen owner) : base(owner, "UI/NecropolisTransformCreature.(WindowScreen).xdb") { }

    protected override TrainQuote Quote(int row, int slot) => Owner.Session.Game.TransformQuote(Owner.Town, RowHero(row), slot);

    protected override (int State, bool Enabled, string? Tip) Button(TrainQuote q) => q.Status switch
    {
        TrainStatus.Ok => (0, true, TownUi.Text("UI/NecropolisTransformCreature/ArmyHolder/TTTransformButton.txt")),
        TrainStatus.NotTrainable => (2, false, UiGameRoot.RelatedTextAt(Texts, "DENY_REASON_IMPOSSIBLE")),
        TrainStatus.NoDwelling => (4, false, UiGameRoot.RelatedTextAt(Texts, "DENY_REASON_NO_DWELLING_TEXT")),
        _ => (0, false, null),
    };

    protected override void Clicked(int row, int slot)
    {
        var q = Quote(row, slot);
        if (q.Status != TrainStatus.Ok) return;
        Apply(new TransformCreatureCmd(Owner.Town.Object, RowHero(row)?.Id ?? -1, slot) { Player = Me.Index });
    }
}
