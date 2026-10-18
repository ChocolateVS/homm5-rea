using System.Buffers.Binary;

namespace H5.Core.Ui.Imaging;

/// <summary>
/// Decodes the DDS variants found in the stock archives (surveyed across all paks):
/// A8R8G8B8 (5773 files), DXT3 (2662), DXT1 (844), R5G6B5 (9), DXT5 (2), DXT4 (1).
/// Only the top mip level is decoded. Used for offline previews and tools; the Godot
/// client can upload DXT data directly instead.
/// </summary>
public static class DdsImage
{
    public static RgbaImage Decode(byte[] dds)
    {
        if (dds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(dds) != 0x20534444)
            throw new InvalidDataException("not a DDS file");
        int height = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(12));
        int width = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(16));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(80));
        uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(84));
        int bits = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(88));
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(92));
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(96));
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(100));
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(104));
        var src = dds.AsSpan(128);
        var img = new RgbaImage(width, height);

        if ((pfFlags & 4) != 0)
        {
            switch (fourCC)
            {
                case 0x31545844: DecodeBlocks(src, img, 8, DecodeDxt1Block); break;               // DXT1
                case 0x33545844: case 0x32545844: DecodeBlocks(src, img, 16, DecodeDxt3Block); break; // DXT3/DXT2
                case 0x35545844: case 0x34545844: DecodeBlocks(src, img, 16, DecodeDxt5Block); break; // DXT5/DXT4
                default: throw new NotSupportedException($"DDS fourCC 0x{fourCC:X8}");
            }
            return img;
        }

        int bpp = bits / 8;
        bool hasAlpha = (pfFlags & 1) != 0 && aMask != 0;
        for (int i = 0; i < width * height; i++)
        {
            uint v = bpp switch
            {
                4 => BinaryPrimitives.ReadUInt32LittleEndian(src[(i * 4)..]),
                3 => (uint)(src[i * 3] | src[i * 3 + 1] << 8 | src[i * 3 + 2] << 16),
                2 => BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]),
                _ => throw new NotSupportedException($"DDS {bits}-bit uncompressed"),
            };
            img.Pixels[i * 4] = Channel(v, rMask);
            img.Pixels[i * 4 + 1] = Channel(v, gMask);
            img.Pixels[i * 4 + 2] = Channel(v, bMask);
            img.Pixels[i * 4 + 3] = hasAlpha ? Channel(v, aMask) : (byte)255;
        }
        return img;
    }

    static byte Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((v & mask) >> shift) * 255 / max);
    }

    delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<uint> outRgba);

    static void DecodeBlocks(ReadOnlySpan<byte> src, RgbaImage img, int blockBytes, BlockDecoder dec)
    {
        int bw = Math.Max(1, (img.Width + 3) / 4), bh = Math.Max(1, (img.Height + 3) / 4);
        Span<uint> px = stackalloc uint[16];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int off = (by * bw + bx) * blockBytes;
            if (off + blockBytes > src.Length) return;
            dec(src.Slice(off, blockBytes), px);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int ix = bx * 4 + x, iy = by * 4 + y;
                if (ix >= img.Width || iy >= img.Height) continue;
                uint c = px[y * 4 + x];
                int di = (iy * img.Width + ix) * 4;
                img.Pixels[di] = (byte)c; img.Pixels[di + 1] = (byte)(c >> 8);
                img.Pixels[di + 2] = (byte)(c >> 16); img.Pixels[di + 3] = (byte)(c >> 24);
            }
        }
    }

    // Colours are packed as 0xAABBGGRR (RGBA byte order in memory).
    static void ColorBlock(ReadOnlySpan<byte> b, Span<uint> outPx, bool allowPunchThrough)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        Span<uint> pal = stackalloc uint[4];
        (int r0, int g0, int b0) = Rgb565(c0);
        (int r1, int g1, int b1) = Rgb565(c1);
        pal[0] = Pack(r0, g0, b0, 255);
        pal[1] = Pack(r1, g1, b1, 255);
        if (c0 > c1 || !allowPunchThrough)
        {
            pal[2] = Pack((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3, 255);
            pal[3] = Pack((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3, 255);
        }
        else
        {
            pal[2] = Pack((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
            pal[3] = 0;
        }
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int i = 0; i < 16; i++) outPx[i] = pal[(int)((idx >> (2 * i)) & 3)];
    }

    static (int, int, int) Rgb565(ushort c) =>
        (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);

    static uint Pack(int r, int g, int b, int a) => (uint)(r | g << 8 | b << 16 | a << 24);

    static void DecodeDxt1Block(ReadOnlySpan<byte> b, Span<uint> px) => ColorBlock(b, px, true);

    static void DecodeDxt3Block(ReadOnlySpan<byte> b, Span<uint> px)
    {
        ColorBlock(b[8..], px, false);
        ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(b);
        for (int i = 0; i < 16; i++)
        {
            uint a = (uint)((alpha >> (4 * i)) & 15) * 17;
            px[i] = (px[i] & 0x00FFFFFF) | a << 24;
        }
    }

    static void DecodeDxt5Block(ReadOnlySpan<byte> b, Span<uint> px)
    {
        ColorBlock(b[8..], px, false);
        int a0 = b[0], a1 = b[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            pal[6] = 0; pal[7] = 255;
        }
        ulong bitsA = 0;
        for (int i = 0; i < 6; i++) bitsA |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            px[i] = (px[i] & 0x00FFFFFF) | (uint)pal[(int)((bitsA >> (3 * i)) & 7)] << 24;
    }
}
