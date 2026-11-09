using Godot;

namespace H5Reborn.Web;

/// <summary>
/// Web-build-only autoload (tools/web/build.sh): keeps user:// (profiles, saves, settings) in the browser's IndexedDB.
/// Godot's web platform mounts user:// as Emscripten IDBFS and writes it back after a Godot FileAccess write; files
/// the game writes with System.IO (and new folders) do not trigger that, so this node watches the folder and, after
/// any change, makes one tiny Godot write, which schedules the same write-back. Export / import: wwwroot/h5saves.js.
/// </summary>
public partial class WebStorage : Node
{
    const string Marker = "user://.h5sync";
    const double Interval = 2;
    string _root = "";
    long _signature;
    double _since;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        WebDecodedCache.Install();
        _root = OS.GetUserDataDir();
        _signature = Signature(out int files);
        GD.Print($"[H5] web: user data {_root}: {files} files{(files > 0 ? ": " + string.Join(", ", List().Take(40)) : "")}");
    }

    public override void _Process(double delta)
    {
        if ((_since += delta) < Interval) return;
        _since = 0;
        var sig = Signature(out int files);
        if (sig == _signature) return;
        using (var f = Godot.FileAccess.Open(Marker, Godot.FileAccess.ModeFlags.Write)) f?.StoreString(DateTime.UtcNow.ToString("O"));
        _signature = Signature(out files);
        GD.Print($"[H5] web: user data changed ({files} files), saved to browser storage");
    }

    IEnumerable<string> List() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).Where(p => p != ".h5sync").Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    /// <summary>Hash of every entry's path, size and write time (the marker file excluded).</summary>
    /// <remarks>Walks the folder with Godot's DirAccess: System.IO's directory enumeration cost ~40 ms per scan in the
    /// browser build even for an empty folder (a CPU profile of the adventure map: 2 % of the frame time).</remarks>
    long Signature(out int files)
    {
        files = 0;
        if (!DirAccess.DirExistsAbsolute(_root)) return 0;
        var h = new HashCode();
        var dirs = new Stack<string>();
        dirs.Push(_root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            foreach (var name in DirAccess.GetDirectoriesAt(dir))
            {
                var p = dir + "/" + name;
                h.Add(p);
                dirs.Push(p);
            }
            foreach (var name in DirAccess.GetFilesAt(dir))
            {
                if (name == ".h5sync") continue;
                var p = dir + "/" + name;
                h.Add(p);
                h.Add(Godot.FileAccess.GetModifiedTime(p));
                h.Add(Godot.FileAccess.GetSize(p));
                files++;
            }
        }
        return h.ToHashCode();
    }
}
