using System.Numerics;

namespace H5.Assets.Effects;

/// <summary>One active cycle of a particle instance: the bake frame it shows.</summary>
public readonly record struct ParticleCycle(float Frame, bool Newest, int CycleIndex);

/// <summary>A camera-facing particle quad ready to draw (corners counter-clockwise from bottom-left).</summary>
public struct ParticleQuad
{
    public Vector3 C0, C1, C2, C3;
    /// <summary>D3DCOLOR 0xAARRGGBB, premultiplied blend expected.</summary>
    public uint Color;
    public int TextureSlot;
    /// <summary>Back-to-front sort key: larger is farther.</summary>
    public float Depth;
}

/// <summary>
/// Port of the original particle playback: cycle scheduling (CParticleAnimator update,
/// H5_Game.exe 0x00b10030) and per-frame quad generation (CStandardParticleEffect,
/// 0x00bf4e80). Engine-neutral: the caller supplies the world matrix and camera basis.
/// </summary>
public static class ParticleSystem
{
    static int RoundEven(float x) => (int)MathF.Round(x, MidpointRounding.ToEven);

    /// <summary>
    /// Active cycles at <paramref name="elapsed"/> seconds after the instance was started.
    /// <paramref name="finished"/> becomes true once no cycle can show particles again.
    /// <paramref name="cycleCountOverride"/> mirrors the engine's runtime override (−1 = use data).
    /// </summary>
    public static List<ParticleCycle> Cycles(ParticleBake bake, InstanceTiming timing, float elapsed,
        out bool finished, int cycleCountOverride = -1)
    {
        var result = new List<ParticleCycle>();
        finished = true;
        float t = (elapsed - timing.Offset) * timing.Speed;
        if (t < 0f) { finished = false; return result; }

        var ages = new List<float>();
        float period = timing.EndCycle;
        if (period == 0f)
        {
            if (timing.CycleCount == 0) { ages.Add(0f); finished = false; }
            else if (t < bake.Length) { ages.Add(t); finished = false; }
        }
        else
        {
            int n = RoundEven(t / period) + 1;
            int cap = cycleCountOverride >= 0 ? cycleCountOverride : timing.CycleCount;
            if (cap != 0 && cap < n) n = cap;
            float age = t - (n - 1) * period;
            for (int i = 0; i < n; i++)
            {
                if (bake.Length <= age) break;
                ages.Add(age);
                age += period;
            }
            finished = cap != 0 && (cap - 1) * period + bake.Length <= t;
        }

        for (int i = 0; i < ages.Count; i++)
        {
            int cycle = period != 0f ? (int)((t - ages[i]) / period + 1f) : 1; // truncating FPU mode
            result.Add(new ParticleCycle(bake.Fps * ages[i], i == 0, cycle));
        }
        return result;
    }

    // View-axis depth jitter per quad corner (H5_Game.exe 0x00f78bd4).
    static readonly float[] Jitter = { 0.02f, -0.05f, 0.07f, -0.12f, 0f, 0.03f, 0.08f, -0.04f };

    // 512-entry sine table with linear interpolation (built at 0x00bf4780).
    static readonly float[] SinTable = BuildSinTable();
    static float[] BuildSinTable()
    {
        var t = new float[1023];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Sin(i * (2 * Math.PI / 512));
        return t;
    }

    static void SinCos(float angle, out float sin, out float cos)
    {
        float x = angle * 81.48733f; // 512 / 2π
        int idx = RoundEven(x - 0.5f);
        float frac = x - idx;
        int i = idx & 0x1ff;
        sin = (SinTable[i + 1] - SinTable[i]) * frac + SinTable[i];
        cos = (SinTable[i + 129] - SinTable[i + 128]) * frac + SinTable[i + 128];
    }

    /// <summary>
    /// Builds the quads for every active cycle. <paramref name="world"/> maps bake positions
    /// (source space) to output space; camera vectors are in output space.
    /// <paramref name="intensity"/> is lighting × fade (1 for unlit, unfaded instances).
    /// <paramref name="stopFrame"/> ≥ 0 stops new particles in the newest cycle (effect stopping).
    /// </summary>
    public static void BuildQuads(ParticleBake bake, ParticleInstanceDef inst, IReadOnlyList<ParticleCycle> cycles,
        Matrix4x4 world, Vector3 camRight, Vector3 camUp, Vector3 camForward, Vector3 wrapCenter,
        float intensity, List<ParticleQuad> output, float stopFrame = -1f, int textureSlotCount = 16)
    {
        float periodFrames = bake.Fps * inst.Timing.EndCycle;
        float half = inst.Scale * 0.5f;
        bool wrap = inst.WrapSize.X != 0f && inst.WrapSize.Y != 0f;

        for (int c = 0; c < cycles.Count; c++)
        {
            var cyc = cycles[c];
            float frame = cyc.Frame;
            for (int i = 0; i < bake.Particles.Length; i++)
            {
                var p = bake.Particles[i];
                float birth = p.BirthFrame;
                if (frame < birth || p.DeathFrame <= frame) continue;
                // Older cycles don't show particles born after the cycle period (they belong to the next cycle).
                if (!cyc.Newest && !(birth < periodFrames)) continue;
                if (stopFrame >= 0f && cyc.Newest && stopFrame <= birth) continue;

                var local = ParticleSampler.Position(p, frame);
                if (wrap)
                {
                    local.X += RoundEven((wrapCenter.X - local.X) / inst.WrapSize.X) * inst.WrapSize.X;
                    local.Y += RoundEven((wrapCenter.Y - local.Y) / inst.WrapSize.Y) * inst.WrapSize.Y;
                }
                var pos = Vector3.Transform(local, world);

                uint color = ParticleSampler.Color(p, frame, intensity);
                if ((color & 0xf8f8f8f8) == 0) continue;
                short slot = ParticleSampler.Texture(p, frame);
                if ((uint)slot >= (uint)textureSlotCount) continue;
                float rot = ParticleSampler.Rotation(p, frame);
                var size = ParticleSampler.Size(p, frame);

                SinCos(rot, out float sn, out float cs);
                float sw = size.X * half, sh = size.Y * half;
                float px = -(sw * inst.Pivot.X), py = -(sh * inst.Pivot.Y);
                float ox = px * cs - py * sn, oy = py * cs + px * sn; // rotated pivot offset
                float ux = cs * sw, uy = sw * sn;                     // half-width axis
                float vx = -(sh * sn), vy = sh * cs;                  // half-height axis
                float jit = (MathF.Abs(sw) + MathF.Abs(sh));
                int u = (i * i + c) & 7, step = i | 1;

                Vector3 Corner(float x, float y, int j) => camRight * x + camUp * y + camForward * (jit * Jitter[j]) + pos;
                var q = new ParticleQuad
                {
                    C0 = Corner(ox - (vx + ux), oy - (vy + uy), u),
                    C1 = Corner((ux - vx) + ox, (uy - vy) + oy, u = (u + step) & 7),
                    C2 = Corner(vx + ux + ox, vy + uy + oy, u = (u + step) & 7),
                    C3 = Corner((vx - ux) + ox, (vy - uy) + oy, (u + step) & 7),
                    Color = color,
                    TextureSlot = slot,
                    Depth = inst.Priority * 0.01f + Vector3.Dot(pos, camForward),
                };
                output.Add(q);
            }
        }
    }
}
