using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Sacrificial Altar (UI/SacrificeAltar, ID_SACRIFICE_ALTAR_SCREEN; screen on the Inferno/Dungeon sacrifice base,
/// FUN_006548e0, with the visiting hero as row 2: Hero2, Creature2_01..07 with Icon / CountView / SacrificeBtn).
/// Caption / description T_SACRIFICE_ALTAR_CAPTION / _DESCRIPTION (FUN_007b6bd0); button tooltip BUTTON_TOOLTIP with
/// creature_name / creature_exp (FUN_007b6960). A button sends <see cref="SacrificeCreatureOnAltarCmd"/> with the
/// whole stack (FUN_007b6eb0 → FUN_007b7270); inferno_dungeon_sacrifice_close closes.
/// </summary>
public sealed partial class SacrificeAltarScreen : MapBuildingScreen
{
    const string Texts = "UI/SacrificialAltar/relatedTexts.(WindowRelatedTexts).xdb";

    public SacrificeAltarScreen(AdventureScreen adv, int heroId, int altar) : base(adv, "UI/SacrificeAltar.(WindowScreen).xdb", heroId, altar)
    {
        Ui.Bus.On("inferno_dungeon_sacrifice_close", Close);
        Ui.SetVisible("dndicon", false);
        Ui.SetVisible("DragDrop", false);
        for (int i = 0; i < Army.Slots; i++)
        {
            int slot = i;
            if (Ui.Find($"Creature2_{i + 1:00}/SacrificeBtn") is { } b) b.Clicked += _ => Sacrifice(slot);
        }
        Ui.Find("DialogCaptionView")?.SetText(UiGameRoot.RelatedTextAt(Texts, "T_SACRIFICE_ALTAR_CAPTION") ?? "");
        Ui.Find("DialogDescView")?.SetText(UiGameRoot.RelatedTextAt(Texts, "T_SACRIFICE_ALTAR_DESCRIPTION") ?? "");
    }

    public override void Refresh()
    {
        base.Refresh();
        var hero = Hero;
        var game = Adv.Session.Game;
        if (Ui.Find("Hero2") is { } face) TownUi.SetPicture(face, TownUi.HeroFace(hero, large: false));
        Ui.SetVisible("Hero2/Levelup", false);
        Ui.SetVisible("Hero2/ExpView", false);
        for (int i = 0; i < Army.Slots; i++)
        {
            string cell = $"Creature2_{i + 1:00}";
            var s = hero.Army[i];
            TownUi.SetPicture(Ui.Find($"{cell}/Icon"), s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
            Ui.Find($"{cell}/Icon/CountView")?.SetText(s is { Count: > 0 } ? $"<body><right>{s.Count}" : "");
            Ui.SetVisible($"{cell}/Highlight", false);
            if (Ui.Find($"{cell}/SacrificeBtn") is not { } b) continue;
            b.Visible = s is { Count: > 0 };
            if (s is not { Count: > 0 }) continue;
            b.Enabled = true;
            // TODO(exe): the creature_exp value of the tooltip (FUN_00a48a50 argument) is not traced; the base
            // experience (before the hero's modifiers) is shown.
            string name = Game.Instance.Db.Creatures.TryGetValue(s.Creature, out var cd) ? cd.Name : s.Creature;
            b.SetTooltip((UiGameRoot.RelatedTextAt(Texts, "BUTTON_TOOLTIP") ?? "")
                .Replace("<value=creature_exp>", game.AltarCreatureExperience(hero, s.Creature, s.Count).ToString())
                .Replace("<value=creature_name>", name));
        }
    }

    void Sacrifice(int slot)
    {
        if (Hero.Army[slot] is not { Count: > 0 } s) return;
        Apply(new SacrificeCreatureOnAltarCmd(HeroId, Object, slot, s.Count));
    }
}
