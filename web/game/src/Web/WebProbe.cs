using Godot;

namespace H5Reborn.Web;

/// <summary>
/// Web-build-only autoload (added to the staged project.godot by tools/web/build.sh): prints a log line when a
/// screen is up, so the headless smoke test (tools/web/harness/smoke.mjs) knows when to take its screenshot, plus a
/// periodic status line (fps, memory, file reads). Observes only; never changes what the game does.
/// </summary>
public partial class WebProbe : Node
{
    string? _scene;
    int _framesInScene;
    double _sinceStats;
    readonly HashSet<string> _announced = new();
    (int Day, int Player, bool Human)? _turn;
    bool _lobbyHooked;

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    public override void _Process(double delta)
    {
        var cur = GetTree().CurrentScene;
        var name = cur?.GetType().Name ?? cur?.Name ?? "none";
        if (name != _scene)
        {
            _scene = name;
            _framesInScene = 0;
            GD.Print($"[H5] web: scene {name}");
        }
        // The adventure screen builds its map over several frames: count from when it is loaded.
        if (cur is H5Reborn.Adventure.AdventureScreen { IsLoaded: false }) _framesInScene = 0;
        _framesInScene++;
        // A few frames after a screen became current it has been built and drawn at least once.
        if (_framesInScene == 5)
        {
            if (name == "MainMenu" && _announced.Add(name)) GD.Print("[H5] web: main menu on screen");
            if (name.StartsWith("Adventure", StringComparison.Ordinal) && name.EndsWith("Screen", StringComparison.Ordinal) && _announced.Add("adv"))
                GD.Print("[H5] web: adventure on screen");
        }
        // Turn changes on the adventure map (the smoke test's end-turn scenario waits for "human turn" on day 2).
        if (cur is H5Reborn.Adventure.AdventureScreen adv && adv.Session is { } ses)
        {
            var t = (ses.S.Day, ses.S.CurrentPlayer, ses.IsHumanTurn);
            if (_turn != t)
            {
                _turn = t;
                GD.Print($"[H5] web: adventure day {t.Day} player {t.CurrentPlayer} {(t.IsHumanTurn ? "human turn" : "computer turn")}");
            }
        }
        // Multiplayer lobby: its status lines (connected, errors), once the game made the service.
        if (!_lobbyHooked && GetTree().Root.GetNodeOrNull("LobbyService") is H5Reborn.Ui.Net.LobbyService lobby)
        {
            _lobbyHooked = true;
            lobby.StatusChanged += st => GD.Print($"[H5] web: lobby: {st}");
        }
        // Movies (intro logos, campaign cinematics): announce each one once it has really advanced.
        if (_framesInScene % 15 == 0)
            foreach (var n in GetTree().Root.FindChildren("*", "VideoStreamPlayer", true, false))
                if (n is VideoStreamPlayer { } v && v.IsPlaying() && v.StreamPosition > 0.5 && v.Stream is VideoStreamTheora th && _announced.Add("video:" + th.File))
                    GD.Print($"[H5] web: video playing {th.File} at {v.StreamPosition:F1}s, frame {v.GetVideoTexture()?.GetSize()}");
        _sinceStats += delta;
        if (_sinceStats >= 10)
        {
            _sinceStats = 0;
            GD.Print($"[H5] web: stats scene={name} fps={Engine.GetFramesPerSecond()} gcMB={GC.GetTotalMemory(false) / 1048576.0:F0} gcCommitMB={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0:F0} " +
                     $"staticMB={OS.GetStaticMemoryUsage() / 1048576.0:F0} vramMB={Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F0} " +
                     $"reads={BrowserFileSource.Requests} readMB={BrowserFileSource.BytesRequested / 1048576.0:F0} " +
                     // Per-frame cost: script _process / _physics_process (ms), what the renderer drew.
                     $"procMs={Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F1} physMs={Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:F1} " +
                     $"objs={Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)} draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} " +
                     $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)} nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}");
        }
    }
}
