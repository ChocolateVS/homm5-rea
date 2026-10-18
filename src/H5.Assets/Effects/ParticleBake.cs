using System.Buffers.Binary;
using System.Numerics;

namespace H5.Assets.Effects;

// Decoder and samplers for bin/effects/<uid>: particle systems baked from Maya into
// per-particle keyframe tracks. The file is a memory image fixed up in place by
// CParticlesLoader (H5_Game.exe 0x00b67f60); the samplers mirror the engine's
// per-track evaluators (0x00bf5fc0 position, 0x00bf60e0 rotation, 0x00bf61d0 size,
// 0x00bf5e30 colour, 0x00bf5f30 texture index). See docs/formats/effects.md.

public readonly record struct PosKey(short Frame, Vector3 Value);
public readonly record struct FloatKey(short Frame, float Value);
public readonly record struct Vec2Key(short Frame, Vector2 Value);
/// <summary>Colour as stored: a D3DCOLOR (0xAARRGGBB).</summary>
public readonly record struct ColorKey(short Frame, uint Argb);
public readonly record struct IndexKey(short Frame, short Value);

public sealed class BakedParticle
{
    /// <summary>The particle is visible while BirthFrame ≤ frame &lt; DeathFrame.</summary>
    public short BirthFrame, DeathFrame;
    public PosKey[] Position = Array.Empty<PosKey>();
    public FloatKey[] Rotation = Array.Empty<FloatKey>();
    public Vec2Key[] Size = Array.Empty<Vec2Key>();
    public ColorKey[] Color = Array.Empty<ColorKey>();
    /// <summary>Index into the ParticleInstance's 16 texture slots.</summary>
    public IndexKey[] Texture = Array.Empty<IndexKey>();
}

public sealed class ParticleBake
{
    /// <summary>Length of the baked simulation in seconds.</summary>
    public float Length;
    /// <summary>Bake sample rate; frames are Length*Fps long.</summary>
    public float Fps;
    public BakedParticle[] Particles = Array.Empty<BakedParticle>();

    public const int RecordSize = 34;
    static readonly int[] KeySize = { 14, 6, 10, 6, 4 };

    public static ParticleBake Read(ReadOnlySpan<byte> d)
    {
        var bake = new ParticleBake();
        if (d.Length < 16) return bake;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(d);
        bake.Length = BinaryPrimitives.ReadSingleLittleEndian(d[4..]);
        bake.Fps = BinaryPrimitives.ReadSingleLittleEndian(d[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        // One stock blob is all zeros (an empty export); size 0 / count 0 decodes to nothing.
        if (size == 0 || count <= 0) return bake;
        if (16L + (long)count * RecordSize > d.Length)
            throw new InvalidDataException($"particle count {count} exceeds blob size {d.Length}");

        bake.Particles = new BakedParticle[count];
        for (int i = 0; i < count; i++)
        {
            var r = d.Slice(16 + i * RecordSize, RecordSize);
            var p = new BakedParticle
            {
                BirthFrame = BinaryPrimitives.ReadInt16LittleEndian(r),
                DeathFrame = BinaryPrimitives.ReadInt16LittleEndian(r[2..]),
            };
            for (int t = 0; t < 5; t++)
            {
                int n = BinaryPrimitives.ReadUInt16LittleEndian(r[(4 + t * 6)..]);
                // Offsets are relative to the image start, which follows the leading size field.
                int off = 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(6 + t * 6)..]);
                if (n == 0) continue;
                if (off < 0 || off + n * KeySize[t] > d.Length)
                    throw new InvalidDataException($"particle {i} track {t} out of range");
                var k = d.Slice(off, n * KeySize[t]);
                switch (t)
                {
                    case 0:
                        p.Position = new PosKey[n];
                        for (int j = 0; j < n; j++)
                        {
                            var e = k[(j * 14)..];
                            p.Position[j] = new PosKey(BinaryPrimitives.ReadInt16LittleEndian(e),
                                new Vector3(F(e, 2), F(e, 6), F(e, 10)));
                        }
                        break;
                    case 1:
                        p.Rotation = new FloatKey[n];
                        for (int j = 0; j < n; j++)
                        {
                            var e = k[(j * 6)..];
                            p.Rotation[j] = new FloatKey(BinaryPrimitives.ReadInt16LittleEndian(e), F(e, 2));
                        }
                        break;
                    case 2:
                        p.Size = new Vec2Key[n];
                        for (int j = 0; j < n; j++)
                        {
                            var e = k[(j * 10)..];
                            p.Size[j] = new Vec2Key(BinaryPrimitives.ReadInt16LittleEndian(e), new Vector2(F(e, 2), F(e, 6)));
                        }
                        break;
                    case 3:
                        p.Color = new ColorKey[n];
                        for (int j = 0; j < n; j++)
                        {
                            var e = k[(j * 6)..];
                            p.Color[j] = new ColorKey(BinaryPrimitives.ReadInt16LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[2..]));
                        }
                        break;
                    case 4:
                        p.Texture = new IndexKey[n];
                        for (int j = 0; j < n; j++)
                        {
                            var e = k[(j * 4)..];
                            p.Texture[j] = new IndexKey(BinaryPrimitives.ReadInt16LittleEndian(e), BinaryPrimitives.ReadInt16LittleEndian(e[2..]));
                        }
                        break;
                }
            }
            bake.Particles[i] = p;
        }
        return bake;
    }

    static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
}

/// <summary>Track evaluation exactly as the engine does it.</summary>
public static class ParticleSampler
{
    /// <summary>x87 FISTP under the default round-to-nearest-even control word.</summary>
    static int RoundEven(float x) => (int)MathF.Round(x, MidpointRounding.ToEven);

    /// <summary>
    /// Binary search used by every track: finds lo/hi key indices around <paramref name="frame"/>,
    /// comparing key frames with (short)round(frame − 0.5). Returns the clamped blend factor.
    /// </summary>
    static float Locate<T>(T[] keys, Func<T, short> frameOf, float frame, out int lo, out int hi)
    {
        lo = 0;
        hi = keys.Length - 1;
        short f = unchecked((short)RoundEven(frame - 0.5f));
        if (hi > 1)
        {
            int top = hi;
            do
            {
                int mid = (top + lo) / 2;
                int next = mid;
                if (frameOf(keys[mid]) <= f) { next = top; lo = mid; }
                top = next;
                hi = next;
            } while (hi - lo > 1);
        }
        float k0 = frameOf(keys[lo]);
        float t = (frame - k0) / (frameOf(keys[hi]) - k0);
        if (t > 1f) t = 1f;
        else if (t < 0f) t = 0f; // NaN (equal frames) also falls through unchanged, as in the exe
        return t;
    }

    public static Vector3 Position(BakedParticle p, float frame)
    {
        var k = p.Position;
        if (k.Length == 0) return Vector3.Zero;
        if (k.Length == 1) return k[0].Value;
        float t = Locate(k, x => x.Frame, frame, out int lo, out int hi);
        return (1f - t) * k[lo].Value + t * k[hi].Value;
    }

    public static float Rotation(BakedParticle p, float frame)
    {
        var k = p.Rotation;
        if (k.Length == 0) return 0f;
        if (k.Length == 1) return k[0].Value;
        float t = Locate(k, x => x.Frame, frame, out int lo, out int hi);
        return t * k[hi].Value + (1f - t) * k[lo].Value;
    }

    public static Vector2 Size(BakedParticle p, float frame)
    {
        var k = p.Size;
        if (k.Length == 0) return Vector2.Zero;
        if (k.Length == 1) return k[0].Value;
        float t = Locate(k, x => x.Frame, frame, out int lo, out int hi);
        return (1f - t) * k[lo].Value + t * k[hi].Value;
    }

    public static short Texture(BakedParticle p, float frame)
    {
        var k = p.Texture;
        if (k.Length == 0) return 0;
        if (k.Length == 1) return k[0].Value;
        Locate(k, x => x.Frame, frame, out int lo, out _);
        return k[lo].Value; // stepped: the lower key is used without interpolation
    }

    /// <summary>
    /// Colour at <paramref name="frame"/> scaled by <paramref name="intensity"/> (lighting × fade),
    /// reproducing the MMX fixed-point blend (0x00b67db0 / 0x00b67e50).
    /// </summary>
    public static uint Color(BakedParticle p, float frame, float intensity)
    {
        var k = p.Color;
        if (k.Length == 0) return 0;
        if (k.Length == 1) return ScaleColor(k[0].Argb, intensity);
        float t = Locate(k, x => x.Frame, frame, out int lo, out int hi);
        return BlendColor(k[lo].Argb, k[hi].Argb, intensity - t * intensity, t * intensity);
    }

    const float Fixed = 16384f;

    /// <summary>c0*w0 + c1*w1 per channel, as the engine's pmulhw path computes it.</summary>
    public static uint BlendColor(uint c0, uint c1, float w0, float w1)
    {
        short a = unchecked((short)RoundEven(w0 * Fixed));
        short b = unchecked((short)(RoundEven((w0 + w1) * Fixed) - a));
        uint result = 0;
        for (int ch = 0; ch < 4; ch++)
        {
            int x0 = (int)((c0 >> (ch * 8)) & 0xff), x1 = (int)((c1 >> (ch * 8)) & 0xff);
            short e0 = unchecked((short)((ushort)(x0 * 0x101) >> 2)), e1 = unchecked((short)((ushort)(x1 * 0x101) >> 2));
            short m0 = unchecked((short)((e0 * a) >> 16)), m1 = unchecked((short)((e1 * b) >> 16));
            ushort sum = unchecked((ushort)(short)(m0 + m1));
            result |= Saturate(sum >> 4) << (ch * 8);
        }
        return result;
    }

    public static uint ScaleColor(uint c, float w)
    {
        short a = unchecked((short)RoundEven(w * Fixed));
        uint result = 0;
        for (int ch = 0; ch < 4; ch++)
        {
            int x = (int)((c >> (ch * 8)) & 0xff);
            short e = unchecked((short)((ushort)(x * 0x101) >> 2));
            ushort m = unchecked((ushort)(short)((e * a) >> 16));
            result |= Saturate(m >> 4) << (ch * 8);
        }
        return result;
    }

    // The exe shifts the 16-bit sum as unsigned and clamps values above 255 to 255.
    static uint Saturate(int v)
    {
        uint u = (uint)v & 0xffff;
        return u > 255 ? 255u : u;
    }
}
