using Godot;

namespace H5Reborn.Arena;

/// <summary>
/// Developer scene: `-- --view-arena=Grass_Big_01 [--arena-units] [--arena-grid]`.
/// Shows a battlefield from the combat camera; with --arena-units places sample
/// armies, with --arena-grid shows the cell overlay. Hovering highlights the cell
/// under the mouse.
/// </summary>
public partial class ArenaViewer : Node3D
{
    public static string ArenaName = "Grass_Big_01";
    public static bool ShowUnits, ShowGrid;

    ArenaScene _arena = null!;
    Vector2I? _hover;

    /// <summary>Handles the viewer's command-line options; returns true if the arg was consumed.</summary>
    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--view-arena="))
        {
            ArenaName = arg["--view-arena=".Length..];
            Callable.From(Open).CallDeferred();
            return true;
        }
        if (arg == "--view-menu-background")
        {
            Callable.From(() =>
            {
                var tree = (SceneTree)Engine.GetMainLoop();
                tree.CurrentScene?.QueueFree();
                var bg = new MainMenuBackground { Name = "MainMenuBackground" };
                tree.Root.AddChild(bg);
                tree.CurrentScene = bg;
            }).CallDeferred();
            return true;
        }
        if (arg.StartsWith("--test-sound="))
        {
            // Loads a sound descriptor, reports the decoded stream, plays it briefly.
            var href = arg["--test-sound=".Length..];
            Callable.From(() =>
            {
                var def = Audio.SoundLibrary.Load(href);
                GD.Print(def == null ? $"[H5] sound {href}: not found"
                    : $"[H5] sound {href}: {def.Stream.GetType().Name} {def.Stream.GetLength():F2}s 3D={def.Is3D} loop={def.Loop}");
                Audio.SoundLibrary.Play(href, ((SceneTree)Engine.GetMainLoop()).Root);
            }).CallDeferred();
            return true;
        }
        if (arg == "--arena-units") { ShowUnits = true; return true; }
        if (arg == "--arena-grid") { ShowGrid = true; return true; }
        return false;
    }

    static void Open()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CurrentScene?.QueueFree();
        var viewer = new ArenaViewer();
        tree.Root.AddChild(viewer);
        tree.CurrentScene = viewer;
    }

    public override void _Ready()
    {
        _arena = ArenaScene.Create(ArenaName);
        AddChild(_arena);
        _arena.Overlay.ShowGrid = ShowGrid;
        if (ShowUnits)
        {
            // Attacker: Haven duel army; defender: Dungeon. Large creatures take 2×2.
            _arena.PlaceCreature("CREATURE_FOOTMAN", new Vector2I(1, 0), 1, 0);
            _arena.PlaceCreature("CREATURE_ARCHER", new Vector2I(0, 2), 1, 0);
            _arena.PlaceCreature("CREATURE_GRIFFIN", new Vector2I(0, 4), 2, 0);
            _arena.PlaceCreature("CREATURE_PRIEST", new Vector2I(0, 7), 1, 0);
            _arena.PlaceCreature("CREATURE_ANGEL", new Vector2I(1, 8), 2, 0);
            _arena.PlaceCreature("CREATURE_SCOUT", new Vector2I(10, 0), 1, 1);
            _arena.PlaceCreature("CREATURE_WITCH", new Vector2I(11, 2), 1, 1);
            _arena.PlaceCreature("CREATURE_MINOTAUR", new Vector2I(10, 4), 1, 1);
            _arena.PlaceCreature("CREATURE_SHADOW_DRAGON", new Vector2I(9, 6), 2, 1);
            _arena.PlaceCreature("CREATURE_HYDRA", new Vector2I(10, 8), 2, 1);
        }
        AddChild(new Label { Text = _arena.Definition.ScenePath, Position = new Vector2(8, 8) });
    }

    public override void _Process(double delta)
    {
        var cell = _arena.CellAtScreen(GetViewport().GetMousePosition());
        if (cell == _hover) return;
        if (_hover is { } old) _arena.Overlay.SetMark(old, CellMark.None);
        if (cell is { } c) _arena.Overlay.SetMark(c, CellMark.Select);
        _hover = cell;
    }
}
