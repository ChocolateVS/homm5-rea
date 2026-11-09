namespace H5.Rmg;

/// <summary>
/// The x87 control word the game runs under: 0x009dc690 calls _control87(0xa031f, 0xffffffff) — every exception
/// masked, _PC_24 (24-bit mantissa) and _RC_CHOP (round toward zero); 0x00d10bf0, 0x00838e10, 0x00c9fa80, 0x0041ecd0,
/// 0x007c98e0, 0x007ca990, 0x007e4420 and 0x007c9810 call it again. Every x87 result of the generator is therefore
/// chopped to a float, and a plain FISTP (Ghidra prints ROUND) truncates like the C casts that set chop themselves.
/// Evidence beyond the call: the road builder's sums (0x0091aee0) reproduce the nine stock RMG maps object for object
/// only this way.
/// </summary>
public static class X87
{
    /// <summary>Round toward zero to single precision.</summary>
    public static float Chop(double x)
    {
        float r = (float)x;
        if (Math.Abs((double)r) > Math.Abs(x)) r = x > 0 ? MathF.BitDecrement(r) : MathF.BitIncrement(r);
        return r;
    }

    /// <summary>FADD: the double sum of two floats is exact while their exponents lie within 29 bits of each other.</summary>
    public static float Add(float a, float b) => Chop((double)a + b);

    /// <summary>FMUL of two exact operands (a float, or an integer below 2^31 as FILD loads it), chopped from the exact product.</summary>
    public static float Mul(double a, double b)
    {
        double p = a * b;
        double err = Math.FusedMultiplyAdd(a, b, -p);   // exact product = p + err
        float r = (float)p;
        double d = (double)r - p;                       // exact (r and p are adjacent)
        if (p > 0 ? d > err : d < err) r = p > 0 ? MathF.BitDecrement(r) : MathF.BitIncrement(r);
        return r;
    }
}
