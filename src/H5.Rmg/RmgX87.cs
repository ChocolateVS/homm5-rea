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

    /// <summary>FSUB of two floats (a − b), chopped like <see cref="Add"/>.</summary>
    public static float Sub(float a, float b) => Chop((double)a - b);

    /// <summary>FDIV of two exact operands (floats or FILD integers), chopped from the exact quotient.</summary>
    public static float Div(double a, double b)
    {
        float r = (float)(a / b);
        if (r == 0f) return r;
        // |r| > |a / b| exactly  ⇔  the residual r·b − a has the sign of a (either sign of b); FMA gives its sign exactly.
        double res = Math.FusedMultiplyAdd(r, b, -a);
        if (res != 0 && Math.Sign(res) == Math.Sign(a)) r = r > 0 ? MathF.BitDecrement(r) : MathF.BitIncrement(r);
        return r;
    }

    /// <summary>FSQRT of an exact non-negative operand, chopped.</summary>
    public static float Sqrt(double a)
    {
        float r = (float)Math.Sqrt(a);
        if (r > 0f && Math.FusedMultiplyAdd(r, r, -a) > 0) r = MathF.BitDecrement(r);
        return r;
    }

    /// <summary>
    /// FSIN / FCOS: not under the precision control (only FADD / FSUB / FMUL / FDIV / FSQRT are); the result keeps
    /// the 64-bit mantissa. Ours is the double result, which the next chopped operation or float store reads.
    /// </summary>
    public static double Sin(float a) => Math.Sin(a);

    /// <summary><see cref="Sin"/> for FCOS.</summary>
    public static double Cos(float a) => Math.Cos(a);
}
