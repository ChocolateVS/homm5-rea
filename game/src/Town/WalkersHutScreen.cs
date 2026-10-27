using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Stronghold Walker's Hut (ID_ARTEFACT_MAGIC_SCREEN = UI/ArtefactMagicScreen; screen 007c0c10, opened by
/// buy_artifacts in a Stronghold (0061a260) or a double click on the hut (0061b760, ability 0x2b) → 00618920 →
/// 007bdfd0). Fill 007bfe40: one UpgradeToLevel%dBtn per talisman level with ARTEFACT_LEVEL%d / ARTEFACT_LEVEL_%d,
/// row status = town quote (level, level), affordable = the summed cost of the owned level + 1 .. level, enabled =
/// status 0 and affordable; Picture = the hero's level texture, Description = DESCRIPTION_FORMAT (hero_name,
/// talisman_level). Selection (007bead0.. / 007be270): button state 1, the spells of levels 1..n in
/// UpgradeLevelInfoTmpl n (007bf2b0), BuyBtn enabled with the row and its tooltip (007bdd90). Buy / double click
/// on an enabled row (007bda50 / 007be400..) → CONFIRM_CAPTION / CONFIRM_TEXT (007bd8e0) → the upgrade command.
/// </summary>
public sealed partial class WalkersHutScreen : TownSubScreen
{
    const string Texts = "UI/ArtefactMagic/Texts.(WindowRelatedTexts).xdb";
    readonly HeroState _hero;
    int _selected = -1;

    WalkersHutScreen(TownScreen owner, HeroState hero) : base(owner, "UI/ArtefactMagicScreen.(WindowScreen).xdb")
    {
        _hero = hero;
        for (int i = 1; i <= 4; i++)
        {
            int level = i;
            Ui.Bus.On($"select_level{level}", () => Select(level - 1));
            Ui.Bus.On($"upgrade_to_level{level}", () => { Select(level - 1); Upgrade(); });
        }
        Ui.Bus.On("upgrade", Upgrade);
    }

    /// <summary>
    /// 00618920: the visiting hero (town hero slot 0) when it is a Barbarian, else the garrison hero (slot 1);
    /// nothing opens unless that hero is a Barbarian. The hut itself is not checked here (the quote reports it).
    /// </summary>
    public static WalkersHutScreen? TryOpen(TownScreen owner)
    {
        if (owner.Town.Type != "TOWN_STRONGHOLD") return null;
        var h = owner.VisitingHero();
        if (h is not { Class: "HERO_CLASS_BARBARIAN" }) h = owner.GarrisonHero();
        return h is { Class: "HERO_CLASS_BARBARIAN" } ? new WalkersHutScreen(owner, h) : null;
    }

    static string T(string key) => UiGameRoot.RelatedTextAt(Texts, key) ?? "";

    H5.Adventure.Engine.Adventure Game => Owner.Session.Game;

    Texture2D? LevelTexture(int level) =>
        Ui.Find("ArtefactMagicMain") is { } main && main.Window.RelatedTextures.TryGetValue($"ARTEFACT_LEVEL_{level}", out var p)
            ? UiResources.Instance.Texture(p) : null;

    /// <summary>Row of level n (007bfe40): single-level status, cumulative affordability, forbidden spells.</summary>
    (TalismanQuote Q, bool Afford, bool Enabled) Row(int level)
    {
        var q = Game.TalismanQuoteFor(Owner.Town, _hero, level, level);
        bool afford = false;
        if (_hero.TalismanLevel < level)
            afford = Me.Purse.CanAfford(Game.TalismanQuoteFor(Owner.Town, _hero, _hero.TalismanLevel + 1, level).Cost);
        return (q, afford, q.Status == TalismanStatus.Ok && afford);
    }

    /// <summary>007bdd90: 4 already obtained, 6 hero level (HeroLevel of the level), forbidden spell, no money, possible.</summary>
    string RowTip(int level)
    {
        var (q, afford, _) = Row(level);
        return q.Status switch
        {
            TalismanStatus.AlreadyUpgraded => T("LEVEL_ALREADY_UPGRADED_TOOLTIP"),
            TalismanStatus.HeroLevelTooLow => T("NOT_ENOUGH_HERO_LEVEL_TOOLTIP").Replace("<value=hero_level>", Game.TalismanLevels[level - 1].HeroLevel.ToString()),
            _ when q.Forbidden.Count > 0 => T("SPELL_FORBIDDEN_TOOLTIP"),
            _ when !afford => T("NOT_ENOUGH_MONEY_TOOLTIP"),
            TalismanStatus.Ok => T("UPGRADE_POSSIBLE_TOOLTIP"),
            _ => "",
        };
    }

    void Select(int row)
    {
        if (row < 0 || row >= Game.TalismanMaxLevel) return;
        _selected = row;
        Refresh();
    }

    void Upgrade()
    {
        if (_selected < 0) return;
        int level = _selected + 1;
        if (!Row(level).Enabled) return;
        var cost = Game.TalismanQuoteFor(Owner.Town, _hero, _hero.TalismanLevel + 1, level).Cost;
        AdvDialogs.YesNo(this, UiResources.StripMarkup(T("CONFIRM_CAPTION")), $"{T("CONFIRM_TEXT")}<br>{MapBuildingScreen.CostText(cost)}", null,
            yes => { if (yes) Apply(new UpgradeTalismanCmd(Owner.Town.Object, _hero.Id, level) { Player = Me.Index }); });
    }

    public override void Refresh()
    {
        base.Refresh();
        if (_hero == null) return;
        int max = Game.TalismanMaxLevel;
        for (int i = 1; i <= 4; i++)
        {
            if (Ui.Find($"UpgradeToLevel{i}Btn") is not { } b) continue;
            b.Visible = i <= max;
            if (i > max) continue;
            var (_, _, enabled) = Row(i);
            b.SetState(i - 1 == _selected ? 1 : 0);
            b.SetTooltip(RowTip(i));
            if (b.FindChild("Choose", true, false) is UiNode choose)
            {
                choose.SetValue("heroskill", T($"ARTEFACT_LEVEL{i}"));
                choose.Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.35f);
            }
            if (b.FindChild("FaceSkill", true, false) is UiNode face)
            {
                TownUi.SetPicture(face, LevelTexture(i));
                face.Modulate = enabled ? Colors.White : new Color(75 / 255f, 75 / 255f, 75 / 255f, 75 / 255f);
            }
        }
        TownUi.SetPicture(Ui.Find("Picture"), LevelTexture(_hero.TalismanLevel));
        Ui.Find("Description")?.SetText(T("DESCRIPTION_FORMAT").Replace("<value=hero_name>", _hero.Name)
            .Replace("<value=talisman_level>", T($"ARTEFACT_LEVEL{_hero.TalismanLevel}")));
        // 007bdab0 (nothing selected): no spell panel, BuyBtn disabled with UPGRADE_NOT_SELECTED_TOOLTIP.
        Ui.SetVisible("SpellsInfoHeader", _selected >= 0);
        for (int n = 1; n <= 4; n++) Ui.SetVisible($"UpgradeLevelInfoTmpl{n}", _selected >= 0 && n == _selected + 1);
        if (Ui.Find("BuyBtn") is { } buy)
        {
            buy.Enabled = _selected >= 0 && Row(_selected + 1).Enabled;
            buy.SetTooltip(_selected >= 0 ? RowTip(_selected + 1) : T("UPGRADE_NOT_SELECTED_TOOLTIP"));
        }
        if (_selected < 0) return;
        var q = Game.TalismanQuoteFor(Owner.Town, _hero, 1, _selected + 1);
        var spells = q.Allowed.Concat(q.Forbidden).ToList();
        for (int s = 0; s < spells.Count && s < 4; s++)
        {
            if (Ui.Find($"UpgradeLevelInfoTmpl{_selected + 1}/Spell{s + 1}") is not { } node) continue;
            var spell = global::H5Reborn.Game.Instance.Db.Spell(spells[s]);
            if (spell == null) continue;
            var icon = (string?)global::H5Reborn.Game.Instance.Xdb.Load(spell.SourcePath).Root?.Element("Texture")?.Attribute("href");
            TownUi.SetPicture(node, string.IsNullOrEmpty(icon) ? null : global::H5Reborn.Game.Instance.LoadTexture(XdbRef.Parse(icon, spell.SourcePath).Path));
            node.TooltipText = UiResources.StripMarkup(spell.Name);
        }
    }
}
