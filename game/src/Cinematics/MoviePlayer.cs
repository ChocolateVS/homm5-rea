using Godot;
using H5.Assets.Scenes;

namespace H5Reborn.Cinematics;

/// <summary>
/// Plays a movie list (video/intro_a1.xml, outro_a1.xml — UIGameRoot "movies") full-screen
/// on the stock VIDEO_PLAYER screen layout: each Ogg Theora file in order, letterboxed on
/// black. CanSkipMovie lets a click / Space / Enter end the current movie, CanSkipSequence
/// lets Escape end the whole list. Files the install doesn't ship (the GOG build has no
/// "claim" movie) are skipped. Plays on the main thread (web-port safe).
/// </summary>
public partial class MoviePlayer : Control
{
    readonly List<MovieEntry> _movies;
    readonly Action _onFinished;
    VideoStreamPlayer _player = null!;
    int _index = -1;
    bool _done;

    public MoviePlayer(List<MovieEntry> movies, Action onFinished)
    {
        _movies = movies;
        _onFinished = onFinished;
        Name = "MoviePlayer";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Loads a UIGameRoot movie description ("../video/intro_a1.xml") relative to the install's bin folder.</summary>
    public static List<MovieEntry> LoadList(string movieDesc)
    {
        var install = Game.Instance.Install;
        var path = H5.Core.IO.InstallPath.ResolveFile(install.Root, "bin", movieDesc);
        if (path == null) return new();
        try { return MovieList.Parse(System.IO.File.ReadAllText(path)); }
        catch (System.Xml.XmlException e) { GD.PushWarning($"[H5] bad movie list {path}: {e.Message}"); return new(); }
    }

    public override void _Ready()
    {
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        _player = new VideoStreamPlayer
        {
            AnchorRight = 1, AnchorBottom = 1,
            Expand = true,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_player);
        _player.Finished += Next;
        Next();
    }

    void Next()
    {
        if (_done) return;
        while (++_index < _movies.Count)
        {
            var file = MovieList.Resolve(Game.Instance.Install, _movies[_index].FileName);
            if (file == null) continue;
            var stream = new VideoStreamTheora { File = file };
            _player.Stream = stream;
            _player.Play();
            FitAspect();
            return;
        }
        Finish();
    }

    /// <summary>Letterbox: keep the movie's aspect inside the window.</summary>
    void FitAspect()
    {
        var tex = _player.GetVideoTexture();
        var size = tex?.GetSize() ?? Vector2.Zero;
        if (size.X <= 0 || size.Y <= 0) { Callable.From(FitAspect).CallDeferred(); return; }
        var view = GetViewportRect().Size;
        float scale = Math.Min(view.X / size.X, view.Y / size.Y);
        var fit = size * scale;
        _player.AnchorRight = 0;
        _player.AnchorBottom = 0;
        _player.Position = (view - fit) * 0.5f;
        _player.Size = fit;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _player != null && _player.IsPlaying()) FitAspect();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_done || _index < 0 || _index >= _movies.Count) return;
        var m = _movies[_index];
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && m.CanSkipSequence)
        {
            GetViewport().SetInputAsHandled();
            Finish();
        }
        else if (m.CanSkipMovie && (e is InputEventMouseButton { Pressed: true }
                     || e is InputEventKey { Pressed: true, Keycode: Key.Space or Key.Enter or Key.KpEnter }))
        {
            GetViewport().SetInputAsHandled();
            _player.Stop();
            Next();
        }
    }

    void Finish()
    {
        if (_done) return;
        _done = true;
        _player?.Stop();
        _onFinished();
        QueueFree();
    }
}
