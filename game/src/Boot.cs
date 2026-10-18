using Godot;

namespace H5Reborn;

/// <summary>
/// First scene: plays the stock intro movie list (UIGameRoot movies "intro" →
/// video/intro_a1.xml: Ubisoft and Nival logos) and hands over to the main menu,
/// as the original does. The UI/AfterGameSplash pictures are shown on quitting
/// (see <see cref="Cinematics.AfterGameSplash"/>).
/// </summary>
public partial class Boot : Control
{
    public override void _Ready()
    {
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1 });

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
        if (Cinematics.CinematicsDevArgs.TryStart(this)) return;
        var movies = Cinematics.MoviePlayer.LoadList("../video/intro_a1.xml");
        if (movies.Count == 0 || Cinematics.StartupOptions.SkipIntro) { Finish(); return; }
        AddChild(new Cinematics.MoviePlayer(movies, Finish));
    }

    // Dev args (--adventure=, --view-town=, …) may already have replaced the boot scene; don't clobber it.
    void Finish() => Callable.From(() =>
    {
        if (IsInstanceValid(this) && GetTree().CurrentScene == this) GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }).CallDeferred();
}
