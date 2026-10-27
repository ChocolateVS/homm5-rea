namespace H5.Adventure.Engine;

// The map's random artifact generator, exe CArtifactsGenerator (vtables 00e651d8 / 00e651b8 / 00e65198).
public sealed partial class Adventure
{
    /// <summary>Artifact class index of the generator (artifact def +0x24): 0 minor, 1 major, 2 relic.</summary>
    public static int ArtifactClassIndex(string cls) => cls switch
    {
        "ARTF_CLASS_MINOR" => 0,
        "ARTF_CLASS_MAJOR" => 1,
        "ARTF_CLASS_RELIC" => 2,
        _ => -1,
    };

    static readonly string[] ClassNames = { "ARTF_CLASS_MINOR", "ARTF_CLASS_MAJOR", "ARTF_CLASS_RELIC" };

    /// <summary>
    /// The generator's per-class lists (009cf6c0, built by world vfunc 0x3c = 00991830 → 009cfa30): the map's
    /// artifactIDs in their order, or every artifact of the table when the list is empty; only classes 0..2 are
    /// kept (the grail class is not).
    /// </summary>
    List<string>[] ArtifactPools()
    {
        if (_artifactPools != null) return _artifactPools;
        var pools = new[] { new List<string>(), new List<string>(), new List<string>() };
        IEnumerable<string> ids = Map.ArtifactIds.Count > 0 ? Map.ArtifactIds : Db.ArtifactClass.Keys;
        foreach (var id in ids)
            if (Db.ArtifactClass.TryGetValue(id, out var cls) && ArtifactClassIndex(cls) is var c and >= 0 && !pools[c].Contains(id))
                pools[c].Add(id);
        return _artifactPools = pools;
    }
    List<string>[]? _artifactPools;

    /// <summary>
    /// 009cf360 (interface slot 4, reached through slot 3 = 009cf8b0 with an empty exclusion list): classes lo..hi
    /// (lo clamped to ≥ 0, hi to ≤ 2); candidates are the listed artifacts whose def +0x3c (CanBeGeneratedToSell)
    /// is set. A first pass takes only those not generated yet (count +4 &lt; 1); when none is left a second pass
    /// takes them all. One is drawn uniformly (008e7840) and its count rises. Null when no candidate exists.
    /// </summary>
    public string? GenerateArtifact(int lo, int hi)
    {
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, 2);
        var pools = ArtifactPools();
        for (int pass = 0; pass < 2; pass++)
        {
            var cand = new List<string>();
            for (int c = lo; c <= hi; c++)
                foreach (var id in pools[c])
                    if (!Db.ArtifactsNotForSale.Contains(id) && (pass != 0 || S.ArtifactsGenerated.GetValueOrDefault(id) < 1))
                        cand.Add(id);
            if (cand.Count == 0) continue;
            var pick = cand[S.Rng.Range(0, cand.Count - 1)];
            S.ArtifactsGenerated[pick] = S.ArtifactsGenerated.GetValueOrDefault(pick) + 1;
            return pick;
        }
        return null;
    }

    /// <summary>The generator by class name (null = any class, 009cf1e0 case 3: 0..2).</summary>
    string? GenerateArtifact(string? cls) =>
        cls == null ? GenerateArtifact(0, 2) : ArtifactClassIndex(cls) is var c and >= 0 ? GenerateArtifact(c, c) : null;

    public static string ArtifactClassName(int c) => ClassNames[c];
}
