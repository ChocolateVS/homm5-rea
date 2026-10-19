namespace H5.Adventure.Map;

/// <summary>
/// The fog-of-war intensity grid the original renderer darkens the adventure map with
/// (H5_Game.exe FUN_005b0f80). One byte per tile corner: corner (x, y) — world (2x, 2y) — is 255
/// only when all four tiles around it are explored, then the grid is blurred twice with the
/// 5-tap kernel (FUN_005b0ea0, ping-pong). The grid is (nextPow2(w) + 1) × (nextPow2(h) + 1),
/// zero outside the map; the renderer samples it bilinearly with texel indices masked by
/// nextPow2 − 1 (FUN_00cf9420) and multiplies every lit vertex colour by it.
/// </summary>
public static class WarFogGrid
{
    /// <summary>Builds the grid for a row-major explored array of <paramref name="w"/> × <paramref name="h"/> tiles.</summary>
    public static byte[] Build(bool[] explored, int w, int h, out int gridW, out int gridH)
    {
        gridW = NextPow2(w) + 1;
        gridH = NextPow2(h) + 1;
        var a = new byte[gridW * gridH];
        for (int y = 1; y < h; y++)
            for (int x = 1; x < w; x++)
            {
                bool all = explored[y * w + x] && explored[y * w + x - 1]
                    && explored[(y - 1) * w + x] && explored[(y - 1) * w + x - 1];
                a[y * gridW + x] = all ? (byte)255 : (byte)0;
            }
        var b = (byte[])a.Clone();
        Blur(a, b, gridW, gridH);
        Blur(b, a, gridW, gridH);
        return a;
    }

    /// <summary>FUN_005b0ea0: interior texels = (centre + 4 neighbours) / 5 (integer), edges kept.</summary>
    static void Blur(byte[] src, byte[] dst, int gw, int gh)
    {
        for (int y = 1; y < gh - 1; y++)
            for (int x = 1; x < gw - 1; x++)
            {
                int i = y * gw + x;
                dst[i] = (byte)((src[i - gw] + src[i + gw] + src[i - 1] + src[i + 1] + src[i]) / 5);
            }
    }

    /// <summary>Smallest power of two ≥ n (the exe's bit smear of n − 1, plus one).</summary>
    public static int NextPow2(int n)
    {
        uint v = (uint)(n - 1);
        v |= v >> 16; v |= v >> 8; v |= v >> 4; v |= v >> 2; v |= v >> 1;
        return (int)v + 1;
    }
}
