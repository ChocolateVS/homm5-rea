namespace H5.Core.IO;

/// <summary>
/// Resolves paths to loose files of the original install (profiles, Maps, UserMODs,
/// video, Music, hwcursors…). The original game was written for Windows, so its own
/// references ignore case and use backslashes; on Linux (Wine/Proton/GOG copies) the
/// folder and file names on disk keep whatever case the installer used. Every segment
/// is therefore matched exactly first, then case-insensitively.
/// </summary>
public static class InstallPath
{
    /// <summary>Case-insensitive enumeration options for wildcard searches like "*.h5u".</summary>
    public static readonly EnumerationOptions IgnoreCase = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Resolves <paramref name="parts"/> (each may itself contain '/' or '\', "." and "..")
    /// under <paramref name="root"/> to an existing file or directory, or null.
    /// </summary>
    public static string? Resolve(string root, params string[] parts)
    {
        var segs = Segments(parts);
        var cur = root;
        for (int i = 0; i < segs.Count; i++)
        {
            var exact = Path.Combine(cur, segs[i]);
            bool last = i == segs.Count - 1;
            if (last ? File.Exists(exact) || Directory.Exists(exact) : Directory.Exists(exact)) { cur = exact; continue; }
            if (!Directory.Exists(cur)) return null;
            string? match;
            try
            {
                var entries = last ? Directory.EnumerateFileSystemEntries(cur) : Directory.EnumerateDirectories(cur);
                match = entries.FirstOrDefault(p => string.Equals(Path.GetFileName(p), segs[i], StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            if (match == null) return null;
            cur = match;
        }
        return segs.Count == 0 ? (Directory.Exists(root) ? root : null) : cur;
    }

    /// <summary>Like <see cref="Resolve"/> but only returns existing files.</summary>
    public static string? ResolveFile(string root, params string[] parts) =>
        Resolve(root, parts) is { } p && File.Exists(p) ? p : null;

    /// <summary>Like <see cref="Resolve"/> but only returns existing directories.</summary>
    public static string? ResolveDirectory(string root, params string[] parts) =>
        Resolve(root, parts) is { } p && Directory.Exists(p) ? p : null;

    /// <summary>The resolved path when it exists, otherwise the plain combination (for messages/creation).</summary>
    public static string ResolveOrCombine(string root, params string[] parts) =>
        Resolve(root, parts) ?? Path.Combine(new[] { root }.Concat(Segments(parts)).ToArray());

    /// <summary>Files in a directory matching a wildcard, ignoring case on every platform.</summary>
    public static IEnumerable<string> EnumerateFiles(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, IgnoreCase) : Enumerable.Empty<string>();

    static List<string> Segments(string[] parts)
    {
        var segs = new List<string>();
        foreach (var part in parts)
            foreach (var s in (part ?? "").Split('/', '\\'))
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); continue; }
                segs.Add(s);
            }
        return segs;
    }
}
