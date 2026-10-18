using H5.Assets.Audio;
using H5.Core.IO;

namespace H5.Audio.Tests;

public class ImaAdpcmTests
{
    /// <summary>Builds an MS IMA ADPCM WAVE file from PCM using the reference encoder.</summary>
    static byte[] EncodeWav(short[] interleaved, int channels, int rate, int blockAlign)
    {
        int samplesPerBlock = (blockAlign - 4 * channels) * 8 / (4 * channels) + 1;
        int frames = interleaved.Length / channels;
        var data = new List<byte>();
        var predictor = new int[channels];
        var index = new int[channels];
        for (int start = 0; start < frames; start += samplesPerBlock)
        {
            var block = new byte[blockAlign];
            for (int c = 0; c < channels; c++)
            {
                predictor[c] = interleaved[start * channels + c];
                BitConverter.TryWriteBytes(block.AsSpan(c * 4, 2), (short)predictor[c]);
                block[c * 4 + 2] = (byte)index[c];
            }
            int pos = 4 * channels;
            for (int f = 1; f < samplesPerBlock; f += 8)
            {
                for (int c = 0; c < channels; c++)
                    for (int i = 0; i < 8; i++)
                    {
                        int frame = Math.Min(start + f + i, frames - 1);
                        int nib = ImaAdpcm.EncodeNibble(interleaved[frame * channels + c], ref predictor[c], ref index[c]);
                        block[pos + c * 4 + (i >> 1)] |= (byte)(nib << ((i & 1) * 4));
                    }
                pos += 4 * channels;
            }
            data.AddRange(block);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(20); w.Write((ushort)0x11); w.Write((ushort)channels); w.Write(rate);
        w.Write(rate * blockAlign / samplesPerBlock); w.Write((ushort)blockAlign); w.Write((ushort)4);
        w.Write((ushort)2); w.Write((ushort)samplesPerBlock);
        w.Write("fact"u8); w.Write(4); w.Write(frames);
        w.Write("data"u8); w.Write(data.Count); w.Write(data.ToArray());
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), bytes.Length - 8);
        return bytes;
    }

    static short[] Sine(int frames, int channels, double freq, int rate)
    {
        var s = new short[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                s[f * channels + c] = (short)(12000 * Math.Sin(2 * Math.PI * freq * (c + 1) * f / rate));
        return s;
    }

    [Theory]
    [InlineData(1, 1024)]
    [InlineData(2, 2048)]
    [InlineData(1, 256)]
    public void RoundTripsSineWithinAdpcmError(int channels, int blockAlign)
    {
        const int rate = 44100;
        var pcm = Sine(10000, channels, 440, rate);
        var decoded = ImaAdpcm.DecodeWav(EncodeWav(pcm, channels, rate, blockAlign));

        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(rate, decoded.SampleRate);
        Assert.Equal(pcm.Length, decoded.Samples.Length);
        double err = 0;
        for (int i = 0; i < pcm.Length; i++) err += Math.Abs(pcm[i] - decoded.Samples[i]);
        Assert.True(err / pcm.Length < 300, $"mean abs error {err / pcm.Length:F1}");
    }

    [Fact]
    public void Pcm16PassesThrough()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        short[] samples = { 0, 1000, -1000, short.MaxValue, short.MinValue };
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((ushort)1); w.Write((ushort)1); w.Write(22050);
        w.Write(44100); w.Write((ushort)2); w.Write((ushort)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        Assert.Equal(samples, ImaAdpcm.DecodeWav(ms.ToArray()).Samples);
    }

    /// <summary>Decodes every WAVE sound in the original archives (skipped without an install).</summary>
    [Fact]
    public void DecodesAllOriginalWaveSounds()
    {
        GameInstall install;
        try { install = GameInstall.Locate(); } catch (DirectoryNotFoundException) { return; }
        using var fs = PakFileSystem.OpenInstall(install);
        int waves = 0;
        var failures = new List<string>();
        foreach (var path in fs.Enumerate("bin/Sounds"))
        {
            var bytes = fs.ReadAllBytes(path);
            if (!ImaAdpcm.IsRiffWave(bytes)) continue;
            waves++;
            try
            {
                var a = ImaAdpcm.DecodeWav(bytes);
                if (a.FrameCount == 0 || a.SampleRate < 8000) failures.Add($"{path}: empty or bad rate");
            }
            catch (Exception e) { failures.Add($"{path}: {e.Message}"); }
        }
        Assert.True(waves > 1000, $"only {waves} WAVE files found");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }
}
