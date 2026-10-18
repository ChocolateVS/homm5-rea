using Godot;

namespace H5Reborn.Profiles;

/// <summary>
/// Player profiles, like the original's profiles\&lt;name&gt;\ folders (H5_Game.exe keeps
/// saves in "profiles\&lt;name&gt;\Saves\", options in the profile's cfg, campaign progress
/// and hall of fame per profile). Here every profile is user://profiles/&lt;name&gt;/, which
/// also works in the browser build. The selected profile is remembered in
/// user://profiles/current.cfg.
/// </summary>
public static class ProfileManager
{
    const string Root = "user://profiles";
    const string CurrentFile = Root + "/current.cfg";
    /// <summary>Name used before any profile exists (and by the campaign data written before profiles).</summary>
    public const string DefaultName = "Player";
    public const int MaxNameLength = 24;

    static string? _current;

    public static event Action? Changed;

    public static string CurrentName
    {
        get
        {
            if (_current != null) return _current;
            var cfg = new ConfigFile();
            if (cfg.Load(CurrentFile) == Error.Ok && cfg.GetValue("profile", "name", "").AsString() is { Length: > 0 } n && Exists(n))
                _current = n;
            else
                _current = List().FirstOrDefault() ?? DefaultName;
            return _current;
        }
    }

    public static string Dir(string name) => $"{Root}/{name}";
    public static string SavesDir(string name) => $"{Dir(name)}/saves";
    public static string CurrentDir => Dir(CurrentName);

    /// <summary>True when no profile has been created yet (the original then asks for a first name).</summary>
    public static bool NoProfiles => List().Count == 0;

    public static List<string> List()
    {
        var result = new List<string>();
        using var d = DirAccess.Open(Root);
        if (d == null) return result;
        foreach (var name in d.GetDirectories()) result.Add(name);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static bool Exists(string name) => DirAccess.DirExistsAbsolute(Dir(name));

    /// <summary>Validates a new profile name; returns null when acceptable.</summary>
    public static string? Validate(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "empty";
        if (name.Length > MaxNameLength) return "too long";
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '.' }) >= 0) return "invalid characters";
        if (List().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return "exists";
        return null;
    }

    public static bool Create(string name)
    {
        name = name.Trim();
        if (Validate(name) != null) return false;
        return DirAccess.MakeDirRecursiveAbsolute(SavesDir(name)) == Error.Ok;
    }

    public static void Select(string name)
    {
        if (!Exists(name)) DirAccess.MakeDirRecursiveAbsolute(SavesDir(name));
        _current = name;
        var cfg = new ConfigFile();
        cfg.SetValue("profile", "name", name);
        DirAccess.MakeDirRecursiveAbsolute(Root);
        cfg.Save(CurrentFile);
        Changed?.Invoke();
    }

    /// <summary>Deletes a profile folder (saves, options, progress). The selected profile moves to another one.</summary>
    public static void Delete(string name)
    {
        if (!Exists(name)) return;
        RemoveTree(Dir(name));
        if (name.Equals(_current, StringComparison.OrdinalIgnoreCase))
        {
            _current = null;
            var next = List().FirstOrDefault();
            if (next != null) Select(next);
            else Changed?.Invoke();
        }
    }

    /// <summary>Recursive delete restricted to user://profiles/.</summary>
    static void RemoveTree(string path)
    {
        if (!path.StartsWith(Root + "/", StringComparison.Ordinal) || path.Contains("..")) return;
        using (var d = DirAccess.Open(path))
        {
            if (d == null) return;
            foreach (var f in d.GetFiles()) d.Remove(f);
            foreach (var sub in d.GetDirectories()) RemoveTree($"{path}/{sub}");
        }
        DirAccess.RemoveAbsolute(path);
    }
}
