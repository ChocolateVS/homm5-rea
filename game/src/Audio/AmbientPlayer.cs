using Godot;
using H5.Assets.Audio;

namespace H5Reborn.Audio;

/// <summary>
/// Plays an AmbientSoundSet the way NSound::CAmbientSound does: the set's Loop sound runs
/// continuously and one of its <c>sounds</c> plays at random after a pause of
/// MinPauseTime..MaxPauseTime seconds, again and again. Switching sets crossfades over
/// sound_ambient_fade_duration seconds. Everything goes to the Ambient bus, which the
/// original's sound_ambient_enable switch mutes.
/// </summary>
public partial class AmbientPlayer : Node
{
    sealed class Layer
    {
        public AmbientSet Set = null!;
        public AudioStreamPlayer? Loop;
        public AudioStreamPlayer? OneShot;
        public double Wait;
        public float Gain, Target;
    }

    readonly List<Layer> _layers = new();
    readonly Random _rng = new();
    Layer? _current;

    public string? CurrentSet => _current?.Set.Path;

    /// <summary>Crossfades to the given set (null fades to silence). Same set: no change.</summary>
    public void SetAmbient(string? setPath)
    {
        if (string.Equals(_current?.Set.Path, setPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_current != null) _current.Target = 0;
        _current = null;
        if (setPath == null || AudioSystem.Data.Ambient(setPath) is not { } set) { AudioSystem.Trace("ambient off"); return; }
        var layer = new Layer { Set = set, Gain = 0, Target = 1 };
        if (set.Loop != null && SoundLibrary.Load(set.Loop) is { } def)
        {
            layer.Loop = new AudioStreamPlayer { Stream = def.Stream, Bus = AudioSystem.AmbientBus, VolumeDb = -80, Autoplay = true };
            AddChild(layer.Loop);
            layer.Loop.Finished += () => layer.Loop?.Play();   // keep the bed running even if the stream isn't flagged as looping
        }
        layer.Wait = NextPause(set);
        _layers.Add(layer);
        _current = layer;
        AudioSystem.Trace($"ambient {set.Path}");
    }

    double NextPause(AmbientSet s)
    {
        float min = Math.Max(0, s.MinPause), max = Math.Max(min, s.MaxPause);
        return min + _rng.NextDouble() * (max - min);
    }

    public override void _Process(double delta)
    {
        float rate = 1f / Math.Max(0.01f, AudioSystem.AmbientFadeSeconds);
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var l = _layers[i];
            l.Gain = l.Gain < l.Target ? Math.Min(l.Target, l.Gain + rate * (float)delta) : Math.Max(l.Target, l.Gain - rate * (float)delta);
            float db = Mathf.LinearToDb(Math.Max(l.Gain, 0.0001f));
            if (l.Loop != null) l.Loop.VolumeDb = db;
            if (l.OneShot != null) l.OneShot.VolumeDb = db + OneShotDb(l);
            if (l != _current && l.Gain <= 0.0001f)
            {
                l.Loop?.QueueFree();
                l.OneShot?.QueueFree();
                _layers.RemoveAt(i);
                continue;
            }
            if (l != _current || l.Set.Sounds.Count == 0) continue;
            if (l.OneShot != null && l.OneShot.Playing) continue;
            l.Wait -= delta;
            if (l.Wait > 0) continue;
            PlayOneShot(l);
            l.Wait = NextPause(l.Set);
        }
    }

    float _oneShotDb;
    float OneShotDb(Layer l) => _oneShotDb;

    void PlayOneShot(Layer l)
    {
        var href = l.Set.Sounds[_rng.Next(l.Set.Sounds.Count)];
        if (SoundLibrary.Load(href) is not { } def) return;
        l.OneShot?.QueueFree();
        float volume = Math.Max(0.001f, def.VolumeBase + (float)(_rng.NextDouble() * 2 - 1) * def.VolumeVar);
        _oneShotDb = Mathf.LinearToDb(volume);
        l.OneShot = new AudioStreamPlayer
        {
            Stream = def.Stream, Bus = AudioSystem.AmbientBus,
            PitchScale = Math.Max(0.1f, 1 + (float)(_rng.NextDouble() * 2 - 1) * def.FrequencyVar),
            VolumeDb = Mathf.LinearToDb(Math.Max(l.Gain, 0.0001f)) + _oneShotDb, Autoplay = true,
        };
        AddChild(l.OneShot);
    }
}
