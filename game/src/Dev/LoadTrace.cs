using System.Diagnostics;
using Godot;

namespace H5Reborn.Dev;

/// <summary>
/// Load-time profile (`--load-trace`, always on in the browser build): <see cref="Mark"/> logs a stage with the time since
/// the previous mark, <see cref="Time"/> adds the time of a block to a named bucket (texture decode, model build …)
/// that <see cref="Summary"/> prints. Observes only.
/// </summary>
public static class LoadTrace
{
    public static bool Enabled = OperatingSystem.IsBrowser() || System.Environment.GetCommandLineArgs().Contains("--load-trace")
                                 || OS.GetCmdlineUserArgs().Contains("--load-trace");
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static long _last;
    static readonly Dictionary<string, (long Ticks, int Count)> _buckets = new();

    public static void Mark(string stage)
    {
        if (!Enabled) return;
        long now = _clock.ElapsedTicks;
        GD.Print($"[H5] load: {stage} {Ms(now - _last):F0} ms");
        _last = now;
    }

    /// <summary>Restarts the stage clock and clears the buckets.</summary>
    public static void Begin(string what)
    {
        if (!Enabled) return;
        _buckets.Clear();
        _last = _clock.ElapsedTicks;
        GD.Print($"[H5] load: begin {what}");
    }

    public static void Summary(string what)
    {
        if (!Enabled) return;
        var parts = _buckets.OrderByDescending(kv => kv.Value.Ticks).Select(kv => $"{kv.Key}={Ms(kv.Value.Ticks):F0}ms/{kv.Value.Count}");
        GD.Print($"[H5] load: {what} buckets {string.Join(" ", parts)}");
    }

    public static Scope Time(string bucket) => Enabled ? new Scope(bucket, _clock.ElapsedTicks) : default;

    public readonly struct Scope(string? bucket, long start) : IDisposable
    {
        public void Dispose()
        {
            if (bucket == null) return;
            long t = _clock.ElapsedTicks - start;
            _buckets.TryGetValue(bucket, out var b);
            _buckets[bucket] = (b.Ticks + t, b.Count + 1);
        }
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
