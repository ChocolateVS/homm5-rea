using Godot;

namespace H5Reborn;

/// <summary>
/// Placeholder until the original 3D main-menu scene (Arenas/Interface/MainMenu)
/// and UI/MainMenu2 layouts are reproduced by the scene and UI decoders.
/// </summary>
public partial class MainMenu : Control
{
    public override void _Ready()
    {
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1 });
        AddChild(new Label
        {
            Text = $"H5Reborn — {Game.Instance.Fs.Paths.Count} original assets mounted\nMain menu under construction",
            AnchorRight = 1, AnchorBottom = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }
}
