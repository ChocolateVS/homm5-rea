using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Hero;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Drives UI/BuyArtefact for an artifact merchant (town Artifact Merchant or the map's merchant wagon): the six
/// artifacts with their prices, the selected one's cost in the CreatureCountHolder strip, Buy and Close.
/// </summary>
public sealed class ArtifactMerchantController
{
    readonly UiScreen _ui;
    readonly AdventureSession _session;
    readonly Func<PlayerState> _me;
    readonly Func<HeroState?> _hero;
    readonly int _market;
    readonly Action _changed;
    readonly Action<string> _notice;
    int _choice = -1;

    public ArtifactMerchantController(UiScreen ui, AdventureSession session, Func<PlayerState> me, Func<HeroState?> hero, int market,
        Action changed, Action<string> notice)
    {
        _ui = ui;
        _session = session;
        _me = me;
        _hero = hero;
        _market = market;
        _changed = changed;
        _notice = notice;
        ui.Bus.On("artifact_select", (_, src) => { if (src?.Name.ToString() is { } n && n.StartsWith("ArtifactButton_")) { _choice = n[^1] - '0'; Refresh(); } });
        ui.Bus.On("buy_artifact", Buy);
        var stock = session.Game.MerchantStock(market);
        _choice = Enumerable.Range(0, stock.Count).FirstOrDefault(i => stock[i] != null, -1);
    }

    void Buy()
    {
        if (_hero() is not { } h) { _notice("A hero must be present to buy an artifact."); return; }
        if (_choice < 0) return;
        var r = _session.Apply(new BuyMerchantArtifactCmd(h.Id, _market, _choice) { Player = _me().Index });
        if (!r.Ok) _notice(char.ToUpperInvariant((r.Error ?? "Not possible")[0]) + (r.Error ?? "Not possible")[1..] + ".");
        else
        {
            Audio.AudioSystem.PlayNamed("BUY_ARTIFACT_SOUND");
            var stock = _session.Game.MerchantStock(_market);
            _choice = Enumerable.Range(0, stock.Count).FirstOrDefault(i => stock[i] != null, -1);
        }
        Refresh();
        _changed();
    }

    public void Refresh()
    {
        var game = _session.Game;
        var stock = game.MerchantStock(_market);
        var me = _me();
        for (int i = 0; i < H5.Adventure.Engine.Adventure.MerchantSlots; i++)
        {
            var art = i < stock.Count ? stock[i] : null;
            TownUi.SetPicture(_ui.Find($"ArtifactButton_{i}/Icon"), art != null ? HeroScreen.ArtifactIcon(art) : null);
            _ui.Find($"ArtifactPrice_{i}")?.SetText(art != null ? $"<center><value_value>{game.MerchantPrice(art)}" : "");
            if (_ui.Find($"ArtifactButton_{i}") is { } b)
            {
                b.Enabled = art != null;
                b.SetTooltip(art != null && Game.Instance.Db.Artifacts.TryGetValue(art, out var def)
                    ? $"<h3>{HeroScreen.ArtifactName(art)}<br><body>{def.Description}" : null);
                b.SetState(i == _choice && b.Window.ButtonStates.Count > 1 ? 1 : 0);
            }
        }
        var chosen = _choice >= 0 && _choice < stock.Count ? stock[_choice] : null;
        var cost = new int[7];
        if (chosen != null) cost[Purse.Gold] = game.MerchantPrice(chosen);
        TownUi.SetCostStrip(_ui.Find("CreatureCountHolder"), chosen != null ? cost : null, me);
        if (_ui.Find("BuyButton") is { } buy) buy.Enabled = chosen != null && me.Purse[Purse.Gold] >= cost[Purse.Gold];
    }
}

/// <summary>A town's Artifact Merchant (Academy TB_SPECIAL_3, Dungeon Trade Guild) in the town screen (`buy_artifacts`).</summary>
public sealed partial class ArtifactMerchantScreen : TownSubScreen
{
    readonly ArtifactMerchantController _merchant;

    public ArtifactMerchantScreen(TownScreen owner) : base(owner, "UI/BuyArtefact.(WindowScreen).xdb")
    {
        _merchant = new ArtifactMerchantController(Ui, owner.Session, () => Me, () => owner.VisitingHero() ?? owner.GarrisonHero(),
            owner.Town.Object, owner.Refresh, text => owner.Notice(owner.Town.Name, text));
    }

    public override void Refresh()
    {
        base.Refresh();
        _merchant?.Refresh();
    }
}
