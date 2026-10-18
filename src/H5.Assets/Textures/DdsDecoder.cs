namespace H5.Assets.Textures;

/// <summary>Decoded top mip level as RGBA8 (row-major, top row first).</summary>
public sealed class RgbaImage
{
    public int Width, Height;
    public byte[] Pixels = Array.Empty<byte>();

    public RgbaImage(int w, int h) { Width = w; Height = h; Pixels = new byte[w * h * 4]; }

    public uint Sample(float u, float v)
    {
        // Wrap addressing, nearest filter; enough for previews.
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        int x = Math.Clamp((int)(u * Width), 0, Width - 1), y = Math.Clamp((int)(v * Height), 0, Height - 1);
        int i = (y * Width + x) * 4;
        return (uint)(Pixels[i] | Pixels[i + 1] << 8 | Pixels[i + 2] << 16 | Pixels[i + 3] << 24);
    }
}

/// <summary>
/// DDS decoder for the formats present in the stock archives: DXT1/DXT3/DXT5 (+DXT2/4),
/// and uncompressed 32/24/16-bit RGB(A) described by bit masks.
/// </summary>
public static class DdsDecoder
{
    public static RgbaImage Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 128 || BitConverter.ToUInt32(d) != 0x20534444) throw new InvalidDataException("not a DDS file");
        int height = BitConverter.ToInt32(d[12..]);
        int width = BitConverter.ToInt32(d[16..]);
        uint pfFlags = BitConverter.ToUInt32(d[80..]);
        uint fourCC = BitConverter.ToUInt32(d[84..]);
        int bits = BitConverter.ToInt32(d[88..]);
        uint rMask = BitConverter.ToUInt32(d[92..]), gMask = BitConverter.ToUInt32(d[96..]),
             bMask = BitConverter.ToUInt32(d[100..]), aMask = BitConverter.ToUInt32(d[104..]);
        var src = d[128..];
        var img = new RgbaImage(width, height);

        if ((pfFlags & 4) != 0)
        {
            string fcc = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(fourCC));
            switch (fcc)
            {
                case "DXT1": DecodeBlocks(src, img, 8, DecodeDxt1Block); break;
                case "DXT2": case "DXT3": DecodeBlocks(src, img, 16, DecodeDxt3Block); break;
                case "DXT4": case "DXT5": DecodeBlocks(src, img, 16, DecodeDxt5Block); break;
                default: throw new NotSupportedException($"DDS fourCC {fcc}");
            }
            return img;
        }

        if ((pfFlags & 0x40) == 0 && (pfFlags & 0x20000) == 0)
            throw new NotSupportedException($"DDS pixel format flags 0x{pfFlags:x}");
        bool hasAlpha = (pfFlags & 1) != 0 && aMask != 0;
        if ((pfFlags & 0x20000) != 0 && rMask == 0) { rMask = gMask = bMask = (1u << Math.Min(bits, 31)) - 1; } // luminance
        int bpp = bits / 8;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * bpp;
                uint px = 0;
                for (int k = 0; k < bpp; k++) px |= (uint)src[o + k] << (8 * k);
                int i = (y * width + x) * 4;
                img.Pixels[i] = Extract(px, rMask);
                img.Pixels[i + 1] = Extract(px, gMask);
                img.Pixels[i + 2] = Extract(px, bMask);
                img.Pixels[i + 3] = hasAlpha ? Extract(px, aMask) : (byte)255;
            }
        return img;
    }

    static byte Extract(uint px, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((px & mask) >> shift) * 255 / max);
    }

    delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<uint> out16);

    static void DecodeBlocks(ReadOnlySpan<byte> src, RgbaImage img, int blockSize, BlockDecoder dec)
    {
        int bw = Math.Max(1, (img.Width + 3) / 4), bh = Math.Max(1, (img.Height + 3) / 4);
        Span<uint> px = stackalloc uint[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int off = (by * bw + bx) * blockSize;
                if (off + blockSize > src.Length) return;
                dec(src.Slice(off, blockSize), px);
                for (int j = 0; j < 4; j++)
                    for (int i = 0; i < 4; i++)
                    {
                        int x = bx * 4 + i, y = by * 4 + j;
                        if (x >= img.Width || y >= img.Height) continue;
                        int o = (y * img.Width + x) * 4;
                        uint c = px[j * 4 + i];
                        img.Pixels[o] = (byte)c; img.Pixels[o + 1] = (byte)(c >> 8);
                        img.Pixels[o + 2] = (byte)(c >> 16); img.Pixels[o + 3] = (byte)(c >> 24);
                    }
            }
    }

    static uint Rgb565(ushort c, byte a = 255)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        return (uint)((r << 3 | r >> 2) | (g << 2 | g >> 4) << 8 | (b << 3 | b >> 2) << 16 | a << 24);
    }

    static uint Lerp(uint a, uint b, int num, int den)
    {
        uint r = 0;
        for (int s = 0; s < 32; s += 8)
        {
            int ca = (int)(a >> s) & 255, cb = (int)(b >> s) & 255;
            r |= (uint)((ca * (den - num) + cb * num) / den) << s;
        }
        return r;
    }

    static void ColorBlock(ReadOnlySpan<byte> b, Span<uint> out16, bool allowPunchThrough)
    {
        ushort c0 = BitConverter.ToUInt16(b), c1 = BitConverter.ToUInt16(b[2..]);
        Span<uint> pal = stackalloc uint[4];
        pal[0] = Rgb565(c0); pal[1] = Rgb565(c1);
        if (c0 > c1 || !allowPunchThrough) { pal[2] = Lerp(pal[0], pal[1], 1, 3); pal[3] = Lerp(pal[0], pal[1], 2, 3); }
        else { pal[2] = Lerp(pal[0], pal[1], 1, 2); pal[3] = 0; }
        uint bits = BitConverter.ToUInt32(b[4..]);
        for (int i = 0; i < 16; i++) out16[i] = pal[(int)(bits >> (2 * i)) & 3];
    }

    static void DecodeDxt1Block(ReadOnlySpan<byte> b, Span<uint> o) => ColorBlock(b, o, true);

    static void DecodeDxt3Block(ReadOnlySpan<byte> b, Span<uint> o)
    {
        ColorBlock(b[8..], o, false);
        ulong a = BitConverter.ToUInt64(b);
        for (int i = 0; i < 16; i++)
        {
            uint a4 = (uint)(a >> (4 * i)) & 15;
            o[i] = (o[i] & 0x00FFFFFF) | (a4 * 17) << 24;
        }
    }

    static void DecodeDxt5Block(ReadOnlySpan<byte> b, Span<uint> o)
    {
        ColorBlock(b[8..], o, false);
        int a0 = b[0], a1 = b[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = (a0 * (7 - i) + a1 * i) / 7;
        else { for (int i = 1; i < 5; i++) pal[i + 1] = (a0 * (5 - i) + a1 * i) / 5; pal[6] = 0; pal[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            o[i] = (o[i] & 0x00FFFFFF) | (uint)pal[(int)(bits >> (3 * i)) & 7] << 24;
    }
}
