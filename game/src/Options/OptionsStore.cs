using System.Globalization;
using Godot;
using H5Reborn.Settings;

namespace H5Reborn.Options;

/// <summary>
/// The original global option variables (setvar names), kept per profile in
/// user://profiles/&lt;name&gt;/options.cfg like the stock profiles\&lt;name&gt;\user_a2.cfg.
/// Values start from the stock default profile (profiles\default_profile\user_a2.cfg as
/// shipped). Video variables are mirrored into <see cref="VideoSettings"/>, which owns
/// the window; sound volumes drive the Music/SFX audio buses.
/// Game code reads options with <see cref="GetFloat"/>/<see cref="GetBool"/>.
/// </summary>
public static class OptionsStore
{
    /// <summary>Stock default_profile/user_a2.cfg values for the variables the options screen edits.</summary>
    static readonly Dictionary<string, string> StockDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Options.Tooltips.HoverTimeout"] = "1.0",
        ["adv_camera_mode"] = "0",
        ["adventure_quick_combat"] = "0",
        ["adventure_speed_ai"] = "2",
        ["adventure_speed_human"] = "1",
        ["app_always_active"] = "0",
        ["autosave"] = "1",
        ["camera_cinematic"] = "1",
        ["combat_ai_use_magic_for_human"] = "0",
        ["combat_dynamic_mode"] = "0",
        ["combat_show_grid"] = "0",
        ["combat_show_movement_shade"] = "1",
        ["combat_show_target_shade"] = "1",
        ["combat_time_factor"] = "1",
        ["combat_turn_speed"] = "0",
        ["game_scrollsensitivity"] = "1",
        ["gfx_anisotropic_filter"] = "0",
        ["gfx_fsaa"] = "0",
        ["gfx_fullscreen"] = "1",
        ["gfx_gamma"] = "1",
        ["gfx_quality"] = "0",
        ["gfx_resolution"] = "1920x1080",
        ["gfx_water_reflection"] = "1",
        ["h5_simple_render"] = "0",
        ["invert_camera"] = "1",
        ["movement_reminder"] = "1",
        ["prefer_special_hero_trail"] = "0",
        ["save_before_combat"] = "0",
        ["show_movement_path"] = "1",
        ["sound_ambient_enable"] = "1",
        ["sound_musicvolume"] = "0.4",
        ["sound_sfxvolume"] = "0.5",
        ["spellbook_animation"] = "0",
        ["town_enable_flyby"] = "1",
        ["tutorial_show"] = "1",
        ["ui_use_hwcursor"] = "0",
        ["play_sounds"] = "1",
    };

    static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    static string? _loadedFor;

    public static event Action? Changed;

    static string FilePath => $"{Profiles.ProfileManager.CurrentDir}/options.cfg";

    static void EnsureLoaded()
    {
        var profile = Profiles.ProfileManager.CurrentName;
        if (_loadedFor == profile) return;
        _loadedFor = profile;
        _values.Clear();
        foreach (var (k, v) in StockDefaults) _values[k] = v;
        var cfg = new ConfigFile();
        if (cfg.Load(FilePath) == Error.Ok && cfg.HasSection("vars"))
            foreach (var key in cfg.GetSectionKeys("vars"))
                _values[key] = cfg.GetValue("vars", key).AsString();
        // The window's own settings live in VideoSettings; show them as the stock vars.
        _values["gfx_resolution"] = $"{VideoSettings.Resolution.X}x{VideoSettings.Resolution.Y}";
        _values["gfx_fullscreen"] = VideoSettings.Mode == VideoSettings.DisplayMode.Fullscreen ? "1" : "0";
    }

    public static string Get(string var, string fallback = "0")
    {
        EnsureLoaded();
        return _values.TryGetValue(var, out var v) ? v : fallback;
    }

    public static float GetFloat(string var, float fallback = 0) =>
        float.TryParse(Get(var, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public static bool GetBool(string var, bool fallback = false) => Get(var, fallback ? "1" : "0") is var s && s != "0" && s != "";

    public static void Set(string var, string value)
    {
        EnsureLoaded();
        _values[var] = value;
    }

    public static void Set(string var, float value) => Set(var, value.ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>Writes the profile's options and applies them (window, audio).</summary>
    public static void Save()
    {
        EnsureLoaded();
        var cfg = new ConfigFile();
        foreach (var (k, v) in _values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) cfg.SetValue("vars", k, v);
        DirAccess.MakeDirRecursiveAbsolute(Profiles.ProfileManager.CurrentDir);
        cfg.Save(FilePath);
        Apply();
    }

    /// <summary>Applies the options that have an effect outside the screens that read them.</summary>
    public static void Apply()
    {
        EnsureLoaded();
        AudioBuses.Apply(GetFloat("sound_musicvolume", 0.4f), GetFloat("sound_sfxvolume", 0.5f), GetBool("play_sounds", true));
        var res = Get("gfx_resolution").Split('x');
        if (res.Length == 2 && int.TryParse(res[0], out var w) && int.TryParse(res[1], out var h))
        {
            var mode = GetBool("gfx_fullscreen", true) ? VideoSettings.DisplayMode.Fullscreen
                : VideoSettings.Mode == VideoSettings.DisplayMode.Fullscreen ? VideoSettings.DisplayMode.Windowed : VideoSettings.Mode;
            if (VideoSettings.Resolution != new Vector2I(w, h) || VideoSettings.Mode != mode)
            {
                VideoSettings.Resolution = new Vector2I(w, h);
                VideoSettings.Mode = mode;
                VideoSettings.Apply();
                VideoSettings.Save();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Called when the profile changes: forget cached values so the next read loads the new profile.</summary>
    public static void Reload()
    {
        _loadedFor = null;
        Apply();
    }
}

/// <summary>
/// Music and sound-effect buses. The original has separate music and sound volumes
/// (sound_musicvolume, sound_sfxvolume); players created by SoundLibrary and the music
/// player route to these buses.
/// </summary>
public static class AudioBuses
{
    public const string Music = "Music";
    public const string Sfx = "SFX";

    static int Ensure(string name)
    {
        int i = AudioServer.GetBusIndex(name);
        if (i >= 0) return i;
        AudioServer.AddBus();
        i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name);
        AudioServer.SetBusSend(i, "Master");
        return i;
    }

    /// <summary>Bus name for a player: creates the bus on first use so routing never fails.</summary>
    public static string For(string name) { Ensure(name); return name; }

    public static void Apply(float music, float sfx, bool soundsOn)
    {
        int m = Ensure(Music), s = Ensure(Sfx);
        AudioServer.SetBusVolumeDb(m, Mathf.LinearToDb(Math.Max(music, 0.0001f)));
        AudioServer.SetBusMute(m, music <= 0.0001f);
        AudioServer.SetBusVolumeDb(s, Mathf.LinearToDb(Math.Max(sfx, 0.0001f)));
        AudioServer.SetBusMute(s, sfx <= 0.0001f || !soundsOn);
    }
}
