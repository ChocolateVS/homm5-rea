using H5.Adventure.Engine;

namespace H5.Adventure.Tests;

/// <summary>Adventure camera model vs H5_Game.exe (docs/rules/CAMERA.md, "Adventure camera").</summary>
public class MissionCameraTests
{
    const float Deg = MathF.PI / 180f;

    [Fact]
    public void DefaultPoseIsDefPosAtMapCentre()
    {
        var c = new MissionCamera(72, 96);
        Assert.Equal(25f, c.Current.Rod);
        Assert.Equal(-53f * Deg, c.Current.Pitch, 5);
        Assert.Equal(16f * Deg, c.Current.Yaw, 5);
        Assert.Equal(72f, c.Current.X);   // W in world units = map centre (2 units per tile)
        Assert.Equal(96f, c.Current.Y);
        Assert.Equal(144f, c.MapWidth);
    }

    [Fact]
    public void SoftLimitFadesInputAndSpringsBack()
    {
        // 69.5 is half-way between soft 69 and hard 70: input towards the hard limit is halved.
        Assert.Equal(69.6f, MissionCamera.SoftAdd(69.5f, 11, 69, 10, 70, 0.2f, 0.016f, 0.03f), 4);
        Assert.Equal(70f, MissionCamera.SoftAdd(69.5f, 11, 69, 10, 70, 5f, 0.016f, 0.03f));
        // No input: back towards the soft limit with base 0.1 per second.
        Assert.Equal(69.1f, MissionCamera.SoftAdd(70f, 11, 69, 10, 70, 0, 1f, 0.03f), 4);
        // Inside the soft range nothing springs.
        Assert.Equal(40f, MissionCamera.SoftAdd(40f, 11, 69, 10, 70, 0, 1f, 0.03f));
    }

    [Fact]
    public void SpeedFactorRunsFromOneToTwoOverTheSoftRodRange()
    {
        Assert.Equal(1f, MissionCamera.SpeedFactor(11));
        Assert.Equal(1.5f, MissionCamera.SpeedFactor(40));
        Assert.Equal(2f, MissionCamera.SpeedFactor(69));
        Assert.Equal(1f, MissionCamera.SpeedFactor(5));
    }

    [Fact]
    public void WheelNotchUsesRodProportionalZoom()
    {
        var c = new MissionCamera(72, 72);
        c.Step(0.016f, 0, 0, 0, 0, 0.75f);
        // (|25|·5 + 1)·0.3·0.75 = 28.35
        Assert.Equal(53.35f, c.Target.Rod, 3);
        Assert.True(c.Current.Rod > 25 && c.Current.Rod < 53.35f);   // glides
        c.Step(0.016f, 0, 0, 0, 0, 5f);
        Assert.Equal(70f, c.Target.Rod);                               // hard limit
    }

    [Fact]
    public void YawIsFreeAndPitchIsLimited()
    {
        var c = new MissionCamera(72, 72);
        for (int i = 0; i < 40; i++) c.Step(0.05f, 0.1f, 0, 0, 0, 0);
        Assert.True(c.Target.Yaw < 16f * Deg - MathF.PI);              // past ±180°, no clamp
        for (int i = 0; i < 40; i++) c.Step(0.05f, 0, 0.1f, 0, 0, 0);
        // Pushing at the hard limit: the faded input reaches 0 there, so the spring pulls back a little (007d4340).
        Assert.InRange(c.Target.Pitch, -31f * Deg, -30f * Deg);
    }

    [Fact]
    public void ExtendedLimitsOnlyWhenSelected()
    {
        var c = new MissionCamera(72, 72) { Hard = MissionCamera.ExtendedHard, Soft = MissionCamera.ExtendedSoft };
        for (int i = 0; i < 40; i++) c.Step(0.05f, 0, -0.1f, 0, 0, 5f);
        Assert.InRange(c.Target.Rod, 159.5f, 160f);
        Assert.InRange(c.Target.Pitch, -85f * Deg, -84f * Deg);
        Assert.Equal(-55f * Deg, MissionCamera.StockHard.MinPitch, 5);
    }

    [Fact]
    public void GlideApproachesExponentially()
    {
        Assert.Equal(0.1f, MissionCamera.Approach(0, 1, 0.1, 1f, 0.001f), 5);
        Assert.Equal(5f, MissionCamera.Approach(5, 5.01f, 0.1, 1f, 0.03f));
    }

    [Fact]
    public void EdgeScrollSnapsAndClampsToTheMapMargin()
    {
        var c = new MissionCamera(10, 10) { };
        c.ResetToDefault();
        c.Current.Yaw = c.Target.Yaw = 0;
        for (int i = 0; i < 100; i++) c.EdgeScroll(0.05f, 0, 1);       // north
        Assert.Equal(19f, c.Current.Y);                                 // 2H − 1
        Assert.Equal(c.Current.Y, c.Target.Y);
        Assert.False(c.EdgeScroll(0.05f, 0, 0));
    }
}
