using Godot;

namespace H5Reborn;

/// <summary>
/// First scene: shows the original after-splash screens (UI/H5A2/AfterSplash) the
/// same way the stock game does, then hands over to the main menu.
/// </summary>
public partial class Boot : Control
{
    static readonly string[] Splashes =
    {
        "UI/H5A2/AfterSplash/Splash1.(Texture).xdb",
        "UI/H5A2/AfterSplash/Splash2.(Texture).xdb",
    };

    const double FadeTime = 0.5, HoldTime = 2.5;

    TextureRect _image = null!;
    int _index = -1;

    public override void _Ready()
    {
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1 });
        _image = new TextureRect
        {
            AnchorRight = 1, AnchorBottom = 1,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(1, 1, 1, 0),
        };
        AddChild(_image);

        if (Game.Instance.StartupError is { } error)
        {
            AddChild(new Label
            {
                Text = $"H5Reborn could not start:\n{error}",
                AnchorRight = 1, AnchorBottom = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }
        Next();
    }

    void Next()
    {
        _index++;
        if (_index >= Splashes.Length) { Finish(); return; }
        _image.Texture = Game.Instance.LoadTexture(Splashes[_index]);
        if (_image.Texture == null) { Next(); return; }
        var tw = CreateTween();
        tw.TweenProperty(_image, "modulate:a", 1.0, FadeTime);
        tw.TweenInterval(HoldTime);
        tw.TweenProperty(_image, "modulate:a", 0.0, FadeTime);
        tw.TweenCallback(Callable.From(Next));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // Like the original, any key or click skips the splash.
        if (e is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true })
            Finish();
    }

    void Finish()
    {
        SetProcessUnhandledInput(false);
        GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }
}
