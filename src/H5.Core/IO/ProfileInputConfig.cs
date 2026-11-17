namespace H5.Core.IO;

/// <summary>
/// Where a profile's key bindings come from, as H5_Game.exe 3.1 does it. The exe has no built-in bindings and
/// the data paks hold no cfg: every key comes from executing an <c>input_a2.cfg</c> on disk.
/// <list type="bullet">
/// <item>New profile (00834a30): CopyFileA(Profiles\default_profile\user_a2.cfg, &lt;profile&gt;\user_a2.cfg) and the
/// same for input_a2.cfg — the profile starts with the install's stock files.</item>
/// <item>Start-up (008352a0): when the profile's user_a2.cfg exists, 00834200 executes the
/// profile's user_a2.cfg, "unbindall", the profile's input_a2.cfg, "bind_update"; otherwise it switches to
/// default_profile (00834690) and executes that folder's files.</item>
/// <item>Profile switch (00835530): find-or-create (00834a30), then 00834200 for the selected profile.</item>
/// <item>A missing file executes nothing (00c12930 opens it, size 0 → no commands): no bindings at all.</item>
/// </list>
/// Here a profile is a folder in Godot user:// (IndexedDB in the browser); the install's default_profile is the
/// copy source and the fallback.
/// </summary>
public static class ProfileInputConfig
{
    public const string InputFile = "input_a2.cfg";
    public const string UserFile = "user_a2.cfg";
    static readonly string[] Stock = { UserFile, InputFile };

    /// <summary>The install's default_profile copy of <paramref name="file"/>, or null.</summary>
    public static string? InstallDefault(string? installRoot, string file) =>
        string.IsNullOrEmpty(installRoot) ? null : InstallPath.ResolveFile(installRoot, "profiles", "default_profile", file);

    /// <summary>
    /// 00834a30's copies: each stock file the profile lacks is copied from the install's default_profile
    /// (CopyFileA with bFailIfExists = 0 on a new profile; an existing profile copy is never overwritten here).
    /// Returns the files copied.
    /// </summary>
    public static List<string> CopyStockFiles(string? installRoot, string profileDir)
    {
        var copied = new List<string>();
        foreach (var f in Stock)
        {
            var src = InstallDefault(installRoot, f);
            var dst = Path.Combine(profileDir, f);
            if (src == null || File.Exists(dst)) continue;
            try
            {
                Directory.CreateDirectory(profileDir);
                File.Copy(src, dst, overwrite: false);
                copied.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return copied;
    }

    /// <summary>
    /// The input_a2.cfg the exe executes for this profile: the profile's own when its user_a2.cfg exists
    /// (008352a0 → 00834200), otherwise default_profile's (00834690). Null when there is none (no bindings).
    /// </summary>
    public static string? Resolve(string? installRoot, string? profileDir)
    {
        if (profileDir != null && File.Exists(Path.Combine(profileDir, UserFile)))
        {
            var own = Path.Combine(profileDir, InputFile);
            return File.Exists(own) ? own : null;
        }
        return InstallDefault(installRoot, InputFile);
    }
}
