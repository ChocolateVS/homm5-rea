using System.Globalization;
using Godot;
using H5.Assets.Audio;
using H5.Core.Xdb;

namespace H5Reborn.Audio;

/// <summary>A Sounds/_(Sound) descriptor with its decoded stream.</summary>
public sealed class SoundDef
{
    public AudioStream Stream = null!;
    public bool Loop, Is3D;
    public float MinDistance = 30, MaxDistance = 10000, VolumeBase = 1, VolumeVar, FrequencyVar;
}

/// <summary>
/// Plays original sounds by their xdb reference (e.g. a BasicSkelAnim's &lt;Sound href&gt;).
/// Audio data lives in bin/Sounds/&lt;uid&gt; (soundsfx.pak: MS IMA ADPCM WAV, some Ogg) and
/// bin/SoundsLoc/&lt;uid&gt; (sound.pak: localised Ogg speech); see docs/formats/sound.md.
/// </summary>
public static class SoundLibrary
{
    static readonly Dictionary<string, SoundDef?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly RandomNumberGenerator Rng = new();

    public static SoundDef? Load(string soundHref)
    {
        var path = XdbRef.Parse(soundHref).Path;
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        SoundDef? def = null;
        try { def = LoadUncached(path); }
        catch (Exception e) { GD.PushWarning($"[H5] sound {path}: {e.Message}"); }
        Cache[path] = def;
        return def;
    }

    static SoundDef? LoadUncached(string path)
    {
        var fs = Game.Instance.Fs;
        if (!fs.Exists(path)) return null;
        var x = Game.Instance.Xdb.Load(path).Root!;
        var uid = x.Element("uid")?.Value;
        if (string.IsNullOrEmpty(uid)) return null;
        byte[]? bytes = null;
        foreach (var dir in new[] { "bin/Sounds/", "bin/SoundsLoc/" })
            if (fs.TryReadAllBytes(dir + uid, out var b)) { bytes = b; break; }
        if (bytes == null) return null;

        float F(string tag, float d) => x.Element(tag) is { } e && !string.IsNullOrWhiteSpace(e.Value)
            ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        var def = new SoundDef
        {
            Loop = x.Element("Loop")?.Value == "true",
            Is3D = x.Element("Is3D")?.Value == "true",
            MinDistance = F("MinDistance", 30), MaxDistance = F("MaxDistance", 10000),
            VolumeBase = F("VolumeBase", 1), VolumeVar = F("VolumeVar", 0), FrequencyVar = F("FrequencyVar", 0),
        };
        def.Stream = Decode(bytes, def.Loop) ?? throw new InvalidDataException("unknown audio format");
        return def;
    }

    /// <summary>Turns raw original audio bytes (Ogg Vorbis or WAVE) into a Godot stream.</summary>
    public static AudioStream? Decode(byte[] bytes, bool loop)
    {
        if (bytes.Length >= 4 && bytes[0] == 'O' && bytes[1] == 'g' && bytes[2] == 'g' && bytes[3] == 'S')
        {
            var ogg = AudioStreamOggVorbis.LoadFromBuffer(bytes);
            if (ogg != null) ogg.Loop = loop;
            return ogg;
        }
        if (!ImaAdpcm.IsRiffWave(bytes)) return null;
        var pcm = ImaAdpcm.DecodeWav(bytes);
        var data = new byte[pcm.Samples.Length * 2];
        Buffer.BlockCopy(pcm.Samples, 0, data, 0, data.Length);
        var wav = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = pcm.SampleRate,
            Stereo = pcm.Channels == 2,
            Data = data,
        };
        if (loop) { wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward; wav.LoopEnd = pcm.FrameCount; }
        return wav;
    }

    /// <summary>
    /// Plays a sound once under <paramref name="parent"/> (3D sounds at its position) with the
    /// descriptor's random volume/pitch variation. Non-looping players free themselves.
    /// </summary>
    public static Node? Play(string soundHref, Node parent)
    {
        if (Load(soundHref) is not { } def) return null;
        float volume = Math.Max(0.001f, def.VolumeBase + Rng.RandfRange(-def.VolumeVar, def.VolumeVar));
        float pitch = Math.Max(0.1f, 1 + Rng.RandfRange(-def.FrequencyVar, def.FrequencyVar));
        Node player;
        if (def.Is3D && parent is Node3D)
        {
            var p3 = new AudioStreamPlayer3D
            {
                Stream = def.Stream, VolumeDb = Mathf.LinearToDb(volume), PitchScale = pitch,
                UnitSize = def.MinDistance, MaxDistance = def.MaxDistance, Autoplay = true,
            };
            if (!def.Loop) p3.Finished += p3.QueueFree;
            player = p3;
        }
        else
        {
            var p2 = new AudioStreamPlayer { Stream = def.Stream, VolumeDb = Mathf.LinearToDb(volume), PitchScale = pitch, Autoplay = true };
            if (!def.Loop) p2.Finished += p2.QueueFree;
            player = p2;
        }
        parent.AddChild(player);
        return player;
    }
}
