using Godot;
using H5Reborn.Combat;
using H5Reborn.Ui;

namespace H5Reborn.Interaction;

/// <summary>
/// The stock creature info screen (UI/CreatureInfo) for a stack outside combat: opened by
/// double-clicking a stack icon in the town screen or hero screen (manual p.10: "You can also call
/// up this screen by double-clicking on the Stack's icon in the main Town screen, Hero screen or on
/// the Initiative bar"). Filled like the combat version (game/src/Combat CombatHud.ShowUnitInfo):
/// the commanding hero's Attack/Defence are added and shown green against the creature's base.
/// </summary>
public static class CreatureInfoWindow
{
    const string InfoTexts = "UI/CreatureInfo/CreatureInfoWindow/Texts/";

    public static UiScreen? Show(Control parent, string creatureId, int count, H5.Adventure.State.HeroState? hero)
    {
        if (!Game.Instance.Db.Creatures.TryGetValue(creatureId, out var def)) return null;
        var info = new UiScreen("UI/CreatureInfo.(WindowScreen).xdb") { MouseFilter = Control.MouseFilterEnum.Stop };
        info.SetVisible("upgrade_tooltip", false);
        info.SetVisible("to_left", false);
        info.SetVisible("to_right", false);
        if (info.Find("creature_info/icon") is { } icon && CombatIcons.Creature(creatureId, true) is { } iconPath)
            SetTex(icon, UiResources.Instance.Texture(iconPath));
        if (info.Find("icon_definition") is { } idef)
        {
            idef.SetValue("name", CombatLogText.Escape(def.Name));
            idef.SetValue("tier", def.Tier.ToString());
            idef.SetValue("town", TownName(def.Town));
        }
        info.Find("quantity")?.SetValue("value", count.ToString());

        int atk = def.Attack + (hero?.Attack ?? 0), dfn = def.Defence + (hero?.Defence ?? 0);
        var rows = new (int Slot, string Value)[]
        {
            (0, Single(atk, def.Attack)),
            (1, Single(dfn, def.Defence)),
            (2, Fmt("double_value", ("value1_normal", def.MinDamage.ToString()), ("value1_red", ""), ("value1_green", ""),
                ("value2_normal", def.MaxDamage.ToString()), ("value2_red", ""), ("value2_green", ""))),
            (3, Single(def.Health, def.Health)),
            (4, Single(def.Speed, def.Speed)),
            (5, Single(def.Initiative, def.Initiative)),
        };
        foreach (var (slot, value) in rows)
        {
            info.Find($"param_{slot}/name")?.SetText(UiResources.Instance.Text(InfoTexts + $"param_{slot}.txt"));
            info.Find($"param_{slot}/value")?.SetText(value);
            info.SetVisible($"param_{slot}/icon", false);
        }
        // Morale/luck depend on the whole army and hero (adventure rules); not shown outside combat yet.
        info.SetVisible("param_6", false);
        info.SetVisible("param_7", false);
        if (def.Shots > 0)
        {
            info.Find("param_8/name")?.SetText(UiResources.Instance.Text(InfoTexts + "Range.txt"));
            info.Find("param_8/value")?.SetText(Single(def.Shots, def.Shots));
            info.SetVisible("param_8/icon", false);
        }
        else info.SetVisible("param_8", false);
        if (info.Find("description_container") is { } cont)
        {
            info.SetVisible("description_text", false);
            var abilities = def.Abilities.Select(a => Game.Instance.Db.Abilities.GetValueOrDefault(a)?.Name).Where(x => !string.IsNullOrEmpty(x));
            var markup = (UiResources.Instance.Text("UI/CreatureInfo/CreatureInfoWindow/description_text.txt") ?? "")
                .Replace("<value=description>", CombatLogText.Escape(def.Description))
                .Replace("<value=abilities>", CombatLogText.Escape(string.Join((UiResources.Instance.Text(InfoTexts + "separator.txt") ?? ",") + " ", abilities)));
            cont.ClipContents = true;
            cont.AddChild(new BitmapText { Markup = markup, AnchorRight = 1, AnchorBottom = 1, OffsetRight = -24 });
        }
        // Any click or Esc closes the stock info screen.
        info.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true }) info.QueueFree(); };
        info.TreeEntered += () => info.GrabFocus();
        info.FocusMode = Control.FocusModeEnum.All;
        info.GuiInput += ev => { if (ev is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Enter or Key.KpEnter }) { info.AcceptEvent(); info.QueueFree(); } };
        parent.AddChild(info);
        return info;
    }

    static string Single(int v, int baseV) => Fmt("single_value",
        ("value_normal", v == baseV ? v.ToString() : ""), ("value_red", v < baseV ? v.ToString() : ""), ("value_green", v > baseV ? v.ToString() : ""));

    static string Fmt(string template, params (string K, string V)[] values)
    {
        var t = UiResources.Instance.Text(InfoTexts + template + ".txt") ?? "";
        foreach (var (k, v) in values) t = t.Replace($"<value={k}>", v);
        return t;
    }

    static void SetTex(Control node, Texture2D? tex)
    {
        if (tex == null) return;
        foreach (var c in node.GetChildren()) if (c.Name == "_pic") c.QueueFree();
        node.AddChild(new TextureRect
        {
            Name = "_pic", Texture = tex, AnchorRight = 1, AnchorBottom = 1,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
    }

    static string TownName(string town) => town switch
    {
        "TOWN_HEAVEN" => "Haven", "TOWN_PRESERVE" => "Sylvan", "TOWN_ACADEMY" => "Academy", "TOWN_FORTRESS" => "Fortress",
        "TOWN_DUNGEON" => "Dungeon", "TOWN_INFERNO" => "Inferno", "TOWN_NECROMANCY" => "Necropolis", "TOWN_STRONGHOLD" => "Stronghold",
        _ => "Neutral",
    };
}
