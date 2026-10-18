using System.Numerics;

namespace H5.Assets.Granny;

/// <summary>
/// A Granny 2.5 curve: a clamped, non-uniform B-spline of the given degree
/// (0 = step, 1 = linear, 2 = quadratic, 3 = cubic). <see cref="Controls"/> holds
/// <c>Knots.Length * Dimension</c> floats. A curve with no knots is "identity" (absent),
/// and a single knot is a constant.
/// </summary>
public sealed class GrannyCurve
{
    public int Degree;
    public int Dimension;
    public float[] Knots = Array.Empty<float>();
    public float[] Controls = Array.Empty<float>();

    float[]? _padded;

    public bool IsEmpty => Knots.Length == 0 || Controls.Length == 0;
    public bool IsConstant => Knots.Length == 1 || Knots.Length < Degree + 1;
    public int ControlCount => Dimension == 0 ? 0 : Controls.Length / Dimension;

    public static GrannyCurve FromObject(GrObject? o, int dimension)
    {
        var c = new GrannyCurve { Dimension = dimension };
        if (o == null) return c;
        c.Degree = o.Int("Degree");
        c.Knots = o.Floats("Knots");
        c.Controls = o.Floats("Controls");
        if (c.Knots.Length > 0 && c.Controls.Length % c.Knots.Length == 0)
            c.Dimension = c.Controls.Length / c.Knots.Length;
        return c;
    }

    /// <summary>
    /// Evaluates the curve at time <paramref name="t"/> using de Boor's algorithm on the
    /// clamped knot vector [0 x (d+1), k_d .. k_{n-1}, k_{n-1} x d] (the first d stored knots
    /// are implicitly clamped to zero). Times outside the knot range clamp to the ends.
    /// </summary>
    public void Evaluate(float t, Span<float> result)
    {
        int dim = Dimension;
        if (IsEmpty) { result[..dim].Clear(); return; }
        int n = ControlCount;
        int k = Degree;
        if (IsConstant || n < k + 1 || k == 0 && n == 1)
        {
            Controls.AsSpan(0, dim).CopyTo(result);
            return;
        }

        if (k == 0)
        {
            // Step curve: value of the last knot not after t.
            int idx = 0;
            while (idx + 1 < n && Knots[idx + 1] <= t) idx++;
            Controls.AsSpan(idx * dim, dim).CopyTo(result);
            return;
        }

        var knots = _padded ??= Pad(Knots, k);
        float tMax = Knots[n - 1];
        if (t <= 0) t = 0;
        if (t >= tMax)
        {
            Controls.AsSpan((n - 1) * dim, dim).CopyTo(result);
            return;
        }

        int i = k;
        while (i < knots.Length - k - 1 && knots[i] <= t) i++;
        i--;

        Span<float> d = stackalloc float[(k + 1) * dim];
        for (int j = 0; j <= k; j++)
            Controls.AsSpan((j + i - k) * dim, dim).CopyTo(d.Slice(j * dim, dim));

        for (int r = 1; r <= k; r++)
            for (int j = k; j >= r; j--)
            {
                float a0 = knots[j + i - k], a1 = knots[j + 1 + i - r];
                float alpha = a1 != a0 ? (t - a0) / (a1 - a0) : 0f;
                for (int c = 0; c < dim; c++)
                    d[j * dim + c] = d[(j - 1) * dim + c] * (1 - alpha) + d[j * dim + c] * alpha;
            }

        d.Slice(k * dim, dim).CopyTo(result);
    }

    static float[] Pad(float[] knots, int degree)
    {
        var v = new float[knots.Length + 1 + degree];
        for (int i = 0; i < knots.Length; i++) v[i + 1] = knots[i];
        for (int i = 1; i <= degree; i++) v[i] = 0;
        for (int i = 0; i < degree; i++) v[knots.Length + 1 + i] = knots[^1];
        return v;
    }
}

public sealed class GrannyTransformTrack
{
    public string Name = "";
    public GrannyCurve Position = new() { Dimension = 3 };
    public GrannyCurve Orientation = new() { Dimension = 4 };
    public GrannyCurve ScaleShear = new() { Dimension = 9 };

    public Vector3 SamplePosition(float t)
    {
        Span<float> v = stackalloc float[3];
        Position.Evaluate(t, v);
        return new Vector3(v[0], v[1], v[2]);
    }

    /// <summary>Quaternion controls are blended component-wise then normalised, as Granny does.</summary>
    public Quaternion SampleOrientation(float t)
    {
        if (Orientation.IsEmpty) return Quaternion.Identity;
        Span<float> v = stackalloc float[4];
        Orientation.Evaluate(t, v);
        var q = new Quaternion(v[0], v[1], v[2], v[3]);
        float len = q.Length();
        return len > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    /// <summary>Row-major 3x3 scale/shear; identity if the curve is empty.</summary>
    public void SampleScaleShear(float t, Span<float> m9)
    {
        if (ScaleShear.IsEmpty)
        {
            m9.Clear(); m9[0] = m9[4] = m9[8] = 1; return;
        }
        ScaleShear.Evaluate(t, m9);
    }
}

/// <summary>A time-stamped text event (e.g. sound or effect triggers placed by animators).</summary>
public readonly record struct GrannyTextEvent(string Track, float Time, string Text);

/// <summary>A Granny animation as stored in the file (one track group per animated model).</summary>
public sealed class GrannyAnimation
{
    public string Name = "";
    public float Duration;
    public float TimeStep;
    public float Oversampling;
    public string TrackGroupName = "";
    public List<GrannyTransformTrack> TransformTracks = new();
    /// <summary>Scalar/vector tracks such as "Mark" (dimension 1).</summary>
    public Dictionary<string, GrannyCurve> VectorTracks = new();
    public List<GrannyTextEvent> TextEvents = new();
    public int AccumulationFlags;
    public Vector3 LoopTranslation;
}
