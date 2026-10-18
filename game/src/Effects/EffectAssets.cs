using Godot;
using H5.Assets.Effects;
using H5.Assets.Textures;
using H5.Core.Xdb;

namespace H5Reborn.Effects;

/// <summary>
/// Shared caches for the effect runtime: decoded effect descriptors and the per-instance
/// particle texture atlases. The original engine packs an instance's (up to) 16 texture
/// slots into one atlas (CParticleTexture) and draws all its quads in one depth-sorted
/// batch; we do the same so ordering between textures matches.
/// </summary>
public static class EffectAssets
{
    static EffectLoader? _loader;
    public static EffectLoader Loader => _loader ??= new EffectLoader(Game.Instance.Xdb);

    public sealed class Atlas
    {
        public required ImageTexture Texture;
        /// <summary>Per slot UVs; null = empty slot.</summary>
        public required SlotUv?[] Slots;
    }

    /// <summary>Atlas UVs of a texture slot: left/right u and the v at the quad's bottom and top edges.</summary>
    public readonly record struct SlotUv(float U0, float U1, float VBottom, float VTop);

    static readonly Dictionary<string, Atlas?> _atlases = new();
    static Shader? _particleShader;

    /// <summary>Premultiplied-alpha, unlit, depth-tested, no depth write — like the original particle pass.</summary>
    public static Shader ParticleShader => _particleShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_premul_alpha, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
            vec3 to_linear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c)); }
            void fragment() {
                vec4 t = texture(atlas, UV);
                // Original modulates texel by vertex colour (both premultiplied/additive-ready).
                ALBEDO = t.rgb * to_linear(COLOR.rgb);
                ALPHA = t.a * COLOR.a;
            }
            """,
    };

    public static Atlas? GetAtlas(string?[] slots)
    {
        var key = string.Join("|", slots.Select(s => s ?? ""));
        if (_atlases.TryGetValue(key, out var cached)) return cached;

        const int Pad = 4;
        var images = new Image?[slots.Length];
        var flipY = new bool[slots.Length];
        int width = 0, height = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not { } href || LoadImage(href, out flipY[i]) is not { } img) continue;
            images[i] = img;
            width += img.GetWidth() + Pad * 2;
            height = Math.Max(height, img.GetHeight() + Pad * 2);
        }
        if (width == 0) { _atlases[key] = null; return null; }

        // Single-row strip: at most 16 small textures, so packing efficiency doesn't matter.
        var atlas = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        atlas.Fill(new Color(0, 0, 0, 0));
        var rects = new SlotUv?[slots.Length];
        int x = 0;
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i] is not { } img) continue;
            atlas.BlitRect(img, new Rect2I(0, 0, img.GetWidth(), img.GetHeight()), new Vector2I(x + Pad, Pad));
            float u0 = (x + Pad) / (float)width, u1 = (x + Pad + img.GetWidth()) / (float)width;
            float v0 = Pad / (float)height, v1 = (Pad + img.GetHeight()) / (float)height;
            // Bottom-left corner maps to the bottom of the image unless the texture is flagged FlipY.
            rects[i] = flipY[i] ? new SlotUv(u0, u1, v0, v1) : new SlotUv(u0, u1, v1, v0);
            x += img.GetWidth() + Pad * 2;
        }
        atlas.GenerateMipmaps();
        var result = new Atlas { Texture = ImageTexture.CreateFromImage(atlas), Slots = rects };
        _atlases[key] = result;
        return result;
    }

    static readonly Dictionary<string, ImageTexture?> _premulTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A texture ready for the premultiplied effect blend: CONVERT_TRANSPARENT(_ADD) exports are
    /// already premultiplied / zero-alpha; CONVERT_ORDINARY exports are straight alpha and are
    /// premultiplied here so one blend mode reproduces both.
    /// </summary>
    public static ImageTexture? LoadPremultipliedTexture(string textureXdb)
    {
        if (_premulTextures.TryGetValue(textureXdb, out var cached)) return cached;
        ImageTexture? tex = null;
        if (LoadImage(textureXdb, out _) is { } img)
        {
            img.GenerateMipmaps();
            tex = ImageTexture.CreateFromImage(img);
        }
        _premulTextures[textureXdb] = tex;
        return tex;
    }

    /// <summary>
    /// Decodes a (Texture) xdb's DDS into an RGBA image in premultiplied form (straight-alpha
    /// CONVERT_ORDINARY exports are premultiplied on load).
    /// </summary>
    public static Image? LoadImage(string textureXdb, out bool flipY)
    {
        flipY = false;
        bool straight = false;
        var fs = Game.Instance.Fs;
        var path = XdbRef.Parse(textureXdb).Path;
        string? dds = path;
        // A .dds path (as ModelLoader resolves materials) carries no flags; its (Texture) xdb
        // normally sits beside it with the same name.
        var xdbPath = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? path[..^4] + ".xdb" : path;
        var root = Loader.TryLoadRoot(xdbPath);
        if (root?.Name.LocalName == "Texture")
        {
            flipY = string.Equals(root.Element("FlipY")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            straight = root.Element("ConversionType")?.Value == "CONVERT_ORDINARY";
            if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var dest = (string?)root.Element("DestName")?.Attribute("href");
                dds = string.IsNullOrEmpty(dest) ? null : XdbRef.Parse(dest, path).Path;
            }
        }
        else if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return null;
        if (dds == null || !fs.TryReadAllBytes(dds, out var bytes)) return null;
        try
        {
            var rgba = DdsDecoder.Decode(bytes);
            if (straight)
            {
                var px = rgba.Pixels;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    int a = px[i + 3];
                    px[i] = (byte)((px[i] * a + 127) / 255);
                    px[i + 1] = (byte)((px[i + 1] * a + 127) / 255);
                    px[i + 2] = (byte)((px[i + 2] * a + 127) / 255);
                }
            }
            return Image.CreateFromData(rgba.Width, rgba.Height, false, Image.Format.Rgba8, rgba.Pixels);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] effect texture {dds}: {e.Message}");
            return null;
        }
    }
}
