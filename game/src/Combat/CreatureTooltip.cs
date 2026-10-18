using Godot;
using H5.Combat;
using H5.Core.Ui;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The small creature tooltip shown on a right-clicked stack (exe 00575430 → 00573dd0): the
/// TOOLTIPS_HOLDER window holding the creature info gathering (UI/Tooltips/CreatureInfo/CommonInfo,
/// filled by exe 00572be0) or, on every third right-click, the active-effects gathering
/// (CreatureEffects, exe 00574790; fan manual p.304), followed by the current hover prediction.
/// Gathering windows stack their visible children top to bottom over a tiled background.
/// </summary>
public partial class CreatureTooltip : Control
{
    const string Dir = "UI/Tooltips/CreatureInfo/";
    const float Width = 250f;

    readonly UiScreen _screen;
    readonly UiWindow _root;

    CreatureTooltip(string xdb)
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 60;
        _screen = new UiScreen(xdb) { AnchorRight = 0, AnchorBottom = 0, Size = new Vector2(Width, 600) };
        _root = _screen.Root;
        AddChild(_screen);
    }

    // Related texts keep their spaces (separators are " /" and " -"); only line ends are dropped.
    static string Text(UiWindow w, string name) =>
        w.RelatedTexts.TryGetValue(name, out var p) ? (UiResources.Instance.Text(p) ?? "").TrimEnd('\r', '\n') : "";

    /// <summary>Builds the tooltip for <paramref name="variant"/> = right-click count mod 3.</summary>
    public static CreatureTooltip Build(Battle b, Unit u, int variant, string? predictionMarkup)
    {
        var t = new CreatureTooltip(Dir + (variant == 2 ? "CreatureEffects.(WindowGathering).xdb" : "CommonInfo.(WindowGathering).xdb"));
        t.Fill(b, u, variant == 2);
        t.Prediction = predictionMarkup;
        return t;
    }

    string? Prediction;

    void Fill(Battle b, Unit u, bool effects)
    {
        var def = Game.Instance.Db.Creatures.GetValueOrDefault(u.CreatureId);
        string name = CombatLogText.Escape(def?.Name is { Length: > 0 } n ? n : u.Name);
        // creature_name.txt: "<tooltip_title><center><value=name>"; the count is part of the name line.
        var nameTpl = (UiResources.Instance.Text(Dir + "creature_name.txt") ?? "<value=name>").Trim();
        foreach (var nv in _screen.FindAll("NameTextView")) nv.SetText(nameTpl.Replace("<value=name>", $"{name} ({u.Count})"));
        if (effects) { FillEffects(u); return; }

        var (minD, maxD) = b.DamageRangeOf(u);
        int mana = u.Mana, maxMana = u.MaxMana;
        // Rows in exe order: value(s) shown coloured against the base, -1 = absent ("-").
        Row("Attack", u.BaseAttack, b.AttackOf(u));
        Row("Defense", u.BaseDefence, b.DefenceOf(u));
        Row("Damage", u.BaseMinDamage, (int)Math.Round(minD), "Separator2", u.BaseMaxDamage, (int)Math.Round(maxD));
        Row("Hitpoints", u.MaxHealthCache, u.TopHealth, "Separator1", u.MaxHealthCache, u.MaxHealthCache);
        Row("Speed", u.BaseSpeed, b.SpeedOf(u));
        Row("Initiative", (int)Math.Round((double)u.BaseInitiative), (int)Math.Round(b.InitiativeOf(u)));
        if (u.MaxShots > 0) Row("ShootsLeft", u.Shots, u.Shots); else Row("ShootsLeft", -1, -1);
        if (maxMana > 0) Row("Mana", mana, mana, "Separator1", maxMana, maxMana); else Row("Mana", -1, -1);
        Row("Morale", 0, b.MoraleOf(u));
        Row("Luck", 0, b.LuckOf(u));
        Hide("Range");
        Hide("RagePointsInfo"); Hide("RagePointsInfoTextView");
        Hide("SkillChainInfo");
        Hide("MiniArtifactInfo");
        Hide("status");
        var abilities = u.Abilities.Select(a => Game.Instance.Db.Abilities.GetValueOrDefault(a)?.Name).Where(x => !string.IsNullOrEmpty(x)).ToList();
        if (abilities.Count > 0 && _screen.Find("Abilities") is { } ab)
        {
            string sep = Text(_root, "Separator3") is { Length: > 0 } s3 ? s3 : ",";
            ab.SetText((UiResources.Instance.Text(Dir + "abilities.txt") ?? "<value=abilities>").Trim()
                .Replace("<value=abilities>", CombatLogText.Escape(string.Join(sep + " ", abilities))));
        }
        else Hide("Abilities");
    }

    /// <summary>Exe 00572880: AttrName + AttrValue (current coloured against base [, separator, second value]).</summary>
    void Row(string row, int base1, int cur1, string? separator = null, int base2 = -1, int cur2 = -1)
    {
        if (_screen.Find(row) is not { } r) return;
        string nameTpl = (UiResources.Instance.Text(Dir + "attributename.txt") ?? "<value=attr_name>").Trim();
        string valueTpl = (UiResources.Instance.Text(Dir + "attributevalue.txt") ?? "").Trim();
        r.Screen?.Find($"{row}/AttrName")?.SetText(nameTpl.Replace("<value=attr_name>", Text(_root, row)));
        string Color(int b, int c) => c > b ? Text(_root, "RaisedAttributeColor") : c < b ? Text(_root, "LowedAttributeColor") : "";
        string value;
        if (base1 == -1 && cur1 == -1)
            value = valueTpl.Replace("<value=val_min_color>", "").Replace("<value=val_min>", Text(_root, "AbsentAttribute"))
                            .Replace("<value=separator>", "").Replace("<value=val_max_color>", "").Replace("<value=val_max>", "");
        else
            value = valueTpl.Replace("<value=val_min_color>", Color(base1, cur1)).Replace("<value=val_min>", cur1.ToString())
                            .Replace("<value=separator>", cur2 == -1 || separator == null ? "" : Text(_root, separator))
                            .Replace("<value=val_max_color>", cur2 == -1 ? "" : Color(base2, cur2))
                            .Replace("<value=val_max>", cur2 == -1 ? "" : cur2.ToString());
        r.Screen?.Find($"{row}/AttrValue")?.SetText(value);
    }

    void FillEffects(Unit u)
    {
        var list = u.Effects.ToList();
        if (list.Count == 0) { Hide("Effects0"); return; }
        Hide("NoActiveEffects");
        // Effect rows (EffectText with effect_text.txt): name in friendly/hostile colour and the
        // remaining duration ("Turns: x/y" in initiative-10 turns, "Permanent" otherwise).
        string tpl = (UiResources.Instance.Text(Dir + "effect_text.txt") ?? "<value=name><br><value=duration>").Trim();
        string turnsTpl = (UiResources.Instance.Text(Dir + "turns-duration.txt") ?? "Turns: <value=val>/<value=maxval>").Trim();
        string perm = (UiResources.Instance.Text(Dir + "effect_permanent_text.txt") ?? "Permanent").Trim();
        var lines = list.Select(e =>
        {
            string nm = Game.Instance.Db.Spells.GetValueOrDefault(e.Id)?.Name is { Length: > 0 } sn ? sn : e.Id;
            string dur = e.Remaining > 0 && e.Remaining < 1e6 ? turnsTpl.Replace("<value=val>", Math.Ceiling(e.Remaining).ToString()).Replace("<value=maxval>", Math.Ceiling(e.Duration > 0 && e.Duration < 1e6 ? Math.Max(e.Duration, e.Remaining) : e.Remaining).ToString()) : perm;
            string color = e.Positive ? Text(_root, "FriendlyEffectColor") : Text(_root, "NonFriendlyEffectColor");
            return tpl.Replace("<value=color>", color).Replace("<value=name>", CombatLogText.Escape(nm))
                      .Replace("<value=duration>", dur).Replace("<value=addition>", "");
        });
        if (_screen.Find("EffectText") is { } et) et.SetText(string.Join("<br>", lines));
    }

    /// <summary>Hides a window for both drawing and the gathering layout.</summary>
    void Hide(string name)
    {
        foreach (var n in _screen.FindAll(name)) { n.Visible = false; n.Window.Visible = false; }
    }

    /// <summary>Lays the gathering out (UiLayout stacks visible rows) and sizes the tooltip to it.</summary>
    public override void _Ready()
    {
        _screen.Relayout();
        float s = _screen.UiScale;
        float h = _root.Rect.H;
        if (Prediction != null && _screen.NodeOf(_root) is { } rootNode)
        {
            rootNode.AddChild(new BitmapText { Markup = Prediction, Wrap = true, Position = new Vector2(8, h + 2), Size = new Vector2(Width - 16, 34) });
            h += 38;
        }
        _screen.Size = new Vector2(Width, h) * s;
        Size = _screen.Size;
    }
}
