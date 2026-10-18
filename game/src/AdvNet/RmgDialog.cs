using Godot;
using H5.AdvNet;
using H5.Core.Ui;
using H5.Rmg;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn.AdvNet;

/// <summary>
/// UI/RMGScreen, "Random Map Generator": options (map size, players, underground, water,
/// monster strength, custom template) listed in the original OptionsContainer with the stock
/// labels from UI/RMGScreen/TemplateInfo; Create generates the map with the ported generator
/// (src/H5.Rmg) and shows its minimap; Close hands the generated map's options back. All peers
/// regenerate the same map from these options and the seed. "Save Map" is disabled: there is no
/// .h5m writer yet.
/// </summary>
public partial class RmgDialog : UiScreen
{
    readonly Action<RmgSettings, string> _done;
    readonly RmgSettings _s = new() { Size = 1, Players = 2, Monsters = 2 };
    readonly BitmapText _options = new() { AnchorRight = 1, AnchorBottom = 1 };
    readonly List<(string Label, Func<string> Value, Action Next)> _rows = new();
    readonly TextureRect _minimap = new() { AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
    readonly List<string> _templates;
    bool _generated;
    const float RowH = 26;

    public RmgDialog(Action<RmgSettings, string> done) : base("UI/RMGScreen/RMGScreen.(WindowScreen).xdb")
    {
        _done = done;
        var db = AdvNetGame.Maps.Rmg;
        _templates = db.Templates.Values.Where(t => !t.TestTemplate).Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        _s.Template = _templates.FirstOrDefault() ?? "";
        _s.Seed = (int)(Time.GetTicksUsec() % int.MaxValue) | 1;

        string T(string path) => AdvNetScreens.Text("UI/RMGScreen/TemplateInfo/" + path);
        _rows.Add((T("MapSize/Name.txt"), () => T($"MapSize/Item0{_s.Size}.txt"), () => _s.Size = (_s.Size + 1) % 7));
        _rows.Add(("Players", () => _s.Players.ToString(), () => _s.Players = _s.Players % 8 + 1 < 2 ? 2 : _s.Players % 8 + 1));
        _rows.Add(("Underground", () => _s.Underground ? "Yes" : "No", () => _s.Underground = !_s.Underground));
        _rows.Add((T("AmountOfWater/Name.txt"), () => T(_s.Water == 0 ? "AmountOfWater/Item01.txt" : "AmountOfWater/Item02.txt"), () => _s.Water = _s.Water == 0 ? 2 : 0));
        _rows.Add(("Monsters", () => new[] { "Weak", "Medium", "Strong", "Very strong", "Impossible" }[_s.Monsters], () => _s.Monsters = (_s.Monsters + 1) % 5));
        _rows.Add((T("CustomTemplate/Name.txt"), () => _s.Template, () =>
        {
            int i = _templates.IndexOf(_s.Template);
            _s.Template = _templates.Count == 0 ? "" : _templates[(i + 1) % _templates.Count];
        }));

        if (Find("OptionsContainer") is { } box)
        {
            box.AddChild(_options);
            box.MouseFilter = MouseFilterEnum.Stop;
            box.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
                {
                    int row = (int)(mb.Position.Y / (RowH * UiScale));
                    if (row >= 0 && row < _rows.Count) { _rows[row].Next(); _generated = false; ShowOptions(); }
                }
            };
        }
        Find("Minimap")?.AddChild(_minimap);
        SetVisible("ProgressPanel", false);
        SetVisible("MinimapUnderground", false);
        if (Find("SaveBtn") is { } save) save.Enabled = false;
        if (Find("MapNameEdit") is UiEditLine name) name.Value = "RMG Map";
        Bus.On("create", Generate);
        Bus.On("back", Close);
        ShowOptions();
    }

    void ShowOptions() =>
        _options.Markup = string.Join("<br>", _rows.Select(r => $"<value_name>{ChatLogView.Escape(r.Label)}: <value_value>{ChatLogView.Escape(r.Value())}"));

    internal void GenerateForTest() => Generate();

    void Generate()
    {
        if (_s.Template.Length == 0)
        {
            UiNavigator.ShowOverlay(new MessageNotice(AdvNetScreens.Text("UI/RMGScreen/NoTemplatesText.txt")));
            return;
        }
        try
        {
            var res = new RmgGenerator(AdvNetGame.Maps.Rmg, _s.ToOptions()).Generate();
            var map = res.ToAdvMap(new H5.Adventure.Map.SharedCatalog(new H5.Core.Xdb.XdbLoader(Game.Instance.Fs)));
            _minimap.Texture = Minimap(map, 0);
            _generated = true;
        }
        catch (Exception e) { UiNavigator.ShowOverlay(new MessageNotice(e.Message)); }
    }

    /// <summary>Top-down terrain/blocking preview of a floor (like the RMG minimap).</summary>
    static ImageTexture? Minimap(H5.Adventure.Map.AdvMap map, int floor)
    {
        var f = map.Floor(floor);
        if (f == null) return null;
        var img = Image.CreateEmpty(f.Width, f.Height, false, Image.Format.Rgb8);
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                var c = f.At(x, y).ToString() switch
                {
                    "Water" => new Color(0.15f, 0.3f, 0.55f),
                    "Road" => new Color(0.75f, 0.68f, 0.5f),
                    "Sand" => new Color(0.8f, 0.72f, 0.45f),
                    "Snow" => new Color(0.9f, 0.92f, 0.95f),
                    "Lava" => new Color(0.4f, 0.15f, 0.1f),
                    "Subterranean" => new Color(0.35f, 0.3f, 0.28f),
                    _ => new Color(0.3f, 0.5f, 0.25f),
                };
                if (f.IsBlocked(x, y)) c = c.Darkened(0.55f);
                img.SetPixel(x, y, c);
            }
        foreach (var o in map.Objects.Where(o => o.Pos.Floor == floor && o.Kind == H5.Adventure.Map.ObjectKind.Town))
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (f.InBounds(o.Pos.X + dx, o.Pos.Y + dy)) img.SetPixel(o.Pos.X + dx, o.Pos.Y + dy, Colors.White);
        return ImageTexture.CreateFromImage(img);
    }

    void Close()
    {
        UiNavigator.CloseOverlay(this);
        if (_generated)
        {
            var name = (Find("MapNameEdit") as UiEditLine)?.Value ?? "RMG Map";
            _done(_s, $"{name} ({_s.Template})");
        }
    }
}
