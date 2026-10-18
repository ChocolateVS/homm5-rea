using System.Globalization;
using Godot;
using H5.Assets.Audio;
using H5Reborn.Options;

namespace H5Reborn.Audio;

/// <summary>
/// Shared audio state: the original sound-routing data, the extra buses and the UI sound
/// hooks. The original has only two volume sliders (sound_musicvolume, sound_sfxvolume) and
/// an ambience switch (sound_ambient_enable), so the Ambient and Speech buses send into the
/// SFX bus: the stock sliders keep controlling everything exactly as before.
/// </summary>
public static class AudioSystem
{
    public const string AmbientBus = "Ambient";
    public const string SpeechBus = "Speech";

    static MusicData? _data;
    static bool _installed;
    static float? _ambientFade;

    public static MusicData Data => _data ??= new MusicData(Game.Instance.Xdb);

    /// <summary>Set H5_AUDIO_LOG=1 (or --audio-log) to log every music/ambient change.</summary>
    public static bool Log { get; } = OS.GetEnvironment("H5_AUDIO_LOG") == "1" || OS.GetCmdlineUserArgs().Contains("--audio-log");

    public static void Trace(string msg)
    {
        if (Log) GD.Print($"[H5][audio] {msg}");
    }

    /// <summary>
    /// Crossfade between ambient sets: setvar sound_ambient_fade_duration (seconds), which the
    /// exe registers with a default of 2.0 (FUN_00b6b840) and the stock autoexec_a2.cfg sets to 2.
    /// </summary>
    public static float AmbientFadeSeconds => _ambientFade ??= ReadSetvar("sound_ambient_fade_duration", 2f);

    static float ReadSetvar(string name, float fallback)
    {
        try
        {
            var cfg = Path.Combine(Game.Instance.Install.Root, "profiles", "autoexec_a2.cfg");
            if (!File.Exists(cfg)) return fallback;
            foreach (var line in File.ReadLines(cfg))
            {
                var t = line.Trim();
                if (!t.StartsWith("setvar ") || !t.Contains(name)) continue;
                var eq = t.IndexOf('=');
                if (eq > 0 && float.TryParse(t[(eq + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            }
        }
        catch (IOException) { }
        return fallback;
    }

    /// <summary>Idempotent: buses, UI sound hooks and the music manager node.</summary>
    public static void EnsureInstalled()
    {
        if (_installed || Game.Instance == null) return;
        _installed = true;
        EnsureSubBus(AmbientBus);
        EnsureSubBus(SpeechBus);
        ApplyAmbientSwitch();
        OptionsStore.Changed += ApplyAmbientSwitch;

        // Stock UI sounds: uiconsts ButtonClickSound / DisableButtonClickSound for every button
        // press, and (UISPlaySound) commands written into window scripts.
        var click = Data.UiConstSound("ButtonClickSound");
        var disabled = Data.UiConstSound("DisableButtonClickSound");
        Ui.UiAudio.Click = node =>
        {
            var href = node.Window.Enabled ? click : disabled;
            if (href != null) SoundLibrary.PlayUi(href);
        };
        Ui.UiAudio.PlaySound = href => { if (!string.IsNullOrEmpty(href)) SoundLibrary.PlayUi(href); };

        MusicManager.Create();
    }

    static void EnsureSubBus(string name)
    {
        int i = AudioServer.GetBusIndex(name);
        if (i < 0)
        {
            AudioServer.AddBus();
            i = AudioServer.BusCount - 1;
            AudioServer.SetBusName(i, name);
        }
        AudioServer.SetBusSend(i, AudioBuses.For(AudioBuses.Sfx));
    }

    static void ApplyAmbientSwitch()
    {
        int i = AudioServer.GetBusIndex(AmbientBus);
        if (i >= 0) AudioServer.SetBusMute(i, _ambientStops > 0 || !OptionsStore.GetBool("sound_ambient_enable", true));
    }

    /// <summary>
    /// Background music / ambience volume factors while an adventure-map dialog runs
    /// (map.xdb dialogs/Item BackgroundMusicVolume / BackgroundAmbientVolume, percent; the
    /// dialog serializer at 00a0da40). 1 outside dialogs.
    /// </summary>
    public static float MusicScale = 1f, AmbientScale = 1f;

    static int _ambientStops;

    /// <summary>
    /// Silences ambience while a dialog-scene sentence with StopAmbient is in force (nested
    /// requests count); <see cref="ReleaseAmbient"/> undoes one request.
    /// </summary>
    public static void StopAmbient() { EnsureInstalled(); _ambientStops++; ApplyAmbientSwitch(); }

    public static void ReleaseAmbient() { _ambientStops = Math.Max(0, _ambientStops - 1); ApplyAmbientSwitch(); }

    /// <summary>
    /// Plays a UIGameRoot named music once over the current music (CNonLoopMusicPlayer:
    /// HERO_FIND_GRAIL_MUSIC, BUILD_GRAIL_MUSIC — FUN_005a5d90, FUN_00615230).
    /// </summary>
    public static void PlayNamedMusicOnce(string id)
    {
        EnsureInstalled();
        Trace($"music once {id}");
        MusicManager.PlayOnce(id, Data.UiMusic(id));
    }

    /// <summary>Plays a UIGameRoot named sound (HERO_LEVEL_UP_SOUND, NEW_WEEK_GOOD_SOUND…).</summary>
    public static void PlayNamed(string id)
    {
        EnsureInstalled();
        if (Data.UiSound(id) is { } href) { Trace($"sound {id}"); SoundLibrary.PlayUi(href); }
    }
}
