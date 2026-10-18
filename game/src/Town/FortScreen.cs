using Godot;
using H5.Adventure.Engine;
using H5Reborn.Arena;
using H5Reborn.Render;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Recruiting (UI/FortScreen over the 3D hall Arenas/Interface/NewFort_screen): each built dwelling's
/// creature stands on a placement point (ArenaLocObject ph1_N), with its "available/+growth" plate
/// (definition_N) projected above it. Clicking a creature selects it (clicking again cycles base /
/// upgrade / alternative upgrade when built); the info panel shows its stats and price; the jog dial
/// sets the number (plus/minus/edit) and recruits (Pay, BuyAll) into the visiting hero or the garrison.
/// </summary>
public sealed partial class FortScreen : TownSubScreen
{
    const string Hall = "Arenas/Interface/NewFort_screen/NewFort_screen.(ArenaDesc).xdb";

    SubViewport _vp = null!;
    TownScene3D _scene = null!;
    readonly Dictionary<int, (Node3D Node, string Creature)> _shown = new();
    int _tier = 1, _variant, _count;

    public FortScreen(TownScreen owner) : base(owner, "UI/FortScreen.(WindowScreen).xdb")
    {
        var vpc = new SubViewportContainer { Name = "Hall3D", AnchorRight = 1, AnchorBottom = 1, Stretch = true, MouseFilter = MouseFilterEnum.Stop };
        _vp = new SubViewport { OwnWorld3D = true, HandleInputLocally = false };
        vpc.AddChild(_vp);
        AddChild(vpc);
        MoveChild(vpc, 0);
        MouseFilter = MouseFilterEnum.Stop;
        _scene = new TownScene3D(Hall);
        _vp.AddChild(_scene);
        vpc.GuiInput += OnHallInput;

        Ui.Bus.On("plus_down", () => SetCount(_count + 1));
        Ui.Bus.On("minus", () => SetCount(_count - 1));
        Ui.Bus.On("Pay", () => Recruit(_count));
        Ui.Bus.On("BuyAll", () =>
        {
            int n = MaxAffordable();
            if (n <= 0 || Selected is not { } c || !Game.Instance.Db.Creatures.TryGetValue(c, out var def)) return;
            var cost = string.Join(", ", new[] { def.Cost.Wood, def.Cost.Ore, def.Cost.Mercury, def.Cost.Crystal, def.Cost.Sulfur, def.Cost.Gem, def.Cost.Gold }
                .Select((u, i) => (u * n, TownUi.ResourceNames[i])).Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"));
            var text = TownUi.Text("UI/FortScreen/buy_all_text_format.txt", "Hire all available creatures?<br><br>Cost: <value=cost>").Replace("<value=cost>", cost);
            Adventure.AdvDialogs.YesNo(this, "", text, null, yes => { if (yes) Recruit(n); });
        });
        if (Ui.Find("JogDial/edit") is UiEditLine edit) edit.Changed += v => { if (int.TryParse(v, out var n)) SetCount(n, updateEdit: false); };
        _tier = Enumerable.Range(1, 7).FirstOrDefault(t => owner.Session.Game.DwellingCreature(owner.Town, t, 0) != null, 1);
    }

    public override void _Ready()
    {
        Callable.From(PlaceCreatures).CallDeferred();
        base._Ready();
    }

    /// <summary>The creature shown for a tier: the chosen variant if built, else the best built one.</summary>
    string? CreatureFor(int tier, int variant)
    {
        var g = Owner.Session.Game;
        return g.DwellingCreature(Owner.Town, tier, variant)
               ?? g.DwellingCreature(Owner.Town, tier, 1)
               ?? g.DwellingCreature(Owner.Town, tier, 0);
    }

    void PlaceCreatures()
    {
        foreach (var (n, _) in _shown.Values) n.QueueFree();
        _shown.Clear();
        for (int tier = 1; tier <= 7; tier++)
        {
            // TODO(exe): which of the ph1_1..8 / ph2_1..6 points the original uses per tier; ph1_N is assumed.
            if (CreatureFor(tier, tier == _tier ? _variant : 1) is not { } creature || !_scene.Locators.TryGetValue($"ph1_{tier}", out var at)) continue;
            Node3D node;
            try
            {
                var character = CreatureModels.CharacterPathFor(creature);
                node = character != null ? CharacterBuilder.Build(character) : new Node3D();
            }
            catch (Exception e) { GD.PushWarning($"[H5] fort creature {creature}: {e.Message}"); continue; }
            node.Transform = at;
            _scene.AddChild(node);
            AddPickBody(node, tier);
            _shown[tier] = (node, creature);
        }
        Refresh();
    }

    static void AddPickBody(Node3D node, int tier)
    {
        var body = new StaticBody3D { CollisionLayer = 1 << 1, CollisionMask = 0 };
        body.SetMeta("tier", tier);
        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 1.2f, Height = 3.5f }, Position = new Vector3(0, 1.75f, 0) });
        node.AddChild(body);
    }

    Vector2? _click;

    void OnHallInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb) _click = mb.Position;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_click is not { } pos || _scene.Camera == null) return;
        _click = null;
        var from = _scene.Camera.ProjectRayOrigin(pos);
        var hit = _scene.GetWorld3D().DirectSpaceState.IntersectRay(new PhysicsRayQueryParameters3D
        {
            From = from, To = from + _scene.Camera.ProjectRayNormal(pos) * 500, CollisionMask = 1 << 1,
        });
        if (hit.Count > 0 && hit["collider"].As<Node>() is { } n && n.HasMeta("tier")) Select(n.GetMeta("tier").AsInt32());
    }

    void Select(int tier)
    {
        if (tier == _tier)
        {
            // Cycle base → upgrade → alternative upgrade among the variants this dwelling offers.
            for (int step = 1; step <= 3; step++)
            {
                int v = (_variant + step) % 3;
                if (Owner.Session.Game.DwellingCreature(Owner.Town, tier, v) != null) { _variant = v; break; }
            }
            PlaceCreatures();
        }
        else
        {
            _tier = tier;
            _variant = Owner.Session.Game.DwellingCreature(Owner.Town, tier, 1) != null ? 1 : 0;
        }
        SetCount(0);
    }

    string? Selected => CreatureFor(_tier, _variant);

    int MaxAffordable()
    {
        if (Selected is not { } c || !Game.Instance.Db.Creatures.TryGetValue(c, out var def)) return 0;
        int max = Owner.Town.Available[_tier - 1];
        var cost = new[] { def.Cost.Wood, def.Cost.Ore, def.Cost.Mercury, def.Cost.Crystal, def.Cost.Sulfur, def.Cost.Gem, def.Cost.Gold };
        for (int i = 0; i < 7; i++) if (cost[i] > 0) max = Math.Min(max, Me.Purse[i] / cost[i]);
        return Math.Max(0, max);
    }

    void SetCount(int n, bool updateEdit = true)
    {
        _count = Math.Clamp(n, 0, MaxAffordable());
        if (updateEdit && Ui.Find("JogDial/edit") is UiEditLine edit) edit.Value = _count.ToString();
        Refresh();
    }

    void Recruit(int n)
    {
        if (n <= 0 || Selected == null) return;
        if (Apply(new RecruitCmd(Owner.Town.Object, _tier, n, _variant))) SetCount(0);
    }

    public override void Refresh()
    {
        base.Refresh();
        var db = Game.Instance.Db;
        // Plates over each creature: available / weekly growth, projected from the creature's head.
        for (int tier = 1; tier <= 7; tier++)
        {
            var plate = Ui.Find($"definition_{tier}");
            if (plate == null) continue;
            if (!_shown.TryGetValue(tier, out var shown) || _scene.Camera == null) { plate.Visible = false; continue; }
            plate.Visible = true;
            var head = shown.Node.GlobalTransform.Origin + new Vector3(0, 3.2f, 0);
            var screen = _scene.Camera.UnprojectPosition(head) * (Size / _vp.Size.Max(Vector2I.One));
            plate.Position = screen - plate.Size / 2;
            (plate.FindChild("text", true, false) as UiNode)?.SetValue("count", Owner.Town.Available[tier - 1].ToString());
            (plate.FindChild("text", true, false) as UiNode)?.SetValue("growth", Owner.Session.Game.WeeklyGrowthOf(Owner.Town, tier).ToString());
        }
        if (Selected is not { } c || !db.Creatures.TryGetValue(c, out var def)) { Ui.SetVisible("CreatureInfo", false); return; }
        Ui.SetVisible("CreatureInfo", true);
        TownUi.SetPicture(Ui.Find("CreatureInfo/CreatureIcon"), TownUi.CreatureIcon(c, large: true));
        Ui.Find("CreatureInfo/creaturename")?.SetValue("creaturename", def.Name);
        void Stat(string key, string value) => (Ui.Find($"CreatureInfo/StatsHolder/{key}/value"))?.SetValue("skill", value);
        Stat("attack", def.Attack.ToString());
        Stat("defence", def.Defence.ToString());
        Stat("hitpoints", def.Health.ToString());
        Stat("speed", def.Speed.ToString());
        Stat("initiative", def.Initiative.ToString());
        Stat("shoots", def.Shots.ToString());
        Stat("mana", def.SpellPoints.ToString());
        Ui.Find("CreatureInfo/StatsHolder/damage/value")?.SetValue("minskill", def.MinDamage.ToString());
        Ui.Find("CreatureInfo/StatsHolder/damage/value")?.SetValue("maxskill", def.MaxDamage.ToString());
        Ui.Find("CreatureInfo/definition")?.SetValue("creaturedefinition", UiResources.StripMarkup(def.Description));
        var unit = new[] { def.Cost.Wood, def.Cost.Ore, def.Cost.Mercury, def.Cost.Crystal, def.Cost.Sulfur, def.Cost.Gem, def.Cost.Gold };
        for (int i = 0; i < 7; i++)
        {
            var item = Ui.Find($"CreatureInfo/cost/{TownUi.CostKeys[i]}");
            if (item == null) continue;
            item.Visible = unit[i] > 0;
            (item.FindChild("value", true, false) as UiNode)?.SetValue("value", unit[i].ToString());
        }
        TownUi.SetCostStrip(Ui.Find("CreatureCountHolder"), _count > 0 ? unit.Select(u => u * _count).ToArray() : null, Me);
    }

    public override void _Process(double delta)
    {
        // Keep the plates on the creatures (idle animations, viewport resize).
        if (_shown.Count > 0 && Engine.GetProcessFrames() % 10 == 0) RefreshPlates();
    }

    void RefreshPlates()
    {
        foreach (var (tier, shown) in _shown)
            if (Ui.Find($"definition_{tier}") is { } plate && _scene.Camera != null)
            {
                var head = shown.Node.GlobalTransform.Origin + new Vector3(0, 3.2f, 0);
                plate.Position = _scene.Camera.UnprojectPosition(head) * (Size / _vp.Size.Max(Vector2I.One)) - plate.Size / 2;
            }
    }
}
