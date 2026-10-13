using SkiaSharp;

namespace DeSignMage.Vectorize.Cli;

/// <summary>Generates the test bitmaps: a flat logo, a gradient, and a photo-like picture (noise, soft shapes).</summary>
internal static class SampleImages
{
    public static void Make(string folder)
    {
        Directory.CreateDirectory(folder);
        Save(Logo(), Path.Combine(folder, "logo.png"));
        Save(Gradient(), Path.Combine(folder, "gradient.png"));
        Save(Photo(), Path.Combine(folder, "photo.png"));
        Console.WriteLine("samples written to " + folder);
    }

    private static void Save(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        bmp.Dispose();
    }

    /// <summary>Flat, anti-aliased shop-sign logo: ring, rounded plaque, star, lettering, a thin rule.</summary>
    private static SKBitmap Logo()
    {
        var bmp = new SKBitmap(480, 320);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        p.Color = new SKColor(0x1F, 0x3A, 0x6E);
        c.DrawRoundRect(new SKRoundRect(new SKRect(20, 20, 460, 300), 28), p);

        p.Color = new SKColor(0xF2, 0xB7, 0x05);
        using (var ring = new SKPath())
        {
            ring.AddCircle(120, 140, 78);
            ring.AddCircle(120, 140, 52);
            ring.FillType = SKPathFillType.EvenOdd;
            c.DrawPath(ring, p);
        }

        p.Color = new SKColor(0xD6, 0x28, 0x28);
        using (var star = new SKPath())
        {
            for (int i = 0; i < 10; i++)
            {
                double ang = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? 40 : 17;
                float x = (float)(120 + r * Math.Cos(ang)), y = (float)(140 + r * Math.Sin(ang));
                if (i == 0) star.MoveTo(x, y); else star.LineTo(x, y);
            }
            star.Close();
            c.DrawPath(star, p);
        }

        p.Color = SKColors.White;
        using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 64);
        c.DrawText("SIGN", 225, 150, font, p);
        using var font2 = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal), 34);
        p.Color = new SKColor(0xF2, 0xB7, 0x05);
        c.DrawText("WIZARD", 227, 200, font2, p);

        p.Color = new SKColor(0xD6, 0x28, 0x28);
        c.DrawRect(228, 222, 190, 4, p);
        p.Color = SKColors.White;
        c.DrawCircle(250, 262, 9, p);
        c.DrawCircle(290, 262, 9, p);
        c.DrawCircle(330, 262, 9, p);
        return bmp;
    }

    /// <summary>A smooth diagonal blend plus a soft disc: stresses color reduction (banding into stacked areas).</summary>
    private static SKBitmap Gradient()
    {
        var bmp = new SKBitmap(400, 300);
        using var c = new SKCanvas(bmp);
        using var p = new SKPaint { IsAntialias = true };
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(400, 300),
            [new SKColor(0xFF, 0x8A, 0x1F), new SKColor(0xC8, 0x2A, 0x6B), new SKColor(0x3A, 0x1C, 0x8C)], [0, 0.5f, 1], SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, 400, 300, p);
        p.Shader = SKShader.CreateRadialGradient(new SKPoint(150, 120), 90,
            [new SKColor(0xFF, 0xFF, 0xFF, 0xE0), new SKColor(0xFF, 0xFF, 0xFF, 0x00)], null, SKShaderTileMode.Clamp);
        c.DrawCircle(150, 120, 90, p);
        return bmp;
    }

    /// <summary>Sky, sun with glow, soft clouds, rolling hills, a lake, and sensor-like noise.</summary>
    private static SKBitmap Photo()
    {
        const int W = 640, H = 480;
        var bmp = new SKBitmap(W, H);
        using (var c = new SKCanvas(bmp))
        {
            using var p = new SKPaint { IsAntialias = true };
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 300),
                [new SKColor(0x2B, 0x5A, 0xA8), new SKColor(0x8D, 0xB8, 0xE0), new SKColor(0xF6, 0xD9, 0xA0)], [0, 0.6f, 1], SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, W, H, p);

            p.Shader = SKShader.CreateRadialGradient(new SKPoint(470, 230), 150,
                [new SKColor(0xFF, 0xF4, 0xCC, 0xFF), new SKColor(0xFF, 0xE0, 0x90, 0x90), new SKColor(0xFF, 0xD0, 0x80, 0x00)], [0, 0.25f, 1], SKShaderTileMode.Clamp);
            c.DrawCircle(470, 230, 150, p);
            p.Shader = null;

            p.Color = new SKColor(255, 255, 255, 170);
            p.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12);
            foreach (var (x, y, rx, ry) in new[] { (140f, 90f, 90f, 24f), (210f, 110f, 70f, 20f), (360f, 60f, 100f, 22f), (90f, 150f, 60f, 16f) })
                c.DrawOval(new SKRect(x - rx, y - ry, x + rx, y + ry), p);
            p.MaskFilter = null;

            DrawHills(c, p, 300, 40, 0.012f, new SKColor(0x6F, 0x8F, 0xA8), 1);
            DrawHills(c, p, 340, 55, 0.009f, new SKColor(0x3F, 0x6B, 0x4A), 2);
            DrawHills(c, p, 395, 45, 0.014f, new SKColor(0x23, 0x4A, 0x2E), 3);

            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 420), new SKPoint(0, H),
                [new SKColor(0x3C, 0x7C, 0xB0), new SKColor(0x1B, 0x3F, 0x66)], null, SKShaderTileMode.Clamp);
            c.DrawRect(0, 420, W, H - 420, p);
        }

        // Sensor noise.
        var rng = new Random(7);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var px = bmp.GetPixel(x, y);
                int n = (int)((rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 18);
                bmp.SetPixel(x, y, new SKColor((byte)Math.Clamp(px.Red + n, 0, 255), (byte)Math.Clamp(px.Green + n, 0, 255), (byte)Math.Clamp(px.Blue + n, 0, 255)));
            }
        }
        return bmp;
    }

    private static void DrawHills(SKCanvas c, SKPaint p, float baseY, float amp, float freq, SKColor color, int seed)
    {
        using var path = new SKPath();
        path.MoveTo(0, 480);
        for (int x = 0; x <= 640; x += 4)
        {
            float y = baseY + amp * (float)(Math.Sin(x * freq + seed * 1.7) * 0.6 + Math.Sin(x * freq * 2.3 + seed) * 0.3);
            path.LineTo(x, y);
        }
        path.LineTo(640, 480);
        path.Close();
        p.Color = color;
        c.DrawPath(path, p);
    }
}
