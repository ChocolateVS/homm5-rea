using System.Text.Json.Nodes;
using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.AdvNet;
using H5Reborn.Render;
using H5Reborn.Ui;

namespace H5Reborn.AdvNet;

/// <summary>
/// Ghost Mode on a networked adventure map (client side). While another player takes their turn,
/// this player's heroes are represented by ghosts: drawn on the map with the ghost class character,
/// moved by clicking a tile, and used on an adjacent object or hero by clicking it (mine → haunt,
/// neutral stack → possess, own building → guard, enemy hero → curse, friendly hero → dispel). The
/// stock GhostInfo screen (UI/GhostInfo) shows energy and skills; EnergyUP invests energy in a
/// skill, clicking a skill makes it the default upgrade. Tab selects the next ghost, G opens the
/// GhostInfo screen. The host validates and applies everything (src/H5.AdvNet GhostMode).
/// </summary>
public partial class GhostController : Node
{
    const string InfoScreen = "UI/GhostInfo.(WindowScreen).xdb";
    const string GenericCharacter = "Characters/Heroes/Ghost.(Character).xdb";

    readonly AdvNetGame _net;
    readonly Adventure.AdventureScreen _screen;
    readonly Dictionary<int, Character> _views = new();
    readonly GhostData _data;
    int _selected = -1;
    UiScreen? _info;
    JsonObject? _shown;

    public GhostController(AdvNetGame net, Adventure.AdventureScreen screen)
    {
        _net = net;
        _screen = screen;
        _data = GhostData.Load(Game.Instance.Fs, Game.Instance.Db.Stats);
        Name = "GhostController";
    }

    int Me => _net.Replica.MyPlayer;
    JsonObject? Ghosts => _net.Replica.Ghosts;
    bool Active => Ghosts is { } g && (int?)g["activeFor"] is { } a && a >= 0 && a != Me && MyGhosts().Any();

    IEnumerable<JsonObject> AllGhosts() => Ghosts?["ghosts"]?.AsArray().Select(n => n!.AsObject()) ?? Enumerable.Empty<JsonObject>();
    IEnumerable<JsonObject> MyGhosts() => AllGhosts().Where(g => (int)g["owner"]! == Me);
    JsonObject? Selected => MyGhosts().FirstOrDefault(g => (int)g["hero"]! == _selected) ?? MyGhosts().FirstOrDefault();

    public override void _Process(double delta)
    {
        if (!ReferenceEquals(_shown, Ghosts)) { _shown = Ghosts; Sync(); }
    }

    /// <summary>Places ghost characters for every visible ghost and refreshes the info screen.</summary>
    void Sync()
    {
        var seen = new HashSet<int>();
        foreach (var g in AllGhosts())
        {
            int hero = (int)g["hero"]!;
            seen.Add(hero);
            if (!_views.TryGetValue(hero, out var view))
            {
                view = BuildGhost((string?)g["class"]);
                _screen.World.AddChild(view);
                _views[hero] = view;
            }
            var tile = new Tile((int)g["x"]!, (int)g["y"]!, (int)g["floor"]!);
            var target = _screen.World.TileCenter(tile);
            if (view.Position.DistanceTo(target) > 0.01f && view.Has("move"))
            {
                view.LookAt(new Vector3(target.X, view.Position.Y, target.Z), Vector3.Up, useModelFront: true);
                view.Play("move", loop: true);
                var tw = view.CreateTween();
                tw.TweenProperty(view, "position", target, Math.Clamp(view.Position.DistanceTo(target) / 8f, 0.15f, 2f));
                tw.TweenCallback(Callable.From(() => view.Play("idle00", loop: true)));
            }
            else view.Position = target;
            view.Visible = Active || (int)g["owner"]! != Me;
        }
        foreach (var gone in _views.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _views[gone].QueueFree();
            _views.Remove(gone);
        }
        if (_info != null) FillInfo();
    }

    Character BuildGhost(string? classId)
    {
        var path = _data.Classes.Find(c => c.Id == classId)?.Character;
        try { return CharacterBuilder.Build(string.IsNullOrEmpty(path) ? GenericCharacter : path, adventureMap: true); }
        catch (Exception) { return CharacterBuilder.Build(GenericCharacter, adventureMap: true); }
    }

    public override void _Input(InputEvent e)
    {
        if (!Active) return;
        switch (e)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Tab }:
                CycleGhost();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.G }:
                ToggleInfo();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when _info == null:
                if (_screen.World.TileAtRay(_screen.World.Camera.ProjectRayOrigin(mb.Position), _screen.World.Camera.ProjectRayNormal(mb.Position)) is { } tile)
                {
                    ClickTile(tile);
                    GetViewport().SetInputAsHandled();
                }
                break;
        }
    }

    void CycleGhost()
    {
        var mine = MyGhosts().Select(g => (int)g["hero"]!).ToList();
        if (mine.Count == 0) return;
        int i = mine.IndexOf(_selected);
        _selected = mine[(i + 1) % mine.Count];
        if (Selected is { } g) _screen.World.Camera.CenterOn(new Tile((int)g["x"]!, (int)g["y"]!, (int)g["floor"]!));
    }

    /// <summary>Uses the selected ghost on an adjacent object/hero at the tile, or moves it there.</summary>
    void ClickTile(Tile tile)
    {
        if (Selected is not { } g) return;
        int hero = (int)g["hero"]!;
        var at = new Tile((int)g["x"]!, (int)g["y"]!, (int)g["floor"]!);
        bool adjacent = at.Floor == tile.Floor && Math.Abs(at.X - tile.X) <= 1 && Math.Abs(at.Y - tile.Y) <= 1 && at != tile;
        var s = _net.Replica.View;
        if (adjacent)
        {
            if (s.Heroes.FirstOrDefault(h => h.Alive && h.Pos == tile) is { } h)
            {
                bool friendly = h.Owner == Me || SameTeam(h.Owner);
                _net.Ghost(AdvNetCommands.GhostTouch(hero, friendly ? "dispel" : "curse", h.Id));
                return;
            }
            if (ObjectAt(tile) is { } obj)
            {
                var o = _net.Replica.Map.Map.Objects[obj];
                string? action = o.Kind switch
                {
                    ObjectKind.Mine when ObjOwner(obj) != Me => "haunt",
                    ObjectKind.Monster => "possess",
                    _ when ObjOwner(obj) == Me => "guard",
                    _ => null,
                };
                if (action != null) { _net.Ghost(AdvNetCommands.GhostTouch(hero, action, obj)); return; }
            }
        }
        _net.Ghost(AdvNetCommands.GhostMove(hero, tile.X, tile.Y, tile.Floor));
    }

    int? ObjectAt(Tile t)
    {
        var objs = _net.Replica.Map.Map.Objects;
        for (int i = 0; i < objs.Count; i++)
            if (objs[i].Pos == t || objs[i].ActiveTiles.Contains(t) || objs[i].BlockedTiles.Contains(t)) return i;
        return null;
    }

    int ObjOwner(int obj) => _net.Replica.View.Town(obj)?.Owner ?? _net.Replica.View.Obj(obj)?.Owner ?? -1;

    bool SameTeam(int other)
    {
        var s = _net.Replica.View;
        return s.Players.Find(p => p.Index == Me) is { } a && s.Players.Find(p => p.Index == other) is { } b && a.Team == b.Team;
    }

    // ---------------------------------------------------------------- GhostInfo screen

    void ToggleInfo()
    {
        if (_info != null) { _info.QueueFree(); _info = null; return; }
        _info = new UiScreen(InfoScreen);
        _screen.DialogLayer.AddChild(_info);
        for (int i = 0; i < GhostSkills.All.Length; i++)
        {
            var skill = GhostSkills.All[i];
            var block = _info.Find($"Info/Skill_{i / 2 + 1}_{i % 2}");
            if (block == null) continue;
            if (block.FindChild("EnergyUP", true, false) is UiNode up)
                up.Clicked += _ => Upgrade(skill);
            if (block.FindChild("desc_win", true, false) is Control desc)
                desc.GuiInput += ev =>
                {
                    if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && Selected is { } g)
                        _net.Ghost(new JsonObject { ["$t"] = "ghost", ["action"] = "default", ["hero"] = (int)g["hero"]!, ["skill"] = skill });
                };
        }
        _info.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) ToggleInfo();
        };
        FillInfo();
    }

    /// <summary>Shows a scaling-texture bar filled to <paramref name="f"/> (horizontal scale from the left edge).</summary>
    static void SetFill(Control bar, float f)
    {
        bar.PivotOffset = Vector2.Zero;
        bar.Scale = new Vector2(Math.Max(0.001f, f), 1);
    }

    void Upgrade(string skill)
    {
        if (Selected is not { } g) return;
        _net.Ghost(AdvNetCommands.GhostUpgrade((int)g["hero"]!, skill));
    }

    void FillInfo()
    {
        if (_info == null || Selected is not { } g) return;
        var hero = _net.Replica.View.Heroes.FirstOrDefault(h => h.Id == (int)g["hero"]!);
        int energy = (int?)g["energy"] ?? 0;
        int max = (int?)Ghosts?["maxLevel"] ?? _data.MaxSkillLevel;
        _info.Find("Info/HeroHeader")?.SetValue("HeroName", hero?.Name ?? "");
        _info.Find("Info/HeroStats")?.SetValue("", energy.ToString());
        if (_info.Find("Info/Energy") is { } bar)
            SetFill(bar, Math.Clamp(energy / (float)Math.Max(1, GhostRules.Revive(_data, 0) * 3), 0f, 1f));
        string def = (string?)g["default"] ?? "";
        for (int i = 0; i < GhostSkills.All.Length; i++)
        {
            var skill = GhostSkills.All[i];
            var block = _info.Find($"Info/Skill_{i / 2 + 1}_{i % 2}");
            if (block == null) continue;
            int level = (int?)g["levels"]?[skill] ?? 1, exp = (int?)g["experience"]?[skill] ?? 0;
            int lo = GhostRules.ExperienceFor(_data, level), hi = GhostRules.ExperienceFor(_data, Math.Min(max, level + 1));
            var name = _data.Skills.Find(sd => sd.Id == skill)?.Name ?? skill;
            if (block.FindChild("Desc", true, false) is UiNode desc) { desc.SetValue("Desc", name); desc.SetValue("Level", level.ToString()); }
            if (block.FindChild("Bar", true, false) is UiNode b) SetFill(b, hi > lo ? Math.Clamp((exp - lo) / (float)(hi - lo), 0f, 1f) : 1f);
            if (block.FindChild("selection", true, false) is Control sel) sel.Visible = skill == def;
            if (block.FindChild("EnergyDOWN", true, false) is Control down) down.Visible = false;
        }
    }
}
