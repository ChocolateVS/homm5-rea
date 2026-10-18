using H5.Assets.Granny;

namespace H5.Granny.Tests;

/// <summary>Curve evaluation tests that need no game data.</summary>
public class CurveTests
{
    static float Eval(GrannyCurve c, float t)
    {
        Span<float> v = stackalloc float[c.Dimension];
        c.Evaluate(t, v);
        return v[0];
    }

    [Fact]
    public void LinearCurveHitsControlsAtKnots()
    {
        var c = new GrannyCurve { Degree = 1, Dimension = 1, Knots = new[] { 0f, 1f, 3f }, Controls = new[] { 2f, 4f, 0f } };
        Assert.Equal(2f, Eval(c, 0f), 5);
        Assert.Equal(4f, Eval(c, 1f), 5);
        Assert.Equal(3f, Eval(c, 0.5f), 5);
        Assert.Equal(2f, Eval(c, 2f), 5);
        Assert.Equal(0f, Eval(c, 3f), 5);
        Assert.Equal(0f, Eval(c, 10f), 5); // clamps past the end
    }

    [Fact]
    public void QuadraticCurveIsClampedAtBothEnds()
    {
        // Granny layout: the first `degree` knots are clamped to zero.
        var c = new GrannyCurve { Degree = 2, Dimension = 1, Knots = new[] { 0f, 0.001f, 1f, 2f }, Controls = new[] { 1f, 5f, 5f, 3f } };
        Assert.Equal(1f, Eval(c, 0f), 4);
        Assert.Equal(3f, Eval(c, 2f), 4);
        Assert.InRange(Eval(c, 1f), 1f, 5f);
    }

    [Fact]
    public void QuadraticCurveIsContinuous()
    {
        var c = new GrannyCurve { Degree = 2, Dimension = 1, Knots = new[] { 0f, 0.0016f, 0.033f, 0.066f, 0.1f, 0.133f }, Controls = new[] { 0f, 1f, -1f, 2f, 0f, 1f } };
        float prev = Eval(c, 0);
        for (float t = 0.001f; t <= 0.133f; t += 0.001f)
        {
            float v = Eval(c, t);
            Assert.True(MathF.Abs(v - prev) < 0.25f, $"jump at {t}: {prev} -> {v}");
            prev = v;
        }
    }

    [Fact]
    public void ConstantAndEmptyCurves()
    {
        var k = new GrannyCurve { Degree = 0, Dimension = 3, Knots = new[] { 0f }, Controls = new[] { 1f, 2f, 3f } };
        var v = new float[3];
        k.Evaluate(5f, v);
        Assert.Equal(new[] { 1f, 2f, 3f }, v);

        var empty = new GrannyCurve { Degree = 0, Dimension = 4 };
        Assert.True(empty.IsEmpty);
        var track = new GrannyTransformTrack { Orientation = empty };
        Assert.Equal(System.Numerics.Quaternion.Identity, track.SampleOrientation(0.3f));
    }

    [Fact]
    public void EmptyChannelsBakeToIdentityKeys()
    {
        var a = new GrannyAnimation { Duration = 1f };
        a.TransformTracks.Add(new GrannyTransformTrack { Name = "Bone" });
        var clip = GrannyConvert.ToClip(a);
        var t = clip.Tracks.Single();
        Assert.Equal(System.Numerics.Vector3.Zero, t.Translations.Single());
        Assert.Equal(System.Numerics.Quaternion.Identity, t.Rotations.Single());
        Assert.Equal(System.Numerics.Vector3.One, t.Scales.Single());
    }
}
