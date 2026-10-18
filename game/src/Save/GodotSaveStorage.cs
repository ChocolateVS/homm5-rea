using Godot;
using H5.Save;

namespace H5Reborn.Save;

/// <summary>Save files in a Godot user:// folder (desktop: %APPDATA%\Godot\app_userdata\H5Reborn; browser: IndexedDB).</summary>
public sealed class GodotSaveStorage(string dir) : ISaveStorage
{
    public string Dir { get; } = dir;

    public IEnumerable<string> List()
    {
        using var d = DirAccess.Open(Dir);
        if (d == null) return Array.Empty<string>();
        return d.GetFiles().Where(f => f.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool Exists(string fileName) => Godot.FileAccess.FileExists($"{Dir}/{fileName}");

    public byte[] Read(string fileName) =>
        Godot.FileAccess.GetFileAsBytes($"{Dir}/{fileName}") is { Length: > 0 } b ? b
            : throw new IOException($"cannot read {fileName}: {Godot.FileAccess.GetOpenError()}");

    public void Write(string fileName, byte[] data)
    {
        DirAccess.MakeDirRecursiveAbsolute(Dir);
        // Write-then-rename so an interrupted save never replaces a good one.
        var path = $"{Dir}/{fileName}";
        var tmp = path + ".tmp";
        using (var f = Godot.FileAccess.Open(tmp, Godot.FileAccess.ModeFlags.Write)
                       ?? throw new IOException($"cannot write {fileName}: {Godot.FileAccess.GetOpenError()}"))
            f.StoreBuffer(data);
        using var d = DirAccess.Open(Dir);
        if (d!.FileExists(fileName)) d.Remove(fileName);
        d.Rename(fileName + ".tmp", fileName);
    }

    public void Delete(string fileName)
    {
        using var d = DirAccess.Open(Dir);
        d?.Remove(fileName);
    }
}
