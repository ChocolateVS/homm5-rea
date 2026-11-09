using System.Runtime.InteropServices.JavaScript;
using Godot;
using H5.Assets.Model;

namespace H5Reborn.Web;

/// <summary>[JSImport] bindings to wwwroot/h5cache.js (decoded assets kept in IndexedDB between visits).</summary>
static partial class H5CacheJs
{
    [JSImport("globalThis.h5cache.enabled")] internal static partial bool Enabled();
    [JSImport("globalThis.h5cache.prefetch")] internal static partial Task<int> Prefetch(string keys);
    [JSImport("globalThis.h5cache.get")] [return: JSMarshalAs<JSType.Array<JSType.Number>>] internal static partial byte[]? Get(string key);
    [JSImport("globalThis.h5cache.put")] internal static partial void Put(string key, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data);
    [JSImport("globalThis.h5cache.release")] internal static partial int Release();
    [JSImport("globalThis.h5cache.stats")] internal static partial string Stats();
}

/// <summary>
/// The browser build's <see cref="IDecodedCache"/>: Granny skeletons and baked clips in IndexedDB, so the second visit
/// to a map skips reading, decoding and baking its characters (docs/WEB_BUILD.md, Loading an adventure map). Installed by
/// <see cref="WebStorage"/>; the adventure map fetches its entries ahead (<see cref="Render.CharacterBuilder.DecodedPrefetch"/>).
/// Keys are prefixed with the install's data fingerprint (pak names and sizes).
/// </summary>
sealed class WebDecodedCache : IDecodedCache
{
    string? _prefix;
    string Prefix => _prefix ??= "h5/" + Game.Instance.Install.DataFingerprint() + "/";

    public static void Install()
    {
        try { if (!H5CacheJs.Enabled()) return; }
        catch (Exception e) { GD.Print($"[H5] web: decoded cache unavailable ({e.Message})"); return; }
        var cache = new WebDecodedCache();
        AnimSetLoader.DecodedCache = cache;
        Render.CharacterBuilder.DecodedPrefetch = cache.Prefetch;
        Render.CharacterBuilder.DecodedRelease = () => GD.Print($"[H5] web: decoded cache {H5CacheJs.Stats()}, released {H5CacheJs.Release()} from memory");
    }

    Task Prefetch(IReadOnlyCollection<string> keys) => H5CacheJs.Prefetch(string.Join('\n', keys.Select(k => Prefix + k)));

    public byte[]? Get(string key) => H5CacheJs.Get(Prefix + key);

    public void Put(string key, byte[] data) => H5CacheJs.Put(Prefix + key, new ArraySegment<byte>(data));
}
