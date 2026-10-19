using System.Text.Json;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;

namespace DeSignMage.Formats.Native;

/// <summary>The picture recipe (Picture Editor) as JSON inside a picture object. Only values that differ from the neutral ones are written; unknown fields are ignored.</summary>
internal static class PictureRecipeJson
{
    public static void Write(Utf8JsonWriter w, string name, PictureRecipe r)
    {
        w.WriteStartObject(name);
        w.WriteNumber("sourceWidth", r.SourceWidth);
        w.WriteNumber("sourceHeight", r.SourceHeight);
        if (r.Mirror) w.WriteBoolean("mirror", true);
        if (r.Quarter != 0) w.WriteNumber("quarter", r.Quarter);
        if (r.Straighten != 0) w.WriteNum("straighten", r.Straighten);
        if (r.Crop is { } c) w.WriteNumbers("crop", [c.X, c.Y, c.Width, c.Height]);
        var tr = r.Transparency;
        if (!tr.IsIdentity)
        {
            w.WriteStartObject("transparency");
            if (tr.HasColors)
            {
                w.WriteNumbers("colors", tr.Colors.Select(c => (double)c).ToArray());
                w.WriteNumber("tolerance", tr.Tolerance);
                if (tr.EdgeOnly) w.WriteBoolean("edgeOnly", true);
            }
            if (tr.HasCuts)
            {
                w.WriteStartArray("cuts");
                foreach (var cut in tr.Cuts.Where(c => c.IsUsable))
                {
                    w.WriteStartObject();
                    if (!cut.Keep) w.WriteBoolean("remove", true);
                    w.WriteNumbers("points", cut.Points.SelectMany(p => new[] { p.X, p.Y }).ToArray());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (tr.Shrink != 0) w.WriteNumber("shrink", tr.Shrink);
            if (tr.Softness != 0) w.WriteNumber("softness", tr.Softness);
            if (tr.Opacity != 100) w.WriteNumber("opacity", tr.Opacity);
            w.WriteEndObject();
        }
        var a = r.Adjust;
        if (!a.IsIdentity)
        {
            w.WriteStartObject("adjust");
            if (a.Brightness != 0) w.WriteNum("brightness", a.Brightness);
            if (a.Contrast != 0) w.WriteNum("contrast", a.Contrast);
            if (a.Gamma != 1) w.WriteNum("gamma", a.Gamma);
            if (a.Saturation != 0) w.WriteNum("saturation", a.Saturation);
            if (a.Hue != 0) w.WriteNum("hue", a.Hue);
            if (!a.BalanceIsDefault) w.WriteNumbers("balance", [a.BalanceR, a.BalanceG, a.BalanceB]);
            if (!a.LevelsAreDefault)
            {
                w.WriteNumbers("levelsLow", [a.LowR, a.LowG, a.LowB]);
                w.WriteNumbers("levelsHigh", [a.HighR, a.HighG, a.HighB]);
            }
            w.WriteEndObject();
        }
        var fx = r.Effects;
        if (!fx.IsIdentity)
        {
            w.WriteStartObject("effects");
            if (fx.NoiseReduction != 0) w.WriteNumber("noiseReduction", fx.NoiseReduction);
            if (fx.Blur > 0) w.WriteNum("blur", fx.Blur);
            if (fx.Sharpen != 0) w.WriteNumber("sharpen", fx.Sharpen);
            if (fx.Grayscale) w.WriteBoolean("grayscale", true);
            if (fx.Sepia != 0) w.WriteNumber("sepia", fx.Sepia);
            if (fx.Invert) w.WriteBoolean("invert", true);
            if (fx.Threshold != 0) w.WriteNumber("threshold", fx.Threshold);
            if (fx.Posterize > 1) w.WriteNumber("posterize", fx.Posterize);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    /// <summary>The recipe, or null when it cannot be understood (the picture then simply shows its finished pixels).</summary>
    public static PictureRecipe? Read(JsonElement e)
    {
        int sw = e.Int("sourceWidth"), sh = e.Int("sourceHeight");
        if (sw < 1 || sh < 1) return null;
        var recipe = new PictureRecipe(sw, sh)
        {
            Mirror = e.Bool("mirror"),
            Quarter = ((e.Int("quarter") % 4) + 4) % 4,
            Straighten = Math.Clamp(e.Num("straighten"), -PictureGeometry.MaxAngle, PictureGeometry.MaxAngle),
        };
        if (e.TryGet("crop", out _) && e.Doubles("crop") is { Length: 4 } c)
            recipe = recipe with { Crop = new RectI((int)c[0], (int)c[1], (int)c[2], (int)c[3]).ClampedTo(recipe.CanvasSize.Width, recipe.CanvasSize.Height) };
        if (e.TryGet("transparency", out var tr))
        {
            var cuts = new List<CutShape>();
            foreach (var cut in tr.Items("cuts"))
            {
                var xy = cut.Doubles("points");
                if (xy.Length < 6) continue;
                var points = new List<Vec2>();
                for (int i = 0; i + 1 < xy.Length; i += 2) points.Add(new Vec2(xy[i], xy[i + 1]));
                cuts.Add(new CutShape(new ValueList<Vec2>(points), Keep: !cut.Bool("remove")));
            }
            recipe = recipe with
            {
                Transparency = new PictureTransparency
                {
                    Colors = new ValueList<int>(tr.Doubles("colors").Select(c => (int)Math.Clamp(c, 0, 0xFFFFFF))),
                    Tolerance = tr.Int("tolerance", PictureTransparency.DefaultTolerance),
                    EdgeOnly = tr.Bool("edgeOnly"),
                    Cuts = new ValueList<CutShape>(cuts),
                    Shrink = tr.Int("shrink"),
                    Softness = tr.Int("softness"),
                    Opacity = tr.Int("opacity", 100),
                }.Clamped(),
            };
        }
        if (e.TryGet("adjust", out var a))
        {
            var adjust = new PictureAdjust
            {
                Brightness = Math.Clamp(a.Num("brightness"), -100, 100),
                Contrast = Math.Clamp(a.Num("contrast"), -100, 100),
                Gamma = Math.Clamp(a.Num("gamma", 1), 0.1, 10),
                Saturation = Math.Clamp(a.Num("saturation"), -100, 100),
                Hue = Math.Clamp(a.Num("hue"), -180, 180),
            };
            if (a.Doubles("balance") is { Length: 3 } b)
                adjust = adjust with { BalanceR = Math.Clamp(b[0], -100, 100), BalanceG = Math.Clamp(b[1], -100, 100), BalanceB = Math.Clamp(b[2], -100, 100) };
            if (a.Doubles("levelsLow") is { Length: 3 } lo && a.Doubles("levelsHigh") is { Length: 3 } hi)
                adjust = adjust with { LowR = B(lo[0]), LowG = B(lo[1]), LowB = B(lo[2]), HighR = B(hi[0]), HighG = B(hi[1]), HighB = B(hi[2]) };
            recipe = recipe with { Adjust = adjust };
        }
        if (e.TryGet("effects", out var x))
        {
            recipe = recipe with
            {
                Effects = new PictureEffects
                {
                    NoiseReduction = x.Int("noiseReduction"),
                    Blur = x.Num("blur"),
                    Sharpen = x.Int("sharpen"),
                    Grayscale = x.Bool("grayscale"),
                    Sepia = x.Int("sepia"),
                    Invert = x.Bool("invert"),
                    Threshold = x.Int("threshold"),
                    Posterize = x.Int("posterize", 1),
                }.Clamped(),
            };
        }
        return recipe;
    }

    private static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
