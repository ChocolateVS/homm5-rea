namespace H5.Assets.Audio;

// Music selection, ported from the original's NSound music calculators (H5_Game.exe).
// A calculator answers "which track next?" each time the player finishes a track and its
// random pause. Screens build calculator trees from game data (TableRaceMusic, UIGameRoot
// musics, AdvMapTile.AdventureMusic, hero/campaign music lists). See docs/formats/sound.md.

/// <summary>A Sounds/_(Music) descriptor. Times are milliseconds, as in the data.</summary>
public sealed record MusicTrack(string Path, string FileName, int FadeInMs, int FadeOutMs, int MinPausedMs, int MaxPausedMs);

public interface IMusicCalculator
{
    /// <summary>The next track to play, or null when the calculator has nothing to offer.</summary>
    MusicTrack? Next(Random rng);
}

/// <summary>CConstantMusicCalculator (vtable 00e47b28): always the same track (may be null).</summary>
public sealed class ConstantMusicCalculator(MusicTrack? track) : IMusicCalculator
{
    public MusicTrack? Track { get; } = track;
    public MusicTrack? Next(Random rng) => Track;
}

/// <summary>
/// Calculators whose answer comes from live game state: CAdvMapHeroMusicCalculator
/// (008164e0, the selected hero's AdventureMusic) and CTerrainMusicCalculator (00816570,
/// the AdventureMusic of the tile under the selected hero).
/// </summary>
public sealed class FuncMusicCalculator(Func<MusicTrack?> query) : IMusicCalculator
{
    public MusicTrack? Next(Random rng) => query();
}

/// <summary>
/// CCompositeMusicCalculator (Next at 00819ae0): asks every child, then picks uniformly at
/// random among the non-null answers, never letting the same track play a third time in a
/// row (the exe counts consecutive plays and re-draws without that track once it reaches 2).
/// </summary>
public sealed class CompositeMusicCalculator : IMusicCalculator
{
    readonly List<IMusicCalculator> _children;
    MusicTrack? _last;
    int _lastCount;

    public CompositeMusicCalculator(IEnumerable<IMusicCalculator?> children) =>
        _children = children.Where(c => c != null).Select(c => c!).ToList();

    public static CompositeMusicCalculator Of(IEnumerable<MusicTrack?> tracks) =>
        new(tracks.Where(t => t != null).Select(t => (IMusicCalculator)new ConstantMusicCalculator(t)));

    public int Count => _children.Count;

    public MusicTrack? Next(Random rng)
    {
        var candidates = new List<MusicTrack>();
        foreach (var c in _children)
            if (c.Next(rng) is { } t) candidates.Add(t);
        if (candidates.Count == 0) return null;
        while (candidates.Count > 1)
        {
            int i = rng.Next(candidates.Count);
            var pick = candidates[i];
            if (Same(pick, _last) && _lastCount == 2) { candidates.RemoveAt(i); continue; }
            return Remember(pick);
        }
        return Remember(candidates[0]);
    }

    MusicTrack Remember(MusicTrack pick)
    {
        if (Same(pick, _last)) _lastCount++;
        else { _last = pick; _lastCount = 1; }
        return pick;
    }

    static bool Same(MusicTrack? a, MusicTrack? b) =>
        a != null && b != null && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// CAdvMapMusicCalculator (Next at 00816370): the campaign mission's start music once,
/// then the normal adventure composite for the rest of the mission.
/// </summary>
public sealed class StartThenMusicCalculator(IMusicCalculator start, IMusicCalculator main) : IMusicCalculator
{
    bool _first = true;

    public MusicTrack? Next(Random rng)
    {
        if (_first)
        {
            _first = false;
            if (start.Next(rng) is { } t) return t;
        }
        return main.Next(rng);
    }
}

/// <summary>
/// CSiegeMusicCalculator (Next at 008188f0): the town race's SiegeMusic first, then the
/// combat composite for the rest of the battle.
/// </summary>
public sealed class SiegeMusicCalculator(MusicTrack? siege, IMusicCalculator combat) : IMusicCalculator
{
    int _state;

    public MusicTrack? Next(Random rng)
    {
        if (_state++ == 0 && siege != null) return siege;
        return combat.Next(rng);
    }
}

/// <summary>
/// CAdvAmbientSound (Calc at 00815400): the ambient set for the adventure view is the one
/// used by most of the 3×3 tiles around the view centre; tiles the player hasn't explored
/// count as INVISIBLE_AMBIENT_SOUND. Ties go to a random one of the most common sets.
/// </summary>
public static class AdvAmbientPicker
{
    public static string? Pick(int centerX, int centerY, int width, int height,
        Func<int, int, string?> tileAmbient, Func<int, int, bool> explored, string? invisible, Random rng)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = Math.Clamp(centerX + dx, 0, width - 1), y = Math.Clamp(centerY + dy, 0, height - 1);
                var set = explored(x, y) ? tileAmbient(x, y) : invisible;
                if (string.IsNullOrEmpty(set)) continue;
                if (counts.TryGetValue(set, out var n)) counts[set] = n + 1;
                else { counts[set] = 1; order.Add(set); }
            }
        if (order.Count == 0) return null;
        int best = counts.Values.Max();
        var top = order.Where(s => counts[s] == best).ToList();
        return top[rng.Next(top.Count)];
    }
}
