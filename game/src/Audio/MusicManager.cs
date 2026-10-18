using Godot;
using H5.Assets.Audio;

namespace H5Reborn.Audio;

/// <summary>
/// The music player stack (NSound::CMusicPlayerStack, vtable 00e47be8). Each screen pushes a
/// player built around a music calculator; the top player sounds, players underneath are
/// paused where they are and resume when the screen above closes (the stack's push/pop
/// pause and un-pause the previous top: FUN_00819e00). Looping players (CMusicPlayer) ask
/// their calculator for a new track after each one ends and a random pause of
/// MinPausedTime..MaxPausedTime ms; one-shot players (CNonLoopMusicPlayer — battle results,
/// grail theme) play a single track and then pop themselves. Tracks fade in and out over
/// their descriptor's FadeIn / FadeOut ms.
/// </summary>
public partial class MusicManager : Node
{
    public static MusicManager? Instance { get; private set; }

    internal static void Create()
    {
        if (Instance != null) return;
        Instance = new MusicManager { Name = "H5Music", ProcessMode = ProcessModeEnum.Always };
        Game.Instance.CallDeferred(Node.MethodName.AddChild, Instance);
    }

    public sealed class Handle
    {
        internal Handle(string name, IMusicCalculator calc, bool once, Action? done) { Name = name; Calc = calc; Once = once; Done = done; }
        public string Name { get; }
        internal readonly IMusicCalculator Calc;
        internal readonly bool Once;
        internal readonly Action? Done;
        internal AudioStreamPlayer? Player;
        internal MusicTrack? Track;
        internal double PauseLeft = -1;   // >= 0 while waiting between tracks
        internal float Gain, Target;       // linear volume and where it's heading
        internal float Rate;               // gain per second
        internal bool Started, Finished;
        public bool Active => Instance?._stack.Count > 0 && Instance._stack[^1] == this;
    }

    readonly List<Handle> _stack = new();
    readonly Random _rng = new();
    readonly List<Handle> _fadingOut = new();

    /// <summary>The name of the playing context and its current track (for logs and tests).</summary>
    public string Describe() => _stack.Count == 0 ? "(silence)" : $"{_stack[^1].Name}: {_stack[^1].Track?.FileName ?? "(pause)"}";

    public static Handle Push(string name, IMusicCalculator calc)
    {
        AudioSystem.EnsureInstalled();
        var h = new Handle(name, calc, once: false, done: null);
        Instance!.PushHandle(h);
        return h;
    }

    /// <summary>Plays one track over the current music (which pauses), then returns to it.</summary>
    public static Handle? PlayOnce(string name, MusicTrack? track, Action? done = null)
    {
        AudioSystem.EnsureInstalled();
        if (track == null) { done?.Invoke(); return null; }
        var h = new Handle(name, new ConstantMusicCalculator(track), once: true, done);
        Instance!.PushHandle(h);
        return h;
    }

    /// <summary>Removes a context wherever it is in the stack; the new top resumes.</summary>
    public static void Pop(Handle? h)
    {
        if (h == null || Instance == null) return;
        Instance.RemoveHandle(h);
    }

    /// <summary>Replaces the whole stack (front-end screens: main menu, credits).</summary>
    public static Handle SetRoot(string name, IMusicCalculator calc)
    {
        AudioSystem.EnsureInstalled();
        var m = Instance!;
        if (m._stack.Count == 1 && m._stack[0].Name == name) return m._stack[0];
        foreach (var old in m._stack.ToList()) m.RemoveHandle(old);
        return Push(name, calc);
    }

    void PushHandle(Handle h)
    {
        if (_stack.Count > 0) Suspend(_stack[^1]);
        _stack.Add(h);
        AudioSystem.Trace($"push {h.Name} (depth {_stack.Count})");
        Resume(h);
    }

    void RemoveHandle(Handle h)
    {
        int i = _stack.IndexOf(h);
        if (i < 0) return;
        bool wasTop = i == _stack.Count - 1;
        _stack.RemoveAt(i);
        AudioSystem.Trace($"pop {h.Name} (depth {_stack.Count})");
        Stop(h);
        if (wasTop && _stack.Count > 0) Resume(_stack[^1]);
        h.Done?.Invoke();
    }

    /// <summary>Fades the context's track out, then pauses it in place.</summary>
    void Suspend(Handle h)
    {
        if (h.Player == null) return;
        FadeTo(h, 0f, h.Track?.FadeOutMs ?? 0);
    }

    void Resume(Handle h)
    {
        if (h.Player != null && h.Started && !h.Finished)
        {
            h.Player.StreamPaused = false;
            FadeTo(h, 1f, h.Track?.FadeInMs ?? 0);
            return;
        }
        if (h.PauseLeft < 0) StartNext(h);
    }

    void Stop(Handle h)
    {
        if (h.Player == null) return;
        FadeTo(h, 0f, h.Track?.FadeOutMs ?? 0);
        _fadingOut.Add(h);
    }

    void StartNext(Handle h)
    {
        // The manager joins the tree deferred; start on the first frame instead.
        if (!IsInsideTree()) { h.PauseLeft = 0; return; }
        h.PauseLeft = -1;
        var track = h.Calc.Next(_rng);
        if (track == null) { h.Track = null; return; }
        var file = MusicData.ResolveFile(Game.Instance.Install.Root, track.FileName);
        if (file == null) { GD.PushWarning($"[H5][audio] music file missing: {track.FileName}"); return; }
        var stream = AudioStreamOggVorbis.LoadFromFile(file);
        if (stream == null) { GD.PushWarning($"[H5][audio] cannot load {file}"); return; }
        stream.Loop = false;

        if (h.Player != null) { h.Player.QueueFree(); h.Player = null; }
        var p = new AudioStreamPlayer { Stream = stream, Bus = Options.AudioBuses.For(Options.AudioBuses.Music), VolumeDb = -80 };
        AddChild(p);
        h.Player = p;
        h.Track = track;
        h.Started = true;
        h.Finished = false;
        h.Gain = 0;
        p.Finished += () => OnTrackFinished(h);
        p.Play();
        FadeTo(h, h.Active ? 1f : 0f, track.FadeInMs);
        if (!h.Active) p.StreamPaused = true;
        AudioSystem.Trace($"play {h.Name}: {track.Path} ({track.FileName})");
    }

    void OnTrackFinished(Handle h)
    {
        h.Finished = true;
        if (h.Once) { RemoveHandle(h); return; }
        if (!_stack.Contains(h)) return;
        // Random silence between tracks, MinPausedTime..MaxPausedTime ms (inclusive range).
        int min = h.Track?.MinPausedMs ?? 0, max = Math.Max(min, h.Track?.MaxPausedMs ?? 0);
        h.PauseLeft = (min + _rng.Next(max - min + 1)) / 1000.0;
    }

    static void FadeTo(Handle h, float target, int ms)
    {
        h.Target = target;
        h.Rate = ms <= 0 ? float.PositiveInfinity : 1000f / ms;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        foreach (var h in _stack.Concat(_fadingOut).ToList()) Step(h, dt);
        for (int i = _fadingOut.Count - 1; i >= 0; i--)
        {
            var h = _fadingOut[i];
            if (h.Gain > 0.0001f && h.Player != null) continue;
            h.Player?.QueueFree();
            h.Player = null;
            _fadingOut.RemoveAt(i);
        }
        if (_stack.Count > 0 && _stack[^1] is { PauseLeft: >= 0 } top)
        {
            top.PauseLeft -= delta;
            if (top.PauseLeft < 0) StartNext(top);
        }
    }

    static void Step(Handle h, float dt)
    {
        if (h.Player == null) return;
        if (h.Gain != h.Target)
        {
            float step = float.IsInfinity(h.Rate) ? 1f : h.Rate * dt;
            h.Gain = h.Gain < h.Target ? Math.Min(h.Target, h.Gain + step) : Math.Max(h.Target, h.Gain - step);
        }
        h.Player.VolumeDb = Mathf.LinearToDb(Math.Max(h.Gain, 0.0001f));
        // A suspended context stays paused at its position once faded out.
        if (h.Target <= 0 && h.Gain <= 0.0001f && !h.Active) h.Player.StreamPaused = true;
    }
}
