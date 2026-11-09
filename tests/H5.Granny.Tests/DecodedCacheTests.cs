using H5.Assets.Granny;
using H5.Assets.Model;
using H5.Core.Xdb;

namespace H5.Granny.Tests;

/// <summary>The decoded-asset cache (browser build) gives back exactly what decoding gives.</summary>
public class DecodedCacheTests(GameFixture game) : IClassFixture<GameFixture>
{
    const string FootmanSkeleton = "E5856CB4-F3E3-44FD-9FBF-74639A66F72A";
    const string FootmanAttack00 = "06661489-7EB6-4E78-9137-20B6753570AF";

    sealed class MemoryCache : IDecodedCache
    {
        public readonly Dictionary<string, byte[]> Entries = new();
        public byte[]? Get(string key) => Entries.GetValueOrDefault(key);
        public void Put(string key, byte[] data) => Entries[key] = data;
    }

    [Fact]
    public void Cached_clip_and_skeleton_equal_the_decoded_ones()
    {
        if (game.Fs == null) return;
        var cache = new MemoryCache();
        try
        {
            AnimSetLoader.DecodedCache = cache;
            var stored = new AnimSetLoader(new XdbLoader(game.Fs));
            var skel = stored.LoadSkeletonByUid(FootmanSkeleton)!;
            var baked = stored.LoadBakedByUid(FootmanAttack00)!.Value;
            // (other test classes may run meanwhile and use the same static cache: check by key)
            Assert.True(cache.Entries.ContainsKey(AnimSetLoader.SkeletonCacheKey(FootmanSkeleton)));
            Assert.True(cache.Entries.ContainsKey(stored.ClipCacheKey(FootmanAttack00)));

            // A fresh loader reads both back from the cache (no Granny source then).
            var fromCache = new AnimSetLoader(new XdbLoader(game.Fs));
            var skel2 = fromCache.LoadSkeletonByUid(FootmanSkeleton)!;
            var again = fromCache.LoadBakedByUid(FootmanAttack00)!.Value;
            Assert.Null(again.Source);
            Assert.NotNull(baked.Source);

            Assert.Equal(Bytes(DecodedCacheFormat.WriteSkeleton(skel)), Bytes(DecodedCacheFormat.WriteSkeleton(skel2)));
            Assert.Equal(skel.Bones.Count, skel2.Bones.Count);
            Assert.Equal(skel.Bones[3].InverseBind, skel2.Bones[3].InverseBind);
            Assert.Equal(Bytes(DecodedCacheFormat.WriteClip(baked.Clip, baked.Events)), Bytes(DecodedCacheFormat.WriteClip(again.Clip, again.Events)));
            Assert.Equal(baked.Events, again.Events);
            Assert.Equal(baked.Clip.Tracks.Count, again.Clip.Tracks.Count);
            Assert.Equal(baked.Clip.Tracks[0].Rotations, again.Clip.Tracks[0].Rotations);

            // Without a cache the same clip is baked.
            AnimSetLoader.DecodedCache = null;
            var plain = new AnimSetLoader(new XdbLoader(game.Fs)).LoadBakedByUid(FootmanAttack00)!.Value;
            Assert.Equal(Bytes(DecodedCacheFormat.WriteClip(plain.Clip, plain.Events)), Bytes(DecodedCacheFormat.WriteClip(again.Clip, again.Events)));
        }
        finally { AnimSetLoader.DecodedCache = null; }
    }

    static string Bytes(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void Xdb_cache_drops_least_recently_used_documents_past_its_budget()
    {
        if (game.Fs == null) return;
        var x = new XdbLoader(game.Fs) { MaxCachedBytes = 1 };
        var a = x.Load("Characters/Creatures/Haven/Footman.(Character).xdb");
        Assert.Equal(1, x.CachedCount);
        var b = x.Load("_(Model)/Creatures/Haven/Footman.(Model).xdb");
        Assert.Equal(1, x.CachedCount);
        Assert.Equal(1, x.Evictions);
        Assert.NotSame(a, x.Load("Characters/Creatures/Haven/Footman.(Character).xdb"));
        Assert.Equal(a.ToString(), x.Load("Characters/Creatures/Haven/Footman.(Character).xdb").ToString());

        var big = new XdbLoader(game.Fs);
        var c = big.Load("Characters/Creatures/Haven/Footman.(Character).xdb");
        big.Load("_(Model)/Creatures/Haven/Footman.(Model).xdb");
        Assert.Same(c, big.Load("Characters/Creatures/Haven/Footman.(Character).xdb"));
        Assert.Equal(0, big.Evictions);
        _ = b;
    }
}
