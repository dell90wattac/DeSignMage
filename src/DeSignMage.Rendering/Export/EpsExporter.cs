using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;
using static DeSignMage.Rendering.Export.PostScriptEncoding;

namespace DeSignMage.Rendering.Export;

/// <summary>
/// Writes the design as an Encapsulated PostScript file (Level 3, ASCII only, no preview, no <c>showpage</c>).
/// <list type="bullet">
/// <item><b>Vectors stay vectors.</b> Every object is written in page coordinates (y up, origin at the lower left of the export area), so stroke
/// widths stay true. Circular arcs become Béziers; the even-odd area of a combined object is a clip for its parts' fills, and outlines follow
/// (the same look as on screen). Live text is written as its glyph outlines.</item>
/// <item><b>Colors:</b> RGB colors with <c>setrgbcolor</c>; a CMYK color keeps its four components (<c>setcmykcolor</c>), so a print shop gets the
/// values that were chosen, not a conversion.</item>
/// <item><b>Gradients</b> are PostScript shadings (<c>shfill</c>, axial or radial) with a 256-sample color function taken from the same ramp the screen
/// uses, positioned by the object's own matrix, so they follow any rotation or skew exactly.</item>
/// <item><b>What EPS cannot do</b> (no alpha, no blur): an object with a soft shadow/glow/feather, a color or gradient with transparency, or a stroke that is not a plain
/// color is drawn into a picture of its bounds (at <see cref="ExportOptions.EpsRasterDpi"/>, lowered for very large pieces) together with everything underneath it, composited
/// on white, and written as a picture with a color-key mask: pixels where nothing at all is painted stay transparent, all others are opaque. Objects above it are vectors again.
/// Pictures are embedded at their own resolution (at most the raster dpi), Flate and ASCII85 encoded; pixels with alpha below half are keyed out.</item>
/// </list>
/// </summary>
public static class EpsExporter
{
    /// <summary>A piece of raster stays below this many pixels (about 48 MB of picture data before compression).</summary>
    private const long MaxPiecePixels = 16_000_000;
    private const int MaxPieceEdge = 8000;

    /// <summary>Pixels whose coverage is below this (of 255) are left transparent in a raster piece.</summary>
    private const int MaskThreshold = 8;

    public static byte[] RenderBytes(Document doc, ExportOptions options) => Encoding.ASCII.GetBytes(Render(doc, options));

    public static string Render(Document doc, ExportOptions options)
    {
        var plan = ExportPlan.Create(doc, options);
        return new Writer(doc, plan, options).Write();
    }

    /// <summary>True when the object (or a part of it) cannot be written as plain PostScript vectors.</summary>
    internal static bool NeedsRaster(DocObject o)
    {
        if (o is CombinedObject c) return c.Parts.Any(NeedsRaster) || StyleNeedsRaster(c.Style);
        return StyleNeedsRaster(o.Style);
    }

    private static bool StyleNeedsRaster(ObjectStyle s)
    {
        if (s.Blur is not null || s.Feather is { Amount: > 0 }) return true;
        switch (s.Fill)
        {
            case SolidPaint { Color.A: < 255 }: return true;
            case GradientPaint g when GradientRamp.Build(g).Any(e => e.A < 255): return true;
        }
        return s.Stroke is { Width: > 0 } st && st.Paint is not SolidPaint { Color.A: 255 };
    }

    private sealed class Writer(Document doc, ExportPlan plan, ExportOptions options)
    {
        private readonly StringBuilder _ps = new();
        private List<DocObject> _all = [];
        private int _index;

        public string Write()
        {
            var a = plan.Area;
            double w = a.Width, h = a.Height;
            _all = plan.Layers.SelectMany(l => l.Objects).ToList();

            _ps.Append("%!PS-Adobe-3.0 EPSF-3.0\n");
            _ps.Append("%%Creator: DeSign Mage\n");
            _ps.Append("%%Title: ").Append(Comment(options.Title)).Append('\n');
            _ps.Append("%%LanguageLevel: 3\n");
            _ps.Append("%%BoundingBox: 0 0 ").Append(Math.Max(1, (int)Math.Ceiling(w - 1e-6))).Append(' ').Append(Math.Max(1, (int)Math.Ceiling(h - 1e-6))).Append('\n');
            _ps.Append("%%HiResBoundingBox: 0 0 ").Append(Num(w)).Append(' ').Append(Num(h)).Append('\n');
            _ps.Append("%%DocumentData: Clean7Bit\n");
            _ps.Append("%%EndComments\n");
            _ps.Append("save\n");
            // Page coordinates (points, y down, origin top left of the export area) on PostScript's y-up page.
            _ps.Append("0 ").Append(Num(h)).Append(" translate 1 -1 scale ").Append(Num(-a.Left)).Append(' ').Append(Num(-a.Top)).Append(" translate\n");
            _ps.Append("newpath ").Append(Num(a.Left)).Append(' ').Append(Num(a.Top)).Append(" moveto ")
               .Append(Num(a.Right)).Append(' ').Append(Num(a.Top)).Append(" lineto ")
               .Append(Num(a.Right)).Append(' ').Append(Num(a.Bottom)).Append(" lineto ")
               .Append(Num(a.Left)).Append(' ').Append(Num(a.Bottom)).Append(" lineto closepath clip newpath\n");

            for (_index = 0; _index < _all.Count; _index++) EmitObject(_all[_index]);

            _ps.Append("restore\n");
            _ps.Append("%%Trailer\n%%EOF\n");
            return _ps.ToString();
        }

        // ---------------------------------------------------------------- objects

        private void EmitObject(DocObject o)
        {
            if (o is ImageObject img && o.Style.Feather is not { Amount: > 0 })
            {
                EmitImage(img, img.GetWorldTransform());
                return;
            }
            if (NeedsRaster(o))
            {
                EmitRasterPiece(o);
                return;
            }
            if (o is CombinedObject combined)
            {
                EmitCombined(combined);
                return;
            }
            EmitShape(o, o.GetWorldTransform(), fill: true, stroke: true);
        }

        private void EmitCombined(CombinedObject combined)
        {
            var world = combined.GetWorldTransform();
            // Fills are clipped to the even-odd area of all parts (overlaps are knocked out); outlines follow, unclipped.
            _ps.Append("gsave\n");
            AppendPath(combined.GetLocalGeometry().Transform(world));
            _ps.Append("eoclip newpath\n");
            foreach (var part in combined.Parts) EmitPart(part, world, fill: true, stroke: false);
            _ps.Append("grestore\n");
            foreach (var part in combined.Parts) EmitPart(part, world, fill: false, stroke: true);
        }

        private void EmitPart(DocObject part, Affine2 parentWorld, bool fill, bool stroke)
        {
            if (!part.Visible) return;
            var world = part.Transform * parentWorld;
            if (part is ImageObject img)
            {
                if (fill) EmitImage(img, world);
                return;
            }
            EmitShape(part, world, fill, stroke);
        }

        private void EmitShape(DocObject o, Affine2 world, bool fill, bool stroke)
        {
            var style = o.Style;
            bool drawFill = fill && style.Fill is not null;
            var strokeStyle = stroke && style.Stroke is { Width: > 0 } st && st.Paint is SolidPaint ? style.Stroke : null;
            if (!drawFill && strokeStyle is null) return;
            if (o.GetLocalGeometry() is not { IsEmpty: false } local) return;
            var geometry = local.Transform(world);
            string fillOp = geometry.FillRule == FillRule.EvenOdd ? "eofill" : "fill";
            string clipOp = geometry.FillRule == FillRule.EvenOdd ? "eoclip" : "clip";

            AppendPath(geometry);
            bool pathLeft = true;   // true while the current path is still there for the stroke

            if (drawFill)
            {
                switch (style.Fill)
                {
                    case SolidPaint solid:
                        if (strokeStyle is not null) _ps.Append("gsave ").Append(ColorOp(solid.Color)).Append(' ').Append(fillOp).Append(" grestore\n");
                        else
                        {
                            _ps.Append(ColorOp(solid.Color)).Append(' ').Append(fillOp).Append('\n');
                            pathLeft = false;
                        }
                        break;
                    case GradientPaint gradient:
                        EmitGradient(gradient, local, world, clipOp);
                        break;
                }
            }
            if (strokeStyle is not null && pathLeft)
            {
                var solid = (SolidPaint)strokeStyle.Paint;
                _ps.Append(ColorOp(solid.Color)).Append(' ').Append(Num(strokeStyle.Width)).Append(" setlinewidth ")
                   .Append((int)strokeStyle.Join).Append(" setlinejoin ").Append((int)strokeStyle.Cap).Append(" setlinecap ")
                   .Append(Num(Math.Max(1, strokeStyle.MiterLimit))).Append(" setmiterlimit stroke\n");
            }
            else if (pathLeft) _ps.Append("newpath\n");
        }

        /// <summary>The current path is kept (it is restored by the final <c>grestore</c>); the gradient fills the area inside it.</summary>
        private void EmitGradient(GradientPaint g, PathGeometry local, Affine2 world, string clipOp)
        {
            var bounds = local.GetBounds();
            var frame = g.Frame(bounds);
            if (bounds.IsEmpty || frame.IsDegenerate)
            {
                // no extent: the last color, as the screen does
                _ps.Append("gsave ").Append(ColorOp(g.Stops[^1].Color)).Append(clipOp == "eoclip" ? " eofill" : " fill").Append(" grestore\n");
                return;
            }
            _ps.Append("gsave ").Append(clipOp).Append(" newpath\n");
            // The gradient is defined in the object's own space; its matrix carries it to the page.
            if (g.IsLinear)
            {
                AppendConcat(world);
                _ps.Append("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [").Append(Num(frame.P0.X)).Append(' ').Append(Num(frame.P0.Y)).Append(' ')
                   .Append(Num(frame.P1.X)).Append(' ').Append(Num(frame.P1.Y)).Append("] /Extend [true true]\n");
            }
            else
            {
                AppendConcat(frame.UnitToLocal * world);
                _ps.Append("<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [0 0 0 0 0 1] /Extend [true true]\n");
            }
            const int samples = 256;
            var data = new byte[samples * 3];
            for (int i = 0; i < samples; i++)
            {
                var c = GradientRamp.ColorAt(g, i / (double)(samples - 1));
                data[3 * i] = c.R; data[3 * i + 1] = c.G; data[3 * i + 2] = c.B;
            }
            _ps.Append("/Function << /FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [").Append(samples).Append("] /BitsPerSample 8 /DataSource <\n");
            AppendHex(_ps, data);
            _ps.Append("> >> >> shfill grestore\n");
        }

        private static string ColorOp(DocColor c)
        {
            if (c.Model == ColorModel.Cmyk && c.Cmyk is { } k)
                return $"{Num(k.C / 100)} {Num(k.M / 100)} {Num(k.Y / 100)} {Num(k.K / 100)} setcmykcolor";
            return $"{Num(c.R / 255.0)} {Num(c.G / 255.0)} {Num(c.B / 255.0)} setrgbcolor";
        }

        private void AppendConcat(Affine2 m) =>
            _ps.Append('[').Append(Num(m.M11)).Append(' ').Append(Num(m.M12)).Append(' ').Append(Num(m.M21)).Append(' ')
               .Append(Num(m.M22)).Append(' ').Append(Num(m.OffsetX)).Append(' ').Append(Num(m.OffsetY)).Append("] concat\n");

        /// <summary>Starts a new path and writes <paramref name="g"/> (already in page coordinates) as PostScript path operators.</summary>
        private void AppendPath(PathGeometry g)
        {
            g = g.WithArcsAsCubics();
            _ps.Append("newpath\n");
            var pts = g.Points;
            int pi = 0;
            foreach (var v in g.Verbs)
            {
                switch (v)
                {
                    case PathVerb.MoveTo: _ps.Append(Num(pts[pi].X)).Append(' ').Append(Num(pts[pi].Y)).Append(" moveto\n"); pi++; break;
                    case PathVerb.LineTo: _ps.Append(Num(pts[pi].X)).Append(' ').Append(Num(pts[pi].Y)).Append(" lineto\n"); pi++; break;
                    case PathVerb.CubicTo:
                        for (int k = 0; k < 3; k++) _ps.Append(Num(pts[pi + k].X)).Append(' ').Append(Num(pts[pi + k].Y)).Append(' ');
                        _ps.Append("curveto\n");
                        pi += 3;
                        break;
                    case PathVerb.Close: _ps.Append("closepath\n"); break;
                }
            }
        }

        // ---------------------------------------------------------------- pictures

        private void EmitImage(ImageObject img, Affine2 world)
        {
            if (doc.Assets.Get(img.AssetId) is not { } asset) return;
            using var decoded = SKBitmap.Decode(asset.Data);
            if (decoded is null || decoded.Width < 1 || decoded.Height < 1) return;

            // Resolution: the picture's own, but no more than the raster dpi across the size it has on the page.
            double inchesAcross = world.TransformVector(new Vec2(img.Width, 0)).Length / Units.PointsPerInch;
            double inchesDown = world.TransformVector(new Vec2(0, img.Height)).Length / Units.PointsPerInch;
            int w = decoded.Width, h = decoded.Height;
            double dpi = Math.Max(options.EpsRasterDpi, 1);
            if (inchesAcross > 0 && w > inchesAcross * dpi) w = Math.Max(1, (int)Math.Ceiling(inchesAcross * dpi));
            if (inchesDown > 0 && h > inchesDown * dpi) h = Math.Max(1, (int)Math.Ceiling(inchesDown * dpi));
            while ((long)w * h > MaxPiecePixels)
            {
                w = Math.Max(1, w * 3 / 4);
                h = Math.Max(1, h * 3 / 4);
            }

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return;
            surface.Canvas.Clear(SKColors.Transparent);
            using (var image = SKImage.FromBitmap(decoded))
                surface.Canvas.DrawImage(image, SKRect.Create(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var pixmap = surface.PeekPixels();
            // keep the picture's own colors (not blended with white): the transparent part is keyed out instead
            var rgb = ToRgb(pixmap, w, h, flattenOnWhite: false, threshold: 128, out var key);
            AppendPicture(rgb, w, h, key, world, img.Width, img.Height, 0, 0);
        }

        /// <summary>Draws the object with everything below it into a picture of its bounds, over white, and writes that.</summary>
        private void EmitRasterPiece(DocObject o)
        {
            var region = ExportPlan.VisualBounds(o).Inflate(1);
            region = new Rect2(Math.Max(region.Left, plan.Area.Left), Math.Max(region.Top, plan.Area.Top), Math.Min(region.Right, plan.Area.Right), Math.Min(region.Bottom, plan.Area.Bottom));
            if (region.IsEmpty || region.Width <= 0 || region.Height <= 0) return;

            double k = Math.Max(options.EpsRasterDpi, 1) / Units.PointsPerInch;
            k = Math.Min(k, MaxPieceEdge / Math.Max(region.Width, region.Height));
            k = Math.Min(k, Math.Sqrt(MaxPiecePixels / (region.Width * region.Height)));
            int w = Math.Max(1, (int)Math.Ceiling(region.Width * k)), h = Math.Max(1, (int)Math.Ceiling(region.Height * k));
            // the picture covers whole pixels: it is placed with exactly that size, so nothing is stretched
            var placed = new Rect2(region.Left, region.Top, region.Left + w / k, region.Top + h / k);

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)k);
            canvas.Translate((float)-placed.Left, (float)-placed.Top);
            using (var renderer = new SceneRenderer(doc))
            {
                for (int i = 0; i <= _index; i++)
                {
                    var other = _all[i];
                    if (i != _index && !ExportPlan.VisualBounds(other).Intersects(placed)) continue;
                    renderer.DrawObject(canvas, other, Affine2.Identity, null, cache: false);
                }
            }
            using var pixmap = surface.PeekPixels();
            var rgb = ToRgb(pixmap, w, h, flattenOnWhite: true, threshold: MaskThreshold, out var key);
            AppendPicture(rgb, w, h, key, Affine2.Identity, placed.Width, placed.Height, placed.Left, placed.Top);
        }

        /// <summary>RGB bytes of an RGBA premultiplied picture. Pixels with alpha below <paramref name="threshold"/> take a color nothing else in the picture has (<paramref name="key"/>).</summary>
        private static byte[] ToRgb(SKPixmap pixmap, int w, int h, bool flattenOnWhite, int threshold, out (byte R, byte G, byte B)? key)
        {
            var src = pixmap.GetPixelSpan();
            var rgb = new byte[w * h * 3];
            var transparent = new bool[w * h];
            bool any = false;
            var used = new System.Collections.BitArray(1 << 24);
            for (int p = 0; p < w * h; p++)
            {
                byte r = src[4 * p], g = src[4 * p + 1], b = src[4 * p + 2], a = src[4 * p + 3];
                if (a < threshold)
                {
                    transparent[p] = true;
                    any = true;
                    continue;
                }
                if (flattenOnWhite)
                {
                    r = (byte)Math.Min(255, r + 255 - a);
                    g = (byte)Math.Min(255, g + 255 - a);
                    b = (byte)Math.Min(255, b + 255 - a);
                }
                else if (a < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }
                rgb[3 * p] = r; rgb[3 * p + 1] = g; rgb[3 * p + 2] = b;
                used[r << 16 | g << 8 | b] = true;
            }
            key = null;
            if (!any) return rgb;
            // a color no kept pixel has (there are at most 16 million pixels in a piece, and 16.7 million colors)
            int free = 0;
            while (free < used.Length && used[free]) free++;
            if (free >= used.Length) free = 0;
            var k = ((byte)(free >> 16), (byte)(free >> 8), (byte)free);
            key = k;
            for (int p = 0; p < w * h; p++)
                if (transparent[p]) { rgb[3 * p] = k.Item1; rgb[3 * p + 1] = k.Item2; rgb[3 * p + 2] = k.Item3; }
            return rgb;
        }

        /// <summary>Writes a picture so that it covers (x, y)-(x + width, y + height) of its own frame, carried by <paramref name="frameToPage"/>.</summary>
        private void AppendPicture(byte[] rgb, int w, int h, (byte R, byte G, byte B)? key, Affine2 frameToPage, double width, double height, double x, double y)
        {
            _ps.Append("gsave\n");
            if (!frameToPage.IsIdentity) AppendConcat(frameToPage);
            _ps.Append('[').Append(Num(width)).Append(" 0 0 ").Append(Num(height)).Append(' ').Append(Num(x)).Append(' ').Append(Num(y)).Append("] concat\n");
            _ps.Append("/DeviceRGB setcolorspace\n");
            _ps.Append("<< /ImageType ").Append(key is null ? 1 : 4).Append(" /Width ").Append(w).Append(" /Height ").Append(h)
               .Append(" /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [").Append(w).Append(" 0 0 ").Append(h).Append(" 0 0]");
            if (key is { } c) _ps.Append(" /MaskColor [").Append(c.R).Append(' ').Append(c.G).Append(' ').Append(c.B).Append(']');
            _ps.Append(" /DataSource currentfile /ASCII85Decode filter /FlateDecode filter >> image\n");
            AppendFlateAscii85(_ps, rgb);
            _ps.Append("grestore\n");
        }
    }
}
