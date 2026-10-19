namespace H5WebSpike;

/// <summary>Copy of game/src/Game.cs PadMipChain (private there): completes the DDS mip chain Godot requires.</summary>
static class DdsMips
{
    public static bool PadMipChain(ref byte[] dds)
    {
        if (BitConverter.ToUInt32(dds, 0) != 0x20534444) return false; // "DDS "
        int height = BitConverter.ToInt32(dds, 12), width = BitConverter.ToInt32(dds, 16);
        int mips = Math.Max(1, BitConverter.ToInt32(dds, 28));
        uint pfFlags = BitConverter.ToUInt32(dds, 80), fourCC = BitConverter.ToUInt32(dds, 84);
        int bpp = BitConverter.ToInt32(dds, 88);
        int blockBytes = fourCC switch
        {
            0x31545844 => 8,                              // DXT1
            0x33545844 or 0x34545844 or 0x35545844 => 16, // DXT3/4/5
            _ => 0,
        };
        if ((pfFlags & 0x4) != 0 && blockBytes == 0) return true; // other FourCC: leave to Godot
        int unit = blockBytes > 0 ? blockBytes : Math.Max(1, bpp / 8);
        if (mips > 1)
        {
            // Godot expects the complete chain down to 1x1 whenever mipmaps are present.
            int full = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
            if (full != mips) { mips = full; BitConverter.TryWriteBytes(dds.AsSpan(28, 4), mips); }
        }

        long expected = 128, lastLevelOffset = 128;
        for (int level = 0, w = width, h = height; level < mips; level++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            long size = blockBytes > 0 ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes : (long)w * h * unit;
            if (expected + size <= dds.Length) lastLevelOffset = expected;
            expected += size;
        }
        if (dds.Length >= expected) return true;
        if (lastLevelOffset + unit > dds.Length) return false;

        var padded = new byte[expected];
        Buffer.BlockCopy(dds, 0, padded, 0, dds.Length);
        for (long p = dds.Length; p < expected; p += unit)
            Buffer.BlockCopy(dds, (int)lastLevelOffset, padded, (int)p, (int)Math.Min(unit, expected - p));
        dds = padded;
        return true;
    }
}
