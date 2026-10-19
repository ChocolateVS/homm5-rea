using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Inferno Sacrificial Pit (UI/InfernoSacrifice, CInfernoSacrificeScreen@NUI, enter_special → FUN_00618870 →
/// FUN_00657060) and Dungeon Ritual Pit (UI/DungeonSacrifice, CDungeonSacrificeScreen@NUI, FUN_006187c0 →
/// FUN_0064f7d0), both on CInfernoDungeonSacrificeScreenBase (layout FUN_00653ca0: Hero%d with Levelup / ExpView,
/// Creature%d_%02d with Icon, CountView, SacrificeBtn, Highlight). Row 1 = garrison hero (or the town garrison for
/// the Ritual Pit), row 2 = visiting hero. Inferno buttons carry BUTTON_TOOLTIP with creature_name /
/// creature_exp (FUN_00656b80); the Dungeon screen fills GrowthPanel_1/2 (FUN_0064fa30) with the two bonuses.
/// </summary>
public sealed partial class SacrificeScreen : TownSubScreen
{
    readonly bool _growth;
    string Texts => _growth ? "UI/DungeonSacrifice/relatedTexts.(WindowRelatedTexts).xdb" : "UI/InfernoSacrifice/relatedTexts.(WindowRelatedTexts).xdb";

    public SacrificeScreen(TownScreen owner, bool growth)
        : base(owner, growth ? "UI/DungeonSacrifice.(WindowScreen).xdb" : "UI/InfernoSacrifice.(WindowScreen).xdb")
    {
        _growth = growth;
        Ui.Bus.On("inferno_dungeon_sacrifice_close", Close);
        Ui.SetVisible("dndicon", false);
        Ui.SetVisible("DragDrop", false);
        for (int row = 1; row <= 2; row++)
            for (int i = 0; i < Army.Slots; i++)
            {
                int r = row, slot = i;
                if (Ui.Find($"Creature{row}_{i + 1:00}/SacrificeBtn") is { } b) b.Clicked += _ => Sacrifice(r, slot);
            }
        // Caption and description: the pit building's name and description (TODO(exe): source of the texts).
        string building = growth ? H5.Adventure.Engine.Adventure.RitualPit : H5.Adventure.Engine.Adventure.SacrificialPit;
        if (TownData.Defs(owner.Session.Db, owner.Town).FirstOrDefault(d => d.Type == building) is { } def)
        {
            var view = TownData.View(def);
            Ui.Find("DialogCaptionView")?.SetText($"<h1>{view.Name}");
            Ui.Find("DialogDescView")?.SetText($"{(growth ? "<body_bright>" : "<body>")}{view.Description}");
        }
    }

    HeroState? RowHero(int row) => row == 1 ? Owner.GarrisonHero() : Owner.VisitingHero();

    Stack?[]? RowArmy(int row) => RowHero(row)?.Army ?? (_growth && row == 1 ? Owner.Town.Garrison : null);

    public override void Refresh()
    {
        base.Refresh();
        var game = Owner.Session.Game;
        for (int row = 1; row <= 2; row++)
        {
            var hero = RowHero(row);
            if (Ui.Find($"Hero{row}") is { } face)
            {
                face.Visible = hero != null;
                if (hero != null) TownUi.SetPicture(face, TownUi.HeroFace(hero, large: false));
            }
            Ui.SetVisible($"Hero{row}/Levelup", false);
            Ui.SetVisible($"Hero{row}/ExpView", false);
            var army = RowArmy(row);
            for (int i = 0; i < Army.Slots; i++)
            {
                string cell = $"Creature{row}_{i + 1:00}";
                var s = army?[i];
                if (Ui.Find(cell) is { } c) c.Visible = army != null;
                TownUi.SetPicture(Ui.Find($"{cell}/Icon"), s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
                Ui.Find($"{cell}/Icon/CountView")?.SetText(s is { Count: > 0 } ? $"<body><right>{s.Count}" : "");
                Ui.SetVisible($"{cell}/Highlight", false);
                if (Ui.Find($"{cell}/SacrificeBtn") is not { } b) continue;
                b.Visible = s is { Count: > 0 };
                if (s is not { Count: > 0 }) continue;
                var (status, value) = game.SacrificeQuote(Owner.Town, hero, i, _growth);
                b.Enabled = status == TrainStatus.Ok;
                var tip = UiGameRoot.RelatedTextAt(Texts, "BUTTON_TOOLTIP") ?? "";
                string name = Game.Instance.Db.Creatures.TryGetValue(s.Creature, out var cd) ? cd.Name : s.Creature;
                b.SetTooltip(tip.Replace("<value=creature_exp>", value.ToString()).Replace("<value=creature_name>", name));
            }
        }
        if (_growth)
        {
            var (witches, minotaurs) = H5.Adventure.Engine.Adventure.RitualPitBonus(Owner.Town.SacrificedHitPoints);
            SetGrowthPanel(1, "TB_DWELLING_2", witches);
            SetGrowthPanel(2, "TB_DWELLING_3", minotaurs);
        }
    }

    void SetGrowthPanel(int index, string dwelling, int bonus)
    {
        var creature = Owner.Session.Db.Building(Owner.Town.Type, dwelling, 1)?.Creature;
        string name = creature != null && Game.Instance.Db.Creatures.TryGetValue(creature, out var cd) ? cd.Name : "";
        TownUi.SetPicture(Ui.Find($"GrowthPanel_{index}/Icon"), creature != null ? TownUi.CreatureIcon(creature) : null);
        if (Ui.Find($"GrowthPanel_{index}/DescView") is { } d) { d.SetValue("name", name); d.SetValue("bonus", bonus.ToString()); }
        Ui.SetVisible($"GrowthPanel_{index}/SacrificeFX", false);
    }

    void Sacrifice(int row, int slot)
    {
        var hero = RowHero(row);
        AdvCommand cmd = _growth
            ? new SacrificeForGrowthCmd(Owner.Town.Object, hero?.Id ?? -1, slot) { Player = Me.Index }
            : new SacrificeForExperienceCmd(Owner.Town.Object, hero?.Id ?? -1, slot) { Player = Me.Index };
        Apply(cmd);
    }
}
