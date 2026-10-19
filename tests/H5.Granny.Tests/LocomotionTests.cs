using System.Numerics;
using H5.Assets.Granny;
using H5.Assets.Model;

namespace H5.Granny.Tests;

/// <summary>
/// Move clips are played in place by shifting the whole skeleton back by the clip's travel
/// (CharacterBuilder.AddInPlaceTrack). Held props are root-level bones that travel with the body;
/// they must end up in place too, or they run ahead and snap back every cycle (beta bug report:
/// "the things troops and heroes are holding glitch around while the hero is moving").
/// </summary>
public class LocomotionTests(GameFixture game) : IClassFixture<GameFixture>
{
    [Fact]
    public void SyntheticPropBoneTravelsWithTheBody()
    {
        var s = new SkeletonData();
        s.Bones.Add(new Bone { Name = "Global", Parent = -1, Rotation = Quaternion.Identity, Scale = Vector3.One });
        s.Bones.Add(new Bone { Name = "Hip", Parent = 0, Rotation = Quaternion.Identity, Scale = Vector3.One });
        s.Bones.Add(new Bone { Name = "Sword", Parent = 0, Rotation = Quaternion.Identity, Scale = Vector3.One });
        var clip = new AnimationClip { Name = "move", Duration = 1 };
        foreach (var (name, x) in new[] { ("Hip", 0f), ("Sword", 0.5f) })
            clip.Tracks.Add(new BoneTrack
            {
                BoneName = name,
                TranslationTimes = new[] { 0f, 1f }, Translations = new[] { new Vector3(x, 0, 1), new Vector3(x, -4, 1) },
                RotationTimes = new[] { 0f }, Rotations = new[] { Quaternion.Identity },
                ScaleTimes = new[] { 0f }, Scales = new[] { Vector3.One },
            });
        var travel = GrannyPose.LocomotionTravel(s, clip, 4)!.Value;
        Assert.Equal(new Vector3(0, -4, 0), travel);
        AssertInPlace(s, clip, travel, "Hip", "Sword");
    }

    [Theory]
    [InlineData("Characters/Heroes/Knight_LOD.(Character).xdb", "Knight_Global")]          // Haven hero: rider + weapon on a horse
    [InlineData("Characters/Creatures/Dwarves/T1_Dwarf/Defender_LOD.(Character).xdb", "Hammer")]
    [InlineData("Characters/Heroes/Agrael_mounted_LOD.(Character).xdb", "Staff")]
    public void AdventureMovePropsStayWithTheBody(string character, string prop)
    {
        if (game.Anims is not { } anims || !game.Fs!.Exists(character)) return;
        var (model, arena, adv) = anims.ReadCharacter(character);
        var skel = anims.LoadSkeletonForModel(model!)!;
        var move = anims.LoadAnimSet((adv ?? arena)!)["move"];
        var travel = GrannyPose.LocomotionTravel(skel, move.Clip, move.Entry.MovementSpeed);
        Assert.NotNull(travel);
        Assert.True(travel!.Value.Length() > 1, $"travel {travel}"); // the prop really moves with the clip
        AssertInPlace(skel, move.Clip, travel!.Value, prop);
    }

    /// <summary>With the rigid shift applied, the named bones end the cycle where they started.</summary>
    static void AssertInPlace(SkeletonData s, AnimationClip clip, Vector3 travel, params string[] bones)
    {
        var w0 = GrannyPose.AnimatedWorld(s, clip, 0);
        var w1 = GrannyPose.AnimatedWorld(s, clip, clip.Duration);
        foreach (var name in bones)
        {
            int i = s.IndexOf(name);
            Assert.True(i >= 0, name);
            var d = w1[i].Translation - travel - w0[i].Translation;
            Assert.True(MathF.Sqrt(d.X * d.X + d.Y * d.Y) < 0.05f, $"{name} drifts {d} after the in-place shift");
        }
    }
}
