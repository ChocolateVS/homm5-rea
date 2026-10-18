using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The puzzle map (UI/PuzzleMap): a top-down view of the land around the Tear of Asha under the 36 pieces of the
/// player's faction puzzle (UI/PuzzleMap/&lt;Faction&gt;.(PuzzleDesc).xdb); one share of the pieces is lifted per
/// obelisk visited and the Tear of Asha mark sits in the middle. Esc / P or the close button return to the map.
/// TODO(exe): the order the pieces come off is not traced (list order), nor the exact zoom of the view.
/// </summary>
public sealed partial class PuzzleMapScreen : Control
{
    const float ViewTiles = 24f;

    public PuzzleMapScreen(AdventureScreen adv)
    {
        Name = "PuzzleMap";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        var me = adv.Session.Human;
        var grail = adv.Session.S.Grail ?? new Tile(adv.Session.Map.TilesX / 2, adv.Session.Map.TilesY / 2, 0);

        // The land around the grail, rendered from the adventure world straight down without the fog of war.
        AddChild(new ColorRect { Name = "Black", Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var container = new SubViewportContainer { Name = "View", Stretch = true, MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(1024, 768) };
        _view = container;
        var vp = new SubViewport { World3D = adv.GetViewport().World3D, HandleInputLocally = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        var env = adv.GetViewport().World3D?.Environment?.Duplicate() as Godot.Environment;
        if (env != null) { env.FogEnabled = false; env.VolumetricFogEnabled = false; }
        var target = adv.World.TileCenter(grail);
        var cam = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = ViewTiles * AdvWorld.Unit, Near = 1, Far = 2000,
            CullMask = ~(AdvWorld.ShroudLayer | AdvWorld.SkyLayer), Environment = env, Current = true,
        };
        vp.AddChild(cam);
        container.AddChild(vp);
        AddChild(container);
        cam.Position = target + new Vector3(0, 500, 0);
        cam.LookAtFromPosition(cam.Position, target, Vector3.Forward);

        var ui = new UiScreen("UI/PuzzleMap.(WindowScreen).xdb");
        AddChild(ui);
        ui.Bus.On("close", Close);
        ui.SetVisible("UglyWindow", false);
        int open = adv.Session.Game.PuzzlePiecesOpen(me);
        _graal = ui.Find("GraalWindow");
        _pieces = AddPieces(this, me, open);
        // BackgroundSimpleTexture draws unscaled from the window's top-left.
        if (ui.Find("FrameWindow") is { } frame && _frameTex != null)
            frame.AddChild(new TextureRect { Texture = _frameTex, StretchMode = TextureRect.StretchModeEnum.Keep, MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true });
        TownUi.SetPicture(ui.Find("CloseButton"), _closeTex);
        // The Tear of Asha mark stays above the pieces.
        if (_graal != null) _graal.ZIndex = 1;
        if (ui.Find("CloseButton") is { } closeButton) closeButton.ZIndex = 2;
    }

    static string PuzzleFile(string race) => race switch
    {
        "TOWN_PRESERVE" => "Rampart", "TOWN_ACADEMY" => "Academy", "TOWN_DUNGEON" => "Dungeon", "TOWN_NECROMANCY" => "Necropolis",
        "TOWN_INFERNO" => "Inferno", "TOWN_FORTRESS" => "Dwarven", "TOWN_STRONGHOLD" => "Stronghold", _ => "Heaven",
    };

    readonly UiNode? _graal;
    static Texture2D? _frameTex, _closeTex;
    readonly SubViewportContainer _view;
    readonly Control _pieces;

    /// <summary>
    /// Pieces are authored in 1024×768 screen pixels: the layer follows the stock layout through the Tear of
    /// Asha mark (GraalWindow, 64×64 at 480,352).
    /// </summary>
    public override void _Process(double delta)
    {
        if (_graal == null || _graal.Size.X <= 0) return;
        // The UI is scaled by transform (UI scale), so the mark's global scale gives the layout factor.
        float k = _graal.GetGlobalTransform().Scale.X * _graal.Size.X / 64f;
        _pieces.Scale = new Vector2(k, k);
        _pieces.GlobalPosition = _graal.GlobalPosition - new Vector2(480, 352) * k;
        // The land view fills the 1024×768 puzzle screen only; black around it, as the stock BlackBorder windows.
        _view.GlobalPosition = _pieces.GlobalPosition;
        _view.Size = new Vector2(1024, 768) * k;
    }

    static Control AddPieces(Control host, PlayerState me, int open)
    {
        var desc = $"UI/PuzzleMap/{PuzzleFile(me.Race)}.(PuzzleDesc).xdb";
        var pieces = Game.Instance.Xdb.Load(desc).Root?.Element("pieces")?.Elements("Item").ToList() ?? new();
        var layer = new Control { Name = "Pieces", MouseFilter = MouseFilterEnum.Ignore };
        host.AddChild(layer);
        var root = Game.Instance.Xdb.Load(desc).Root;
        Texture2D? Tex(string el) => (string?)root?.Element(el)?.Attribute("href") is { Length: > 0 } h ? Game.Instance.LoadTexture(XdbRef.Parse(h, desc).Path) : null;
        // The faction's frame and close button (PuzzleDesc Frame / CloseButton*), set at run time like the original.
        _frameTex = Tex("Frame");
        _closeTex = Tex("CloseButtonActive");
        for (int i = open; i < pieces.Count; i++)
        {
            var href = (string?)pieces[i].Element("Texture")?.Attribute("href");
            if (string.IsNullOrEmpty(href) || Game.Instance.LoadTexture(XdbRef.Parse(href, desc).Path) is not { } tex) continue;
            layer.AddChild(new TextureRect
            {
                Texture = tex, MouseFilter = MouseFilterEnum.Ignore,
                Position = new Vector2((int?)pieces[i].Element("x") ?? 0, (int?)pieces[i].Element("y") ?? 0),
            });
        }
        return layer;
    }

    void Close() => QueueFree();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape or Key.P })
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }
}
