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
        _framesInScene++;
        // A few frames after a screen became current it has been built and drawn at least once.
        if (_framesInScene == 5)
        {
            if (name == "MainMenu" && _announced.Add(name)) GD.Print("[H5] web: main menu on screen");
            if (name.StartsWith("Adventure", StringComparison.Ordinal) && name.EndsWith("Screen", StringComparison.Ordinal) && _announced.Add("adv"))
                GD.Print("[H5] web: adventure on screen");
        }
        _sinceStats += delta;
        if (_sinceStats >= 10)
        {
            _sinceStats = 0;
            GD.Print($"[H5] web: stats scene={name} fps={Engine.GetFramesPerSecond()} gcMB={GC.GetTotalMemory(false) / 1048576.0:F0} " +
                     $"staticMB={OS.GetStaticMemoryUsage() / 1048576.0:F0} vramMB={Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F0} " +
                     $"reads={BrowserFileSource.Requests} readMB={BrowserFileSource.BytesRequested / 1048576.0:F0}");
        }
    }
}
