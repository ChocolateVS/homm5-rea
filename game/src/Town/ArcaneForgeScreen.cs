using System.Xml.Linq;
using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.Xdb;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Academy Arcane Forge (UI/AcademyCreatureArtifacts, N_ACADEMY_CREATURE_ARTIFACTS_SCREEN_ID; enter_special → FUN_00618c10 →
/// FUN_00640850 with the town's hero). Three tabs (FUN_00640890: tab 0 create, 1 equip, 2 dismantle, tooltips
/// create_tooltip / dismantle_tooltip):
/// <list type="bullet">
/// <item>create: three effect selectors properties/ud1..ud3 (Up / Down step through the effects, skipping those
/// FUN_00c0a700 refuses — FUN_0063c0b0; the first selector never offers "none"), the artifact preview with its name
/// (amulet_name, FUN_0063c560) and price (FUN_0063c180, red_cost_format when not affordable — FUN_0063c230); the
/// artifact is made with <see cref="CreateMicroArtifactCmd"/> (FUN_0063c440);</item>
/// <item>equip: the hero's army (Slot_1..7) and the bag; an artifact picked from the bag goes onto an Academy stack
/// (<see cref="EquipMicroArtifactCmd"/>; replacing asks artifact-replace-confirmation); a stack's artifact goes back with
/// <see cref="UnequipMicroArtifactCmd"/>;</item>
/// <item>dismantle: the bag; the picked artifact's effects (artifact_definition), name and refund, dismantled with
/// <see cref="DismantleMicroArtifactCmd"/> (FUN_00644600).</item>
/// </list>
/// The bag grid shows five entries per row from a scroll offset (inv_left / inv_right, FUN_006447d0 / FUN_00644820).
/// TODO(exe): which control fires the create / dismantle action (here the preview's button and the definition panel),
/// drag-and-drop, and the preview's Up / Down are not decoded.
/// </summary>
public sealed partial class ArcaneForgeScreen : TownSubScreen
{
    const string Dir = "UI/AcademyCreatureArtifacts/";
    const string EffectsTable = "GameMechanics/RefTables/MicroArtifactEffects.xdb";
    const string ShellsTable = "GameMechanics/RefTables/MicroArtifactShells.xdb";
    const string PrefixesTable = "GameMechanics/RefTables/MicroArtifactPrefixes.xdb";
    static readonly string[] Tabs = { "create", "equip", "dismantle" };
    static readonly string[] EffectText =
        { "", "wounding", "piercing", "armor_crushing", "health", "defence", "magic_protection", "luck", "morale", "speed", "haste" };

    readonly HeroState _hero;
    int _tab;
    readonly MicroEffect[] _choice = { MicroEffect.None, MicroEffect.None, MicroEffect.None };
    int _offset;
    (int Row, int Index)? _picked;

    public ArcaneForgeScreen(TownScreen owner, HeroState hero) : base(owner, "UI/AcademyCreatureArtifacts.(WindowScreen).xdb")
    {
        _hero = hero;
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            Ui.Find($"tabs/tab_{i}")!.Clicked += _ => { _tab = k; _picked = null; Refresh(); };
        }
        for (int i = 0; i < 3; i++)
        {
            int slot = i;
            if (Ui.Find($"create/properties/ud{i + 1}/Up") is { } up) up.Clicked += _ => Step(slot, +1);
            if (Ui.Find($"create/properties/ud{i + 1}/Down") is { } dn) dn.Clicked += _ => Step(slot, -1);
        }
        foreach (var b in new[] { "Up", "Down" }) Ui.SetVisible($"create/artifact_view/{b}", false);
        if (Ui.Find("create/artifact_view/self/button") is { } make) make.Clicked += _ => Create();
        foreach (var panel in Tabs)
        {
            if (Ui.Find($"{panel}/inv_left") is { } l) l.Clicked += _ => { if (_offset > 0) _offset--; Refresh(); };
            if (Ui.Find($"{panel}/inv_right") is { } r) r.Clicked += _ => { if (_offset + 5 < BagWidth()) _offset++; Refresh(); };
            for (int line = 0; line < 2; line++)
                for (int c = 0; c < 5; c++)
                {
                    int row = line, col = c;
                    if (Ui.Find($"{panel}/artifacts/line{line + 1}/icon{c + 1}/button") is { } bn) bn.Clicked += _ => PickBag(row, _offset + col);
                }
        }
        for (int s = 0; s < 7; s++)
        {
            int slot = s;
            if (Ui.Find($"equip/Slot_{s + 1}") is { } bn) bn.Clicked += _ => SlotClicked(slot);
        }
        for (int k = 1; k <= 3; k++)
            if (Ui.Find($"dismantle/artifact_definition/icon{k}/button") is { } def) def.Clicked += _ => Dismantle();
        foreach (var m in new[] { "inv_left", "inv_right", "up", "down" }) Ui.Bus.On(m, () => { });
        _choice[0] = FirstValid(0);
    }

    /// <summary>The screen needs the Arcane Forge and a hero in the town (FUN_00618c10 passes the town's hero).</summary>
    public static HeroState? HeroFor(TownScreen owner) =>
        owner.Town.Type == "TOWN_ACADEMY" && owner.Town.Level(H5.Adventure.Engine.Adventure.ArcaneForgeBuilding) > 0
            ? owner.VisitingHero() ?? owner.GarrisonHero() : null;

    H5.Adventure.Engine.Adventure Adv => Owner.Session.Game;

    // ------------------------------------------------------------------ create

    MicroEffect FirstValid(int slot)
    {
        for (int e = 1; e <= 10; e++)
        {
            var trial = (MicroEffect[])_choice.Clone();
            trial[slot] = (MicroEffect)e;
            if (H5.Adventure.Engine.Adventure.BuildMicroArtifact(_hero, trial) != null) return (MicroEffect)e;
        }
        return MicroEffect.None;
    }

    /// <summary>FUN_0063c0b0: (value ± 1) mod 11, again while the choice is refused; "none" only below the first selector.</summary>
    void Step(int slot, int dir)
    {
        int start = (int)_choice[slot];
        for (int k = 1; k <= 11; k++)
        {
            int v = ((start + dir * k) % 11 + 11) % 11;
            if (v == 0 && slot == 0) continue;
            var trial = (MicroEffect[])_choice.Clone();
            trial[slot] = (MicroEffect)v;
            if (v == 0 || H5.Adventure.Engine.Adventure.BuildMicroArtifact(_hero, trial) != null)
            {
                _choice[slot] = (MicroEffect)v;
                break;
            }
        }
        Refresh();
    }

    MicroArtifact? Preview() => H5.Adventure.Engine.Adventure.BuildMicroArtifact(_hero, _choice);

    void Create()
    {
        if (Preview() is not { Effects.Count: > 0 } a || !Me.Purse.CanAfford(Adv.MicroArtifactCost(a))) return;   // FUN_0063c230
        Apply(new CreateMicroArtifactCmd(_hero.Id, _choice.ToList()) { Player = Me.Index });
    }

    // ------------------------------------------------------------------ bag, equip, dismantle

    int BagWidth() => _hero.MicroBag.Count == 0 ? 0 : _hero.MicroBag.Max(r => r.Count);

    MicroArtifact? BagAt(int row, int index) =>
        row < _hero.MicroBag.Count && index >= 0 && index < _hero.MicroBag[row].Count ? _hero.MicroBag[row][index] : null;

    void PickBag(int row, int index)
    {
        if (BagAt(row, index) == null) return;
        _picked = _picked == (row, index) ? null : (row, index);
        Refresh();
    }

    void SlotClicked(int slot)
    {
        if (slot >= _hero.Army.Length || _hero.Army[slot] is not { Count: > 0 } st) return;
        if (_picked is (int row, int index) && BagAt(row, index) != null)
        {
            void Equip() { _picked = null; Apply(new EquipMicroArtifactCmd(_hero.Id, slot, row, index) { Player = Me.Index }); }
            if (st.Artifact is { Effects.Count: > 0 } old)
            {
                string q = TownUi.Text(Dir + "artifact-replace-confirmation.txt")
                    .Replace("<value=artifactold>", Name(old)).Replace("<value=artifactnew>", Name(BagAt(row, index)!));
                AdvDialogs.YesNo(this, TownUi.Text(Dir + "CreateArtifacts-caption.(Text).txt", Owner.Town.Name), q, null, yes => { if (yes) Equip(); });
            }
            else Equip();
            return;
        }
        if (st.Artifact is { Effects.Count: > 0 })
            Apply(new UnequipMicroArtifactCmd(_hero.Id, slot, -1, -1) { Player = Me.Index });
    }

    void Dismantle()
    {
        if (_picked is not (int row, int index) || BagAt(row, index) == null) return;
        _picked = null;
        Apply(new DismantleMicroArtifactCmd(_hero.Id, row, index) { Player = Me.Index });
    }

    // ------------------------------------------------------------------ display

    public override void Refresh()
    {
        base.Refresh();
        for (int i = 0; i < 3; i++)
        {
            Ui.SetVisible(Tabs[i], i == _tab);
            Ui.Find($"tabs/tab_{i}")?.SetState(i == _tab ? 2 : 0);
        }
        Ui.Find("tabs/tab_0")?.SetTooltip(TownUi.Text(Dir + "Tabs/tooltip_create.txt"));
        Ui.Find("tabs/tab_1")?.SetTooltip(TownUi.Text(Dir + "Tabs/tooltip_Equip.txt"));
        Ui.Find("tabs/tab_2")?.SetTooltip(TownUi.Text(Dir + "Tabs/tooltip_dismantle.txt"));
        var panel = Tabs[_tab];
        ShowBag(panel);
        if (_tab == 0)
        {
            for (int i = 0; i < 3; i++)
            {
                string ud = $"create/properties/ud{i + 1}";
                var e = _choice[i];
                ShowIcon($"{ud}/self", e == MicroEffect.None ? null : EffectIcon(e), e == MicroEffect.None ? null : EffectTooltip(e, Level(e)));
            }
            var a = Preview();
            ShowArtifact("create", a is { Effects.Count: > 0 } ? a : null);
            ShowIcon("create/artifact_view/self", a is { Effects.Count: > 0 } ? ShellIcon(a) : null, a is { Effects.Count: > 0 } ? Tooltip(a) : null);
        }
        else if (_tab == 1)
        {
            for (int s = 0; s < 7; s++)
            {
                var st = s < _hero.Army.Length ? _hero.Army[s] : null;
                string slot = $"equip/Slot_{s + 1}";
                TownUi.SetPicture(Ui.Find($"{slot}/Pic"), st is { Count: > 0 } ? TownUi.CreatureIcon(st.Creature) : null);
                Ui.Find($"{slot}/Pic/Text")?.SetValue("NumberOfCreatures", st is { Count: > 0 } ? st.Count.ToString() : "");
                Ui.Find(slot)?.SetTooltip(st is { Artifact.Effects.Count: > 0 } ? Tooltip(st.Artifact!) : TownUi.Text(Dir + "TTChooseCreatureToGiveArtifact.txt"));
            }
        }
        else
        {
            var a = _picked is (int r, int i) ? BagAt(r, i) : null;
            ShowArtifact("dismantle", a);
            for (int k = 0; k < 3; k++)
            {
                var e = a != null && k < a.Effects.Count ? a.Effects[k] : null;
                ShowIcon($"dismantle/artifact_definition/icon{k + 1}", e == null ? null : EffectIcon(e.Effect), e == null ? null : EffectTooltip(e.Effect, e.Level));
            }
        }
    }

    int Level(MicroEffect e) => H5.Adventure.Engine.Adventure.MicroEffectLevel(e, H5.Adventure.Engine.Adventure.ForgeKnowledge(_hero));

    /// <summary>Name and price of the artifact (create: its cost, red when not affordable; dismantle: the refund).</summary>
    void ShowArtifact(string panel, MicroArtifact? a)
    {
        Ui.Find($"{panel}/amulet_name")?.SetValue("amuletname", a == null ? "" : Name(a));
        var cost = a == null ? null : new Purse(Adv.MicroArtifactCost(a)).R;
        for (int i = 0; i < TownUi.CostKeys.Length; i++)
        {
            if (Ui.Find($"{panel}/price/{TownUi.CostKeys[i]}") is not { } item) continue;
            int need = cost?[i] ?? 0;
            item.Visible = need > 0;
            item.Window.Visible = need > 0;   // the gathering stacks only the shown resources (UiLayout)
            string col = panel == "create" && Me.Purse[i] < need ? TownUi.Text(Dir + "red-cost-format.txt") : "";
            if (item.FindChild("value", false, false) is UiNode v) { v.SetValue("col", col); v.SetValue("value", need.ToString()); }
        }
        Ui.QueueRelayout();   // the price is a WindowGathering: it stacks the shown resources
    }

    void ShowBag(string panel)
    {
        int width = BagWidth();
        if (_offset + 5 > width) _offset = Math.Max(0, width - 5);
        for (int line = 0; line < 2; line++)
            for (int c = 0; c < 5; c++)
            {
                var a = BagAt(line, _offset + c);
                string cell = $"{panel}/artifacts/line{line + 1}/icon{c + 1}";
                ShowIcon(cell, a == null ? null : ShellIcon(a), a == null ? null : Tooltip(a));
                if (Ui.Find($"{cell}/button") is { } b) b.SetState(_picked == (line, _offset + c) ? 2 : 0);
            }
        if (Ui.Find($"{panel}/inv_left") is { } l) l.Enabled = _offset > 0;
        if (Ui.Find($"{panel}/inv_right") is { } r) r.Enabled = _offset + 5 < width;
    }

    void ShowIcon(string path, Texture2D? tex, string? tooltip)
    {
        TownUi.SetPicture(Ui.Find($"{path}/icon"), tex);
        Ui.SetVisible($"{path}/icon", tex != null);
        Ui.Find($"{path}/button")?.SetTooltip(tooltip);
    }

    // ------------------------------------------------------------------ data

    static XElement? Record(string table, int index, string kind) =>
        Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item").ElementAtOrDefault(index)?.Element("Obj")?.Element(kind);

    static string Href(string table, XElement? rec, string field) =>
        (string?)rec?.Element(field)?.Attribute("href") is { Length: > 0 } h ? XdbRef.Parse(h, table).Path : "";

    static Texture2D? EffectIcon(MicroEffect e) =>
        Href(EffectsTable, Record(EffectsTable, (int)e, "MicroArtifactEffect"), "Icon") is { Length: > 0 } p ? Game.Instance.LoadTexture(p) : null;

    /// <summary>The shell picture: shell n for n effects (FUN_00c0ae80), its Icon1.</summary>
    static Texture2D? ShellIcon(MicroArtifact a) =>
        Href(ShellsTable, Record(ShellsTable, a.Effects.Count, "MicroArtifactShell"), "Icon1") is { Length: > 0 } p ? Game.Instance.LoadTexture(p) : null;

    static string EffectName(MicroEffect e, string field) => TownUi.Text(Href(EffectsTable, Record(EffectsTable, (int)e, "MicroArtifactEffect"), field));

    static string EffectLine(MicroEffect e, int level) =>
        (int)e > 0 && (int)e < EffectText.Length ? TownUi.Text(Dir + EffectText[(int)e] + ".txt").Replace("<value=val>", level.ToString()) : "";

    static string EffectTooltip(MicroEffect e, int level) =>
        TownUi.Text(Dir + "effect_tooltip.txt").Replace("<value=name>", EffectName(e, "Name")).Replace("<value=effect>", EffectLine(e, level));

    /// <summary>tooltip_one / two / three_effects: the name, then one line per effect.</summary>
    static string Tooltip(MicroArtifact a)
    {
        string[] files = { "tooltip_one_effect.txt", "tooltip_two_effects.txt", "tooltip_three_effects.txt" };
        int n = Math.Clamp(a.Effects.Count, 1, 3);
        string t = TownUi.Text(Dir + files[n - 1]).Replace("<value=name>", Name(a));
        for (int i = 0; i < n; i++)
            t = t.Replace($"<value=effect_{i + 1}>", i < a.Effects.Count ? EffectLine(a.Effects[i].Effect, a.Effects[i].Level) : "");
        return t;
    }

    /// <summary>FUN_0063c560: MicroArtifactName1..3 with shell_name, prefix and effect_of_name.</summary>
    static string Name(MicroArtifact a)
    {
        var (shell, prefix, of) = H5.Adventure.Engine.Adventure.MicroArtifactNameParts(a);
        if (shell is < 1 or > 3) return "";
        string fmt = TownUi.Text($"Text/Game/MicroArtifacts/prediction_formats/MicroArtifactName{shell}.txt");
        string shellName = TownUi.Text(Href(ShellsTable, Record(ShellsTable, shell, "MicroArtifactShell"), "Name"));
        string pre = "";
        if (prefix >= 0)
        {
            var items = Record(PrefixesTable, 0, "MicroArtifactPrefixes")?.Element("Prefixes")?.Elements("Item").ToList();
            if (items != null && prefix < items.Count && (string?)items[prefix].Attribute("href") is { Length: > 0 } h)
                pre = TownUi.Text(XdbRef.Parse(h, PrefixesTable).Path);
        }
        string ofName = of == MicroEffect.None ? "" : EffectName(of, "OfName");
        return fmt.Replace("<value=shell_name>", shellName).Replace("<value=prefix>", pre).Replace("<value=effect_of_name>", ofName).Trim();
    }
}
