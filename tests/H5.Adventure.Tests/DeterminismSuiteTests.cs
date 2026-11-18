using System.Text.RegularExpressions;
using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>
/// The cross-platform reference: <see cref="DeterminismSuite"/>'s recorded results are what desktop x64 computes. The
/// browser build checks the same cases in the page (tools/web/harness/smoke.mjs --only determinism). A rules change
/// that moves a hash on purpose re-records it; the browser then has to reproduce the new value.
/// </summary>
[Collection("adv")]
public class DeterminismSuiteTests(Fixture f)
{
    static string Target => DataFixture.Mmh55 ? "mmh55" : "stock";

    public static IEnumerable<object[]> Battles() => DeterminismSuite.Cases.Select(c => new object[] { c.Name });
    public static IEnumerable<object[]> Adventures() => DeterminismSuite.AdvCases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Battles))]
    public void Battle(string name) =>
        Assert.Equal(DeterminismSuite.Expected[Target][name], DeterminismSuite.Run(f.Db, DeterminismSuite.Cases.Single(c => c.Name == name)));

    [Theory]
    [MemberData(nameof(Adventures))]
    public void AdventureDays(string name)
    {
        var c = DeterminismSuite.AdvCases.Single(x => x.Name == name);
        var map = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, c.Map)!);
        Assert.Equal(DeterminismSuite.Expected[Target][name], DeterminismSuite.Run(f.Db, map, c));
    }

    [Fact]
    public void MathFunctions()
    {
        foreach (var (name, hash) in DeterminismSuite.MathProbes())
            Assert.True(DeterminismSuite.ExpectedMath[name] == hash, $"{name}: {hash}");
    }

    static readonly Regex PlatformMath =
        new(@"\bMathF?\.(Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Exp|Log|Log10|Log2|Pow|Cbrt|Sinh|Cosh|Tanh|Asinh|Acosh|Atanh|SinCos)\s*\(");

    /// <summary>
    /// Rules code must not call the platform math library (Math / MathF Sin, Cos, Atan2, Log*, Pow …): its last bits differ
    /// between desktop and the browser build (DeterminismSuite.MathProbes). <c>H5.Core.Numerics.DetMath</c> has portable
    /// ones. Presentation-only code (the adventure camera) may.
    /// </summary>
    [Fact]
    public void RulesUsePortableMath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        Assert.True(Directory.Exists(Path.Combine(root, "src", "H5.Combat")), root);
        string[] projects = { "H5.Core", "H5.Combat", "H5.Adventure", "H5.Rmg", "H5.Scripting", "H5.Duel", "H5.AdvNet", "H5.Save" };
        string[] presentation = { "MissionCamera.cs", "DetMath.cs" };
        var sep = Path.DirectorySeparatorChar;
        var hits = new List<string>();
        foreach (var p in projects)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}") || presentation.Contains(Path.GetFileName(file))) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (PlatformMath.IsMatch(lines[i])) hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }
}
