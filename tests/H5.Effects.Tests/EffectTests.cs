using System.Numerics;
using H5.Assets.Effects;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Effects.Tests;

public class EffectTests
{
    static readonly Lazy<PakFileSystem> Fs = new(() => PakFileSystem.OpenInstall(GameInstall.Locate()));
    static readonly Lazy<EffectLoader> Loader = new(() => new EffectLoader(new XdbLoader(Fs.Value)));

    [Fact]
    public void EveryParticleBakeDecodes()
    {
        int n = 0, particles = 0;
        foreach (var path in Fs.Value.Enumerate("bin/effects/"))
        {
            var bake = ParticleBake.Read(Fs.Value.ReadAllBytes(path));
            foreach (var p in bake.Particles)
            {
                Assert.True(p.BirthFrame <= p.DeathFrame, $"{path}: birth after death");
                foreach (var k in p.Position) Assert.True(float.IsFinite(k.Value.X) && float.IsFinite(k.Value.Y) && float.IsFinite(k.Value.Z), path);
            }
            particles += bake.Particles.Length;
            n++;
        }
        Assert.True(n >= 1900, $"only {n} blobs");
        Assert.True(particles > 10000);
    }

    [Fact]
    public void DecodesSmallBakeExactly()
    {
        // 90-byte blob, hand-decoded against the exe fixup (0x00b67f60).
        var bake = ParticleBake.Read(Fs.Value.ReadAllBytes("bin/effects/0693F7D4-B6FB-47DB-871F-969C9EACA028"));
        Assert.Equal(30f, bake.Fps);
        Assert.Equal(3.9666667f, bake.Length, 5);
        var p = Assert.Single(bake.Particles);
        Assert.Equal(0, p.BirthFrame);
        Assert.Equal(119, p.DeathFrame);
        Assert.Single(p.Position);
        Assert.Equal(MathF.PI / 4, Assert.Single(p.Rotation).Value, 5);
        Assert.Equal(new Vector2(2, 2), Assert.Single(p.Size).Value);
        Assert.Equal(0x39ff3f00u, Assert.Single(p.Color).Argb);
        Assert.Equal(0, Assert.Single(p.Texture).Value);
    }

    [Fact]
    public void ColorBlendMatchesFixedPointPath()
    {
        // Full intensity keeps the colour (the c*257/256 expansion rounds back down).
        Assert.Equal(0xff804020u, ParticleSampler.ScaleColor(0xff804020u, 1f));
        Assert.Equal(0u, ParticleSampler.ScaleColor(0xffffffffu, 0f));
        // Halfway between black and white at full intensity.
        uint mid = ParticleSampler.BlendColor(0x00000000u, 0xffffffffu, 0.5f, 0.5f);
        Assert.InRange((int)(mid & 0xff), 126, 128);
    }

    [Fact]
    public void SamplersInterpolateLinearlyBetweenKeys()
    {
        var p = new BakedParticle
        {
            Position = new[] { new PosKey(0, Vector3.Zero), new PosKey(10, new Vector3(10, 0, 0)), new PosKey(20, new Vector3(10, 10, 0)) },
            Texture = new[] { new IndexKey(0, 1), new IndexKey(10, 2), new IndexKey(20, 3) },
        };
        Assert.Equal(5f, ParticleSampler.Position(p, 5).X, 4);
        Assert.Equal(new Vector3(10, 5, 0), ParticleSampler.Position(p, 15));
        Assert.Equal(new Vector3(10, 10, 0), ParticleSampler.Position(p, 30)); // clamped
        Assert.Equal(1, ParticleSampler.Texture(p, 9.9f)); // stepped, no interpolation
        Assert.Equal(2, ParticleSampler.Texture(p, 11f));
        // Engine quirk kept on purpose: the search never selects the last key, so the
        // final texture index is never shown (0x00bf5f30), and with two keys only the first.
        Assert.Equal(2, ParticleSampler.Texture(p, 25f));
        var two = new BakedParticle { Texture = new[] { new IndexKey(0, 1), new IndexKey(10, 2) } };
        Assert.Equal(1, ParticleSampler.Texture(two, 15f));
    }

    [Fact]
    public void CycleSchedulingFollowsEngineRules()
    {
        var bake = new ParticleBake { Length = 2f, Fps = 30f };
        var once = new InstanceTiming { EndCycle = 0, CycleCount = 1 };
        var c = ParticleSystem.Cycles(bake, once, 1f, out bool done);
        Assert.False(done);
        Assert.Equal(30f, Assert.Single(c).Frame, 4);
        Assert.Empty(ParticleSystem.Cycles(bake, once, 2.5f, out done));
        Assert.True(done);

        // Delay: nothing before Offset, not finished.
        Assert.Empty(ParticleSystem.Cycles(bake, new InstanceTiming { Offset = 1f, CycleCount = 1 }, 0.5f, out done));
        Assert.False(done);

        // Looping every second with a 2 s bake: two overlapping cycles, newest first, never finishes.
        var loop = new InstanceTiming { EndCycle = 1f, CycleCount = 0 };
        var cycles = ParticleSystem.Cycles(bake, loop, 5.2f, out done);
        Assert.False(done);
        Assert.True(cycles.Count >= 2);
        Assert.True(cycles[0].Newest);
        Assert.True(cycles.Skip(1).All(x => !x.Newest));

        // Two cycles of a 1 s period finish after (2-1)*1 + 2 = 3 s.
        var two = new InstanceTiming { EndCycle = 1f, CycleCount = 2 };
        ParticleSystem.Cycles(bake, two, 2.9f, out done);
        Assert.False(done);
        ParticleSystem.Cycles(bake, two, 3.0f, out done);
        Assert.True(done);
    }

    [Fact]
    public void EveryAnimLightDecodes()
    {
        int n = 0;
        foreach (var path in Fs.Value.Enumerate("bin/Lights/"))
        {
            var l = EffectLoader.ReadAnimLight(Fs.Value.ReadAllBytes(path));
            Assert.True(l.Fps > 0, path);
            Assert.True(l.Color.Count > 0 || l.Range.Count > 0, path);
            n++;
        }
        Assert.True(n >= 90);
    }

    [Fact]
    public void EveryEffectXdbLoadsAndResolvesBakes()
    {
        int effects = 0, instances = 0, missingBakes = 0;
        foreach (var path in Fs.Value.Enumerate("Effects/").Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)))
        {
            if (Loader.Value.TryLoadRoot(path)?.Name.LocalName != "Effect") continue;
            var def = Loader.Value.LoadEffect(path)!;
            effects++;
            foreach (var pi in def.Particles)
            {
                instances++;
                if (Loader.Value.LoadBake(pi.BakeUid) == null) missingBakes++;
            }
        }
        Assert.True(effects > 1500, $"{effects} effects");
        // A handful of stock instances reference particles whose bake was never exported.
        Assert.True(missingBakes * 50 < instances, $"{missingBakes}/{instances} instances without bake");
    }

    [Fact]
    public void LoadsShotsAndSpellVisuals()
    {
        var archer = Loader.Value.LoadShot("GameMechanics/Shot/Creatures/Haven/Archer.xdb")!;
        Assert.Equal(0.03f, archer.Missile.Speed);
        Assert.Equal(0.03f, archer.Missile.Height);
        Assert.NotNull(archer.Missile.ModelPath);

        var fireball = Assert.Single(Loader.Value.LoadSpellVisuals("GameMechanics/Spell/Combat_Spells/DestructiveMagic/Fireball.xdb"));
        Assert.Equal("Effects/_(Effect)/Spells/FireBallHit.xdb", fireball.EffectPath);
        var hit = Loader.Value.LoadEffect(fireball.EffectPath!)!;
        Assert.Equal(2.5f, hit.Duration);
        Assert.NotEmpty(hit.Lights);
        Assert.NotNull(Loader.Value.LoadLight(hit.Lights[0].LightUid));
    }
}
