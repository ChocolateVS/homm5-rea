using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Town Info (UI/TownInfo): town picture, name, town level, race, fortifications, daily gold, and the
/// specialization's biography and special ability (TownSpecialization xdb), with the stock text templates.
/// </summary>
public sealed partial class TownInfoScreen : TownSubScreen
{
    const string TownTypesInfo = "GameMechanics/RefTables/TownTypesInfo.xdb";

    public TownInfoScreen(TownScreen owner) : base(owner, "UI/TownInfo.(WindowScreen).xdb") { }

    public override void _UnhandledInput(Godot.InputEvent e)
    {
        // The screen has no buttons: any click or Esc returns to the town.
        if (e is Godot.InputEventMouseButton { Pressed: true } or Godot.InputEventKey { Pressed: true, Keycode: Godot.Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    static string Text(string? href, string file) => Game.Instance.Xdb.ReadText(href, file)?.Trim() ?? "";

    public override void Refresh()
    {
        base.Refresh();
        var t = Owner.Town;
        var info = Game.Instance.Xdb.Load(TownTypesInfo).Root?.Element("objects")?.Elements("Item")
            .FirstOrDefault(i => (string?)i.Element("ID") == t.Type)?.Element("obj");
        string Ref(string el) => Text((string?)info?.Element(el)?.Attribute("href"), TownTypesInfo);
        int fortLevel = t.Level("TB_FORT");
        var fort = fortLevel switch { 1 => Ref("textFort"), 2 => Ref("textCitadel"), >= 3 => Ref("textCaste"), _ => "" };
        if (fort.Length == 0) fort = TownUi.Text("UI/TownInfo/FortificationNone.txt", "None");
        TownUi.SetPicture(Ui.Find("town_pic"), Owner.TownIcon());
        var stats = Ui.Find("town_stats");
        stats?.SetValue("level", t.DevLevel.ToString());
        stats?.SetValue("race", Ref("textType"));
        stats?.SetValue("fort", fort);
        Ui.Find("town_income")?.SetValue("income", Owner.Session.Game.TownIncome(t)[H5.Adventure.State.Purse.Gold].ToString());
        string biography = "", special = "";
        if (!string.IsNullOrEmpty(t.Specialization) && Game.Instance.Fs.Exists(t.Specialization))
        {
            var spec = Game.Instance.Xdb.Load(t.Specialization).Root;
            biography = Text((string?)spec?.Element("BiographyFileRef")?.Attribute("href"), t.Specialization);
            var bonus = Text((string?)spec?.Element("BonusDescriptionFileRef")?.Attribute("href"), t.Specialization);
            if (bonus.Length > 0)
                special = TownUi.Text("UI/TownInfo/specialization.txt", "<br><h3>Special Ability<br><body_bright><left><value=text>").Replace("<value=text>", bonus);
        }
        // Towns without a map-given name go by their specialization's name (TownSpecialization NameFileRef).
        var name = t.Name;
        if (name.Length == 0 && !string.IsNullOrEmpty(t.Specialization) && Game.Instance.Fs.Exists(t.Specialization))
            name = Text((string?)Game.Instance.Xdb.Load(t.Specialization).Root?.Element("NameFileRef")?.Attribute("href"), t.Specialization);
        stats?.SetValue("name", name.Length > 0 ? name : Ref("textType"));
        var desc = Ui.Find("description");
        desc?.SetValue("biography", biography);
        desc?.SetValue("special", special);
    }
}
