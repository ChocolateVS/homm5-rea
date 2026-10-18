namespace H5.Core.Ui.Imaging;

/// <summary>Simple 8-bit RGBA image used for decoding and offline compositing/previews.</summary>
public sealed class RgbaImage
{
    public readonly int Width, Height;
    public readonly byte[] Pixels;

    public RgbaImage(int width, int height)
    {
        Width = width; Height = height;
        Pixels = new byte[width * height * 4];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("pixel buffer size mismatch");
        Width = width; Height = height; Pixels = pixels;
    }

    public void Fill(uint argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        for (int i = 0; i < Pixels.Length; i += 4) { Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = a; }
    }

    /// <summary>
    /// Alpha-blends a source rectangle onto this image at (dx,dy), scaled to dw×dh with
    /// nearest sampling and multiplied by an ARGB tint.
    /// </summary>
    public void Draw(RgbaImage src, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, uint tint = 0xFFFFFFFF)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return;
        float ta = (tint >> 24) / 255f, tr = ((tint >> 16) & 255) / 255f, tg = ((tint >> 8) & 255) / 255f, tb = (tint & 255) / 255f;
        for (int y = Math.Max(0, dy); y < Math.Min(Height, dy + dh); y++)
        {
            int syy = sy + (int)((long)(y - dy) * sh / dh);
            if (syy < 0 || syy >= src.Height) continue;
            for (int x = Math.Max(0, dx); x < Math.Min(Width, dx + dw); x++)
            {
                int sxx = sx + (int)((long)(x - dx) * sw / dw);
                if (sxx < 0 || sxx >= src.Width) continue;
                int si = (syy * src.Width + sxx) * 4, di = (y * Width + x) * 4;
                float a = src.Pixels[si + 3] / 255f * ta;
                if (a <= 0) continue;
                Pixels[di] = (byte)(src.Pixels[si] * tr * a + Pixels[di] * (1 - a));
                Pixels[di + 1] = (byte)(src.Pixels[si + 1] * tg * a + Pixels[di + 1] * (1 - a));
                Pixels[di + 2] = (byte)(src.Pixels[si + 2] * tb * a + Pixels[di + 2] * (1 - a));
                Pixels[di + 3] = (byte)Math.Min(255, Pixels[di + 3] + a * 255 * (1 - Pixels[di + 3] / 255f));
            }
        }
    }

    public void Draw(RgbaImage src, int dx, int dy) => Draw(src, 0, 0, src.Width, src.Height, dx, dy, src.Width, src.Height);

    public void StrokeRect(int x, int y, int w, int h, uint argb)
    {
        void Px(int px, int py)
        {
            if (px < 0 || py < 0 || px >= Width || py >= Height) return;
            int i = (py * Width + px) * 4;
            Pixels[i] = (byte)(argb >> 16); Pixels[i + 1] = (byte)(argb >> 8); Pixels[i + 2] = (byte)argb; Pixels[i + 3] = 255;
        }
        for (int i = 0; i < w; i++) { Px(x + i, y); Px(x + i, y + h - 1); }
        for (int i = 0; i < h; i++) { Px(x, y + i); Px(x + w - 1, y + i); }
    }
}
