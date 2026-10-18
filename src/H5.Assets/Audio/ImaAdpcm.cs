namespace H5.Assets.Audio;

/// <summary>Decoded PCM audio: 16-bit samples, interleaved by channel.</summary>
public sealed class PcmAudio
{
    public int Channels, SampleRate;
    public short[] Samples = Array.Empty<short>();
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;
}

/// <summary>
/// Decoder for the RIFF WAVE files in soundsfx.pak (bin/Sounds/&lt;uid&gt;): Microsoft
/// IMA ADPCM (wFormatTag 0x0011, nearly all of them), Microsoft ADPCM (0x0002, a
/// handful) and plain PCM. Godot's own IMA-ADPCM stream uses a different block
/// layout, so we decode to PCM.
/// </summary>
public static class ImaAdpcm
{
    static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
        253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
        3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    public static bool IsRiffWave(ReadOnlySpan<byte> d) =>
        d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';

    public static PcmAudio DecodeWav(byte[] riff)
    {
        if (!IsRiffWave(riff)) throw new InvalidDataException("not a RIFF WAVE file");
        int format = 0, channels = 0, rate = 0, blockAlign = 0, bits = 0, samplesPerBlock = 0, factFrames = -1;
        short[]? msCoefs = null;
        ReadOnlySpan<byte> data = default;
        int p = 12;
        while (p + 8 <= riff.Length)
        {
            uint id = BitConverter.ToUInt32(riff, p);
            int size = BitConverter.ToInt32(riff, p + 4);
            int body = p + 8;
            size = Math.Min(size, riff.Length - body);
            switch (id)
            {
                case 0x20746d66: // "fmt "
                    format = BitConverter.ToUInt16(riff, body);
                    channels = BitConverter.ToUInt16(riff, body + 2);
                    rate = BitConverter.ToInt32(riff, body + 4);
                    blockAlign = BitConverter.ToUInt16(riff, body + 12);
                    bits = BitConverter.ToUInt16(riff, body + 14);
                    if (size >= 20) samplesPerBlock = BitConverter.ToUInt16(riff, body + 18);
                    if (format == 2 && size >= 22)
                    {
                        int n = BitConverter.ToUInt16(riff, body + 20);
                        msCoefs = new short[n * 2];
                        for (int i = 0; i < n * 2 && body + 22 + i * 2 + 2 <= body + size; i++)
                            msCoefs[i] = BitConverter.ToInt16(riff, body + 22 + i * 2);
                    }
                    break;
                case 0x74636166: // "fact"
                    factFrames = BitConverter.ToInt32(riff, body);
                    break;
                case 0x61746164: // "data"
                    data = riff.AsSpan(body, size);
                    break;
            }
            p = body + size + (size & 1);
        }
        if (channels <= 0) throw new InvalidDataException("WAVE file has no fmt chunk");

        var audio = new PcmAudio { Channels = channels, SampleRate = rate };
        switch (format)
        {
            case 1 when bits == 16:
                audio.Samples = new short[data.Length / 2];
                for (int i = 0; i < audio.Samples.Length; i++) audio.Samples[i] = BitConverter.ToInt16(data.Slice(i * 2, 2));
                break;
            case 1 when bits == 8:
                audio.Samples = new short[data.Length];
                for (int i = 0; i < data.Length; i++) audio.Samples[i] = (short)((data[i] - 128) << 8);
                break;
            case 0x11:
                if (samplesPerBlock == 0) samplesPerBlock = (blockAlign - 4 * channels) * 8 / (4 * channels) + 1;
                audio.Samples = DecodeBlocks(data, channels, blockAlign, samplesPerBlock);
                if (factFrames >= 0 && factFrames * channels < audio.Samples.Length)
                    Array.Resize(ref audio.Samples, factFrames * channels);
                break;
            case 2:
                if (samplesPerBlock == 0) samplesPerBlock = (blockAlign - 7 * channels) * 2 / channels + 2;
                audio.Samples = MsAdpcm.DecodeBlocks(data, channels, blockAlign, samplesPerBlock, msCoefs);
                if (factFrames >= 0 && factFrames * channels < audio.Samples.Length)
                    Array.Resize(ref audio.Samples, factFrames * channels);
                break;
            default:
                throw new NotSupportedException($"WAVE format 0x{format:X4} ({bits} bits)");
        }
        return audio;
    }

    /// <summary>
    /// Decodes Microsoft IMA ADPCM blocks. Each block starts with a 4-byte header per
    /// channel (initial sample i16, step index u8, reserved u8), which is also the
    /// block's first output sample; then groups of 4 bytes (8 nibbles, low nibble
    /// first) per channel, interleaved channel by channel.
    /// </summary>
    public static short[] DecodeBlocks(ReadOnlySpan<byte> data, int channels, int blockAlign, int samplesPerBlock)
    {
        int blocks = data.Length / blockAlign;
        int tail = data.Length % blockAlign;
        int tailFrames = tail > 4 * channels ? (tail - 4 * channels) * 8 / (4 * channels) + 1 : 0;
        var output = new short[(blocks * samplesPerBlock + tailFrames) * channels];
        var predictor = new int[channels];
        var index = new int[channels];
        int outFrame = 0;

        for (int b = 0; b <= blocks; b++)
        {
            int start = b * blockAlign;
            int len = Math.Min(blockAlign, data.Length - start);
            if (len <= 4 * channels) break;
            var block = data.Slice(start, len);
            int frames = b < blocks ? samplesPerBlock : tailFrames;

            for (int c = 0; c < channels; c++)
            {
                predictor[c] = BitConverter.ToInt16(block.Slice(c * 4, 2));
                index[c] = Math.Clamp((int)block[c * 4 + 2], 0, 88);
                output[outFrame * channels + c] = (short)predictor[c];
            }

            int pos = 4 * channels;
            int decoded = 1;
            while (decoded < frames && pos + 4 * channels <= block.Length)
            {
                for (int c = 0; c < channels; c++)
                {
                    var chunk = block.Slice(pos + c * 4, 4);
                    for (int i = 0; i < 8 && decoded + i < frames; i++)
                    {
                        int nibble = (chunk[i >> 1] >> ((i & 1) * 4)) & 0xF;
                        output[(outFrame + decoded + i) * channels + c] = Step(ref predictor[c], ref index[c], nibble);
                    }
                }
                pos += 4 * channels;
                decoded += 8;
            }
            outFrame += frames;
        }
        return output;
    }

    static short Step(ref int predictor, ref int index, int nibble)
    {
        int step = StepTable[index];
        int diff = step >> 3;
        if ((nibble & 1) != 0) diff += step >> 2;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 4) != 0) diff += step;
        predictor = (nibble & 8) != 0 ? predictor - diff : predictor + diff;
        predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88);
        return (short)predictor;
    }

    /// <summary>Reference IMA ADPCM nibble encoder (used by tests and tools).</summary>
    public static int EncodeNibble(int sample, ref int predictor, ref int index)
    {
        int step = StepTable[index];
        int diff = sample - predictor;
        int nibble = 0;
        if (diff < 0) { nibble = 8; diff = -diff; }
        if (diff >= step) { nibble |= 4; diff -= step; }
        if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; }
        if (diff >= step >> 2) { nibble |= 1; }
        Step(ref predictor, ref index, nibble);
        return nibble;
    }
}

/// <summary>Microsoft ADPCM (wFormatTag 0x0002) block decoder.</summary>
public static class MsAdpcm
{
    static readonly int[] Adaptation = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };
    static readonly short[] StandardCoefs = { 256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232 };

    /// <summary>
    /// Each block: per channel a predictor index (u8), then per channel delta (i16),
    /// sample1 (i16), sample2 (i16); samples 2 and 1 are output first; then nibbles,
    /// high nibble first, alternating channels.
    /// </summary>
    public static short[] DecodeBlocks(ReadOnlySpan<byte> data, int channels, int blockAlign, int samplesPerBlock, short[]? coefs)
    {
        coefs ??= StandardCoefs;
        var output = new List<short>(data.Length * 2);
        var c1 = new int[channels]; var c2 = new int[channels];
        var delta = new int[channels]; var s1 = new int[channels]; var s2 = new int[channels];
        for (int start = 0; start + 7 * channels <= data.Length; start += blockAlign)
        {
            var block = data.Slice(start, Math.Min(blockAlign, data.Length - start));
            int p = 0;
            for (int c = 0; c < channels; c++)
            {
                int pi = Math.Min(block[p++], coefs.Length / 2 - 1);
                c1[c] = coefs[pi * 2]; c2[c] = coefs[pi * 2 + 1];
            }
            for (int c = 0; c < channels; c++) { delta[c] = BitConverter.ToInt16(block.Slice(p, 2)); p += 2; }
            for (int c = 0; c < channels; c++) { s1[c] = BitConverter.ToInt16(block.Slice(p, 2)); p += 2; }
            for (int c = 0; c < channels; c++) { s2[c] = BitConverter.ToInt16(block.Slice(p, 2)); p += 2; }
            for (int c = 0; c < channels; c++) output.Add((short)s2[c]);
            for (int c = 0; c < channels; c++) output.Add((short)s1[c]);

            int produced = 2, ch = 0;
            for (; p < block.Length && produced < samplesPerBlock; p++)
            {
                foreach (int nib in new[] { block[p] >> 4, block[p] & 0xF })
                {
                    int signed = nib >= 8 ? nib - 16 : nib;
                    int pred = (s1[ch] * c1[ch] + s2[ch] * c2[ch]) / 256 + signed * delta[ch];
                    pred = Math.Clamp(pred, short.MinValue, short.MaxValue);
                    output.Add((short)pred);
                    s2[ch] = s1[ch]; s1[ch] = pred;
                    delta[ch] = Math.Max(16, Adaptation[nib] * delta[ch] / 256);
                    if (++ch == channels) { ch = 0; produced++; }
                }
            }
        }
        return output.ToArray();
    }
}
