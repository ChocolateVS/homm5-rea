namespace H5.Core.Numerics;

/// <summary>
/// Platform-independent transcendental functions for game rules. <see cref="Math"/>.Sin/Atan2/Log/Pow … call the
/// platform C library (the Windows CRT on desktop, musl in the WebAssembly build, glibc on Linux), whose last-bit results
/// differ; desktop and browser players share network games, so rules must not use them. These are fdlibm's algorithms
/// (Sun, as kept in FreeBSD msun / musl) in plain IEEE double arithmetic, which every .NET runtime evaluates the same way
/// (C# never contracts to FMA). Error below 1 ulp; the float versions round the double result (correctly rounded but for
/// double-rounding cases far rarer than the inputs the rules use). DeterminismSuite records their values.
/// </summary>
public static class DetMath
{
    static int Hi(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);
    static uint Lo(double x) => (uint)BitConverter.DoubleToInt64Bits(x);
    static double Make(int hi, uint lo) => BitConverter.Int64BitsToDouble(((long)hi << 32) | lo);
    static double WithLo(double x, uint lo) => Make(Hi(x), lo);
    static double WithHi(double x, int hi) => Make(hi, Lo(x));

    // ---------------------------------------------------------------- sin / cos (k_sin.c, k_cos.c, e_rem_pio2.c)

    static double KSin(double x, double y, bool hasTail)
    {
        const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
            S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
        double z = x * x, w = z * z;
        double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
        double v = z * x;
        if (!hasTail) return x + v * (S1 + z * r);
        return x - ((z * (0.5 * y - v * r) - y) - v * S1);
    }

    static double KCos(double x, double y)
    {
        const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
            C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;
        double z = x * x, w = z * z;
        double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
        double hz = 0.5 * z;
        w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + (z * r - x * y));
    }

    /// <summary>x − n·π/2 = y0 + y1 for |x| &lt; 2^20·π/2 (the medium-size path of e_rem_pio2.c, exact for every such x).</summary>
    static int RemPio2(double x, out double y0, out double y1)
    {
        const double toint = 6755399441055744.0, pio4 = 7.85398163397448278999e-01,
            invpio2 = 6.36619772367581382433e-01,
            pio2_1 = 1.57079632673412561417e+00, pio2_1t = 6.07710050650619224932e-11,
            pio2_2 = 6.07710050630396597660e-11, pio2_2t = 2.02226624879595063154e-21,
            pio2_3 = 2.02226624871116645580e-21, pio2_3t = 8.47842766036889956997e-32;
        int ix = Hi(x) & 0x7fffffff;
        double fn = x * invpio2 + toint - toint;
        int n = (int)fn;
        double r = x - fn * pio2_1, w = fn * pio2_1t;
        if (r - w < -pio4) { n--; fn--; r = x - fn * pio2_1; w = fn * pio2_1t; }
        else if (r - w > pio4) { n++; fn++; r = x - fn * pio2_1; w = fn * pio2_1t; }
        y0 = r - w;
        int ex = ix >> 20, ey = (Hi(y0) >> 20) & 0x7ff;
        if (ex - ey > 16)
        {
            double t = r;
            w = fn * pio2_2; r = t - w; w = fn * pio2_2t - ((t - r) - w); y0 = r - w;
            ey = (Hi(y0) >> 20) & 0x7ff;
            if (ex - ey > 49)
            {
                t = r;
                w = fn * pio2_3; r = t - w; w = fn * pio2_3t - ((t - r) - w); y0 = r - w;
            }
        }
        y1 = (r - y0) - w;
        return n;
    }

    const int MediumLimit = 0x413921fb;   // |x| ~< 2^20·π/2

    public static double Sin(double x)
    {
        int ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return ix < 0x3e500000 ? x : KSin(x, 0, false);   // |x| ~<= π/4
        if (ix >= 0x7ff00000) return x - x;
        if (ix >= MediumLimit) throw new ArgumentOutOfRangeException(nameof(x), x, "DetMath.Sin: |x| >= 2^20·π/2");
        int n = RemPio2(x, out double y0, out double y1);
        return (n & 3) switch { 0 => KSin(y0, y1, true), 1 => KCos(y0, y1), 2 => -KSin(y0, y1, true), _ => -KCos(y0, y1) };
    }

    public static double Cos(double x)
    {
        int ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return ix < 0x3e46a09e ? 1.0 : KCos(x, 0);
        if (ix >= 0x7ff00000) return x - x;
        if (ix >= MediumLimit) throw new ArgumentOutOfRangeException(nameof(x), x, "DetMath.Cos: |x| >= 2^20·π/2");
        int n = RemPio2(x, out double y0, out double y1);
        return (n & 3) switch { 0 => KCos(y0, y1), 1 => -KSin(y0, y1, true), 2 => -KCos(y0, y1), _ => KSin(y0, y1, true) };
    }

    // ---------------------------------------------------------------- atan / atan2 (s_atan.c, e_atan2.c)

    static readonly double[] AtanHi = { 4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00 };
    static readonly double[] AtanLo = { 2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17 };
    static readonly double[] AT =
    {
        3.33333333333329318027e-01, -1.99999999998764832476e-01, 1.42857142725034663711e-01, -1.11111104054623557880e-01,
        9.09088713343650656196e-02, -7.69187620504482999495e-02, 6.66107313738753120669e-02, -5.83357013379057348645e-02,
        4.97687799461593236017e-02, -3.65315727442169155270e-02, 1.62858201153657823623e-02,
    };

    public static double Atan(double x)
    {
        int hx = Hi(x), ix = hx & 0x7fffffff;
        bool neg = hx < 0;
        int id;
        if (ix >= 0x44100000)
        {
            if (double.IsNaN(x)) return x;
            return neg ? -AtanHi[3] : AtanHi[3];
        }
        if (ix < 0x3fdc0000)
        {
            if (ix < 0x3e400000) return x;
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {
                if (ix < 0x3fe60000) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }
                else { id = 1; x = (x - 1.0) / (x + 1.0); }
            }
            else
            {
                if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }
                else { id = 3; x = -1.0 / x; }
            }
        }
        double z = x * x, w = z * z;
        double s1 = z * (AT[0] + w * (AT[2] + w * (AT[4] + w * (AT[6] + w * (AT[8] + w * AT[10])))));
        double s2 = w * (AT[1] + w * (AT[3] + w * (AT[5] + w * (AT[7] + w * AT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - (x * (s1 + s2) - AtanLo[id] - x);
        return neg ? -z : z;
    }

    public static double Atan2(double y, double x)
    {
        const double pi = 3.1415926535897931160E+00, pi_lo = 1.2246467991473531772E-16;
        if (double.IsNaN(x) || double.IsNaN(y)) return x + y;
        int ix = Hi(x), iy = Hi(y);
        uint lx = Lo(x), ly = Lo(y);
        if (((uint)(ix - 0x3ff00000) | lx) == 0) return Atan(y);
        int m = ((iy >> 31) & 1) | ((ix >> 30) & 2);
        ix &= 0x7fffffff; iy &= 0x7fffffff;
        if (((uint)iy | ly) == 0) return m switch { 0 or 1 => y, 2 => pi, _ => -pi };
        if (((uint)ix | lx) == 0) return (m & 1) != 0 ? -pi / 2 : pi / 2;
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000) return m switch { 0 => pi / 4, 1 => -pi / 4, 2 => 3 * pi / 4, _ => -3 * pi / 4 };
            return m switch { 0 => 0.0, 1 => -0.0, 2 => pi, _ => -pi };
        }
        if (ix + (64 << 20) < iy || iy == 0x7ff00000) return (m & 1) != 0 ? -pi / 2 : pi / 2;
        double z = (m & 2) != 0 && iy + (64 << 20) < ix ? 0 : Atan(Math.Abs(y / x));
        return m switch { 0 => z, 1 => -z, 2 => pi - (z - pi_lo), _ => (z - pi_lo) - pi };
    }

    /// <summary>atan2 of floats, rounded to float (for MathF.Atan2).</summary>
    public static float Atan2(float y, float x) => (float)Atan2((double)y, (double)x);

    // ---------------------------------------------------------------- asin (e_asin.c)

    public static double Asin(double x)
    {
        const double pio2_hi = 1.57079632679489655800e+00, pio2_lo = 6.12323399573676603587e-17;
        int hx = Hi(x), ix = hx & 0x7fffffff;
        if (ix >= 0x3ff00000)
        {
            if (((uint)(ix - 0x3ff00000) | Lo(x)) == 0) return x * pio2_hi + 7.5231638452626401e-37;
            return double.NaN;
        }
        if (ix < 0x3fe00000)
        {
            if (ix < 0x3e500000 && ix >= 0x00100000) return x;
            return x + x * AsinR(x * x);
        }
        double z = (1 - Math.Abs(x)) * 0.5, s = Math.Sqrt(z), r = AsinR(z);
        if (ix >= 0x3fef3333) x = pio2_hi - (2 * (s + s * r) - pio2_lo);
        else
        {
            double f = WithLo(s, 0), c = (z - f * f) / (s + f);
            x = 0.5 * pio2_hi - (2 * s * r - (pio2_lo - 2 * c) - (0.5 * pio2_hi - 2 * f));
        }
        return hx < 0 ? -x : x;
    }

    static double AsinR(double z)
    {
        const double pS0 = 1.66666666666666657415e-01, pS1 = -3.25565818622400915405e-01, pS2 = 2.01212532134862925881e-01,
            pS3 = -4.00555345006794114027e-02, pS4 = 7.91534994289814532176e-04, pS5 = 3.47933107596021167570e-05,
            qS1 = -2.40339491173441421878e+00, qS2 = 2.02094576023350569471e+00, qS3 = -6.88283971605453293030e-01, qS4 = 7.70381505559019352791e-02;
        double p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
        double q = 1.0 + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
        return p / q;
    }

    // ---------------------------------------------------------------- log / log10 / log2 (e_log.c, e_log10.c, e_log2.c)

    const double Lg1 = 6.666666666666735130e-01, Lg2 = 3.999999999940941908e-01, Lg3 = 2.857142874366239149e-01,
        Lg4 = 2.222219843214978396e-01, Lg5 = 1.818357216161805012e-01, Lg6 = 1.531383769920937332e-01, Lg7 = 1.479819860511658591e-01;

    /// <summary>
    /// The shared argument reduction: x = 2^k·(1 + f), 1 + f in [√2/2, √2). Returns false with <paramref name="special"/>
    /// set for x ≤ 0, inf, NaN and x = 1.
    /// </summary>
    static bool Reduce(double x, out int k, out double f, out double hfsq, out double s, out double R, out double special)
    {
        k = 0; f = hfsq = s = R = special = 0;
        uint hx = (uint)Hi(x);
        long bits = BitConverter.DoubleToInt64Bits(x);
        if (hx < 0x00100000 || (hx >> 31) != 0)
        {
            if ((bits << 1) == 0) { special = double.NegativeInfinity; return false; }
            if ((hx >> 31) != 0) { special = double.NaN; return false; }
            k -= 54; x *= 18014398509481984.0; hx = (uint)Hi(x);   // subnormal: scale by 2^54
        }
        else if (hx >= 0x7ff00000) { special = x; return false; }
        else if (hx == 0x3ff00000 && (bits << 32) == 0) { special = 0; return false; }
        hx += 0x3ff00000 - 0x3fe6a09e;
        k += (int)(hx >> 20) - 0x3ff;
        hx = (hx & 0x000fffff) + 0x3fe6a09e;
        x = Make((int)hx, Lo(x));
        f = x - 1.0;
        hfsq = 0.5 * f * f;
        s = f / (2.0 + f);
        double z = s * s, w = z * z;
        double t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        double t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        R = t2 + t1;
        return true;
    }

    public static double Log(double x)
    {
        const double ln2_hi = 6.93147180369123816490e-01, ln2_lo = 1.90821492927058770002e-10;
        if (!Reduce(x, out int k, out double f, out double hfsq, out double s, out double R, out double special)) return special;
        double dk = k;
        return s * (hfsq + R) + dk * ln2_lo - hfsq + f + dk * ln2_hi;
    }

    public static double Log10(double x)
    {
        const double ivln10hi = 4.34294481878168880939e-01, ivln10lo = 2.50829467116452752298e-11,
            log10_2hi = 3.01029995663611771306e-01, log10_2lo = 3.69423907715893078616e-13;
        if (!Reduce(x, out int k, out double f, out double hfsq, out double s, out double R, out double special)) return special;
        double hi = WithLo(f - hfsq, 0);
        double lo = f - hi - hfsq + s * (hfsq + R);
        double val_hi = hi * ivln10hi, dk = k, y = dk * log10_2hi;
        double val_lo = dk * log10_2lo + (lo + hi) * ivln10lo + lo * ivln10hi;
        double w = y + val_hi;
        val_lo += (y - w) + val_hi;
        val_hi = w;
        return val_lo + val_hi;
    }

    public static double Log2(double x)
    {
        const double ivln2hi = 1.44269504072144627571e+00, ivln2lo = 1.67517131648865118353e-10;
        if (!Reduce(x, out int k, out double f, out double hfsq, out double s, out double R, out double special)) return special;
        double hi = WithLo(f - hfsq, 0);
        double lo = f - hi - hfsq + s * (hfsq + R);
        double val_hi = hi * ivln2hi;
        double val_lo = (lo + hi) * ivln2lo + lo * ivln2hi;
        double y = k, w = y + val_hi;
        val_lo += (y - w) + val_hi;
        val_hi = w;
        return val_lo + val_hi;
    }

    // ---------------------------------------------------------------- pow (e_pow.c)

    public static double Pow(double x, double y)
    {
        const double two53 = 9007199254740992.0, huge = 1.0e300, tiny = 1.0e-300,
            L1 = 5.99999999999994648725e-01, L2 = 4.28571428578550184252e-01, L3 = 3.33333329818377432918e-01,
            L4 = 2.72728123808534006489e-01, L5 = 2.30660745775561754067e-01, L6 = 2.06975017800338417784e-01,
            P1 = 1.66666666666666019037e-01, P2 = -2.77777777770155933842e-03, P3 = 6.61375632143793436117e-05,
            P4 = -1.65339022054652515390e-06, P5 = 4.13813679705723846039e-08,
            lg2 = 6.93147180559945286227e-01, lg2_h = 6.93147182464599609375e-01, lg2_l = -1.90465429995776804525e-09,
            ovt = 8.0085662595372944372e-17,
            cp = 9.61796693925975554329e-01, cp_h = 9.61796700954437255859e-01, cp_l = -7.02846165095275826516e-09,
            ivln2 = 1.44269504088896338700e+00, ivln2_h = 1.44269502162933349609e+00, ivln2_l = 1.92596299112661746887e-08;
        ReadOnlySpan<double> bp = stackalloc double[] { 1.0, 1.5 };
        ReadOnlySpan<double> dp_h = stackalloc double[] { 0.0, 5.84962487220764160156e-01 };
        ReadOnlySpan<double> dp_l = stackalloc double[] { 0.0, 1.35003920212974897128e-08 };

        int hx = Hi(x), hy = Hi(y);
        uint lx = Lo(x), ly = Lo(y);
        int ix = hx & 0x7fffffff, iy = hy & 0x7fffffff;
        double z, ax, z_h, z_l, p_h, p_l, y1, t1, t2, r, s, t, u, v, w;
        int i, j, k, yisint, n;

        if (((uint)iy | ly) == 0) return 1.0;
        if (hx == 0x3ff00000 && lx == 0) return 1.0;
        if (ix > 0x7ff00000 || (ix == 0x7ff00000 && lx != 0) || iy > 0x7ff00000 || (iy == 0x7ff00000 && ly != 0)) return x + y;

        yisint = 0;
        if (hx < 0)
        {
            if (iy >= 0x43400000) yisint = 2;
            else if (iy >= 0x3ff00000)
            {
                k = (iy >> 20) - 0x3ff;
                if (k > 20)
                {
                    uint jj = ly >> (52 - k);
                    if ((jj << (52 - k)) == ly) yisint = 2 - (int)(jj & 1);
                }
                else if (ly == 0)
                {
                    uint jj = (uint)iy >> (20 - k);
                    if ((jj << (20 - k)) == (uint)iy) yisint = 2 - (int)(jj & 1);
                }
            }
        }

        if (ly == 0)
        {
            if (iy == 0x7ff00000)
            {
                if (((uint)(ix - 0x3ff00000) | lx) == 0) return 1.0;
                if (ix >= 0x3ff00000) return hy >= 0 ? y : 0.0;
                return hy >= 0 ? 0.0 : -y;
            }
            if (iy == 0x3ff00000) return hy >= 0 ? x : 1.0 / x;
            if (hy == 0x40000000) return x * x;
            if (hy == 0x3fe00000 && hx >= 0) return Math.Sqrt(x);
        }

        ax = Math.Abs(x);
        if (lx == 0 && (ix == 0x7ff00000 || ix == 0 || ix == 0x3ff00000))
        {
            z = ax;
            if (hy < 0) z = 1.0 / z;
            if (hx < 0)
            {
                if (((ix - 0x3ff00000) | yisint) == 0) z = double.NaN;
                else if (yisint == 1) z = -z;
            }
            return z;
        }

        s = 1.0;
        if (hx < 0)
        {
            if (yisint == 0) return double.NaN;
            if (yisint == 1) s = -1.0;
        }

        if (iy > 0x41e00000)
        {
            if (iy > 0x43f00000)
            {
                if (ix <= 0x3fefffff) return hy < 0 ? huge * huge : tiny * tiny;
                if (ix >= 0x3ff00000) return hy > 0 ? huge * huge : tiny * tiny;
            }
            if (ix < 0x3fefffff) return hy < 0 ? s * huge * huge : s * tiny * tiny;
            if (ix > 0x3ff00000) return hy > 0 ? s * huge * huge : s * tiny * tiny;
            t = ax - 1.0;
            w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
            u = ivln2_h * t;
            v = t * ivln2_l - w * ivln2;
            t1 = WithLo(u + v, 0);
            t2 = v - (t1 - u);
        }
        else
        {
            double ss, s2, s_h, s_l, t_h, t_l;
            n = 0;
            if (ix < 0x00100000) { ax *= two53; n -= 53; ix = Hi(ax); }
            n += (ix >> 20) - 0x3ff;
            j = ix & 0x000fffff;
            ix = j | 0x3ff00000;
            if (j <= 0x3988E) k = 0;
            else if (j < 0xBB67A) k = 1;
            else { k = 0; n += 1; ix -= 0x00100000; }
            ax = WithHi(ax, ix);

            u = ax - bp[k];
            v = 1.0 / (ax + bp[k]);
            ss = u * v;
            s_h = WithLo(ss, 0);
            t_h = Make(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18), 0);
            t_l = ax - (t_h - bp[k]);
            s_l = v * ((u - s_h * t_h) - s_h * t_l);
            s2 = ss * ss;
            r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
            r += s_l * (s_h + ss);
            s2 = s_h * s_h;
            t_h = WithLo(3.0 + s2 + r, 0);
            t_l = r - ((t_h - 3.0) - s2);
            u = s_h * t_h;
            v = s_l * t_h + t_l * ss;
            p_h = WithLo(u + v, 0);
            p_l = v - (p_h - u);
            z_h = cp_h * p_h;
            z_l = cp_l * p_h + p_l * cp + dp_l[k];
            t = n;
            t1 = WithLo(((z_h + z_l) + dp_h[k]) + t, 0);
            t2 = z_l - (((t1 - t) - dp_h[k]) - z_h);
        }

        y1 = WithLo(y, 0);
        p_l = (y - y1) * t1 + y * t2;
        p_h = y1 * t1;
        z = p_l + p_h;
        j = Hi(z); i = (int)Lo(z);
        if (j >= 0x40900000)
        {
            if (((j - 0x40900000) | i) != 0) return s * huge * huge;
            if (p_l + ovt > z - p_h) return s * huge * huge;
        }
        else if ((j & 0x7fffffff) >= 0x4090cc00)
        {
            if ((unchecked((uint)j - 0xc090cc00u) | (uint)i) != 0) return s * tiny * tiny;
            if (p_l <= z - p_h) return s * tiny * tiny;
        }
        i = j & 0x7fffffff;
        k = (i >> 20) - 0x3ff;
        n = 0;
        if (i > 0x3fe00000)
        {
            n = j + (0x00100000 >> (k + 1));
            k = ((n & 0x7fffffff) >> 20) - 0x3ff;
            t = Make(n & ~(0x000fffff >> k), 0);
            n = ((n & 0x000fffff) | 0x00100000) >> (20 - k);
            if (j < 0) n = -n;
            p_h -= t;
        }
        t = WithLo(p_l + p_h, 0);
        u = t * lg2_h;
        v = (p_l - (t - p_h)) * lg2 + t * lg2_l;
        z = u + v;
        w = v - (z - u);
        t = z * z;
        t1 = z - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        r = (z * t1) / (t1 - 2.0) - (w + z * w);
        z = 1.0 - (r - z);
        j = Hi(z);
        j += n << 20;
        z = (j >> 20) <= 0 ? Math.ScaleB(z, n) : WithHi(z, j);
        return s * z;
    }

    /// <summary>pow of floats, rounded to float (for MathF.Pow).</summary>
    public static float Pow(float x, float y) => (float)Pow((double)x, (double)y);
}
