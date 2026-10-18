namespace H5.Save;

/// <summary>Where save files live: a directory on disk, or Godot's user:// (desktop and browser).</summary>
public interface ISaveStorage
{
    IEnumerable<string> List();
    bool Exists(string fileName);
    byte[] Read(string fileName);
    void Write(string fileName, byte[] data);
    void Delete(string fileName);
}

public sealed class DirectorySaveStorage(string directory) : ISaveStorage
{
    public string Directory { get; } = directory;

    public IEnumerable<string> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.sav").Select(Path.GetFileName).OfType<string>()
            : Enumerable.Empty<string>();

    public bool Exists(string fileName) => File.Exists(Path.Combine(Directory, fileName));
    public byte[] Read(string fileName) => File.ReadAllBytes(Path.Combine(Directory, fileName));

    public void Write(string fileName, byte[] data)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Write-then-rename so a crash never leaves a half-written save over a good one.
        var path = Path.Combine(Directory, fileName);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string fileName) => File.Delete(Path.Combine(Directory, fileName));
}

/// <summary>One entry of the saves list.</summary>
public sealed record SaveEntry(string FileName, SaveHeader Header);

/// <summary>
/// Save file naming and listing, following H5_Game.exe: files are "save_N.sav" with N
/// the first unused number counting from 0 (FUN_007ea770 probes save_%d.sav from 0 up);
/// the player-visible name is stored inside. Quicksave/quickload use the save whose name
/// is UI/Common/defaultsavename.txt ("quick", FUN_007e7500); autosaves are named from
/// UI/Common/autosavename.txt ("Autosave &lt;value=id&gt;").
/// </summary>
public static class SaveSlots
{
    public const string FilePattern = "save_{0}.sav";

    public static string NextFileName(ISaveStorage storage)
    {
        for (int i = 0; ; i++)
        {
            var name = string.Format(FilePattern, i);
            if (!storage.Exists(name)) return name;
        }
    }

    /// <summary>All readable saves, newest first. Damaged files are listed with <paramref name="damaged"/>.</summary>
    public static List<SaveEntry> List(ISaveStorage storage, List<string>? damaged = null)
    {
        var result = new List<SaveEntry>();
        foreach (var file in storage.List())
        {
            try { result.Add(new SaveEntry(file, SaveArchive.ReadHeader(storage.Read(file)))); }
            catch (Exception e) when (e is SaveCorruptException or IOException or System.Text.Json.JsonException)
            {
                damaged?.Add(file);
            }
        }
        return result.OrderByDescending(e => e.Header.SavedUtc).ThenBy(e => e.FileName, StringComparer.Ordinal).ToList();
    }

    public static SaveEntry? FindByName(ISaveStorage storage, string name) =>
        List(storage).FirstOrDefault(e => string.Equals(e.Header.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a save under its header name: an existing save with the same name is
    /// overwritten in place (the caller confirms first), otherwise a new save_N.sav is used.
    /// Returns the file name written.
    /// </summary>
    public static string Write(ISaveStorage storage, SaveArchive save)
    {
        var file = FindByName(storage, save.Header.Name)?.FileName ?? NextFileName(storage);
        storage.Write(file, save.ToBytes());
        return file;
    }

    /// <summary>Formats an original text template such as "Autosave &lt;value=id&gt;".</summary>
    public static string Format(string template, string id) =>
        template.Replace("<value=id>", id, StringComparison.OrdinalIgnoreCase).Trim();
}
