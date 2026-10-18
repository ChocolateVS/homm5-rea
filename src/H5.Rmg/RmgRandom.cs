namespace H5.Rmg;

/// <summary>
/// The random map generator's RNG, ported from H5_Game.exe (globals at 0x01040028..34):
/// a 64-bit LCG s' = s·0x343FD + 0x269EC3 (mod 2^64) seeded with the sign-extended start seed.
/// Every draw bumps a counter that the original logs per stage ("Rnd Counter(...)").
/// </summary>
public sealed class RmgRandom
{
    ulong _s;

    /// <summary>Number of draws so far (exe 0x00ba6800).</summary>
    public int Counter { get; private set; }

    public RmgRandom(int seed) => Seed(seed);

    /// <summary>exe 0x00ba6650: state = sign-extended seed, counter reset.</summary>
    public void Seed(int seed)
    {
        _s = (ulong)(long)seed;
        Counter = 0;
    }

    void Step()
    {
        Counter++;
        _s = unchecked(_s * 0x343FDUL + 0x269EC3UL);
    }

    /// <summary>
    /// exe 0x00ba6700: uniform integer in [0, n). For n == 0 the original returns without
    /// drawing (and without touching the counter).
    /// </summary>
    public int Next(int n)
    {
        if (n == 0) return 0;
        Step();
        ulong v = (_s >> 16) & 0x7FFF_FFFF_FFFFUL;
        return (int)(uint)(v % (ulong)(uint)n);
    }

    /// <summary>
    /// exe 0x00ba6680: one draw whose result is discarded. The generator calls it in the
    /// "option not random" branches so the draw count does not depend on the options.
    /// </summary>
    public uint Advance()
    {
        Step();
        return (uint)(_s >> 23) & 0x7FFF_FFFF;
    }

    /// <summary>exe 0x00ba6790: a + (b - a) · ((s >> 23) &amp; 0x7FFFFFFF) / 2^31, in single precision.</summary>
    public float Float(float a, float b)
    {
        Step();
        uint v = (uint)(_s >> 23) & 0x7FFF_FFFF;
        return (float)((double)v * (b - a) * 4.656613E-10f + a);
    }

    /// <summary>
    /// exe 0x00910090: MSVC std::random_shuffle over a vector, element i swapped with a[rand(i+1)].
    /// </summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
