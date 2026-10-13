using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering.Export;

/// <summary>How the preview in the Export dialog is drawn: at most this big, on a checkerboard (everything not covered is transparent in a PNG, PDF, SVG, ...), plain white, or on the solid <paramref name="Solid"/> color a JPEG gets behind its art.</summary>
public sealed record ExportPreviewOptions(int BoxWidth, int BoxHeight, SKColor CheckerA, SKColor CheckerB, bool White = false, int Cell = 8, SKColor? Solid = null);

/// <summary>
/// The picture the Export dialog shows of what is about to be exported: the very same <see cref="ExportPlan"/> (so the same area, with the effects,
/// and the same objects) drawn by the same renderer as the picture exports, fitted into a box. It only reads the document, so it can run on a
/// worker thread while the dialog stays responsive; <see cref="CancellationToken"/> stops it between two objects when the choices changed again.
/// </summary>
public static class ExportPreview
{
    /// <summary>The size of the preview of <paramref name="area"/> in a box: as big as fits, with the area's proportions, at least 1 pixel.</summary>
    public static (int Width, int Height) FitSize(Rect2 area, int boxWidth, int boxHeight)
    {
        double w = Math.Max(area.Width, 1e-9), h = Math.Max(area.Height, 1e-9);
        double scale = Math.Min(Math.Max(boxWidth, 1) / w, Math.Max(boxHeight, 1) / h);
        return (Math.Clamp((int)Math.Round(w * scale), 1, Math.Max(boxWidth, 1)), Math.Clamp((int)Math.Round(h * scale), 1, Math.Max(boxHeight, 1)));
    }

    public static SKBitmap Render(Document doc, ExportPlan plan, ExportPreviewOptions options, CancellationToken cancel = default)
    {
        var (w, h) = FitSize(plan.Area, options.BoxWidth, options.BoxHeight);
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            DrawBackdrop(canvas, w, h, options);
            canvas.Scale((float)(w / Math.Max(plan.Area.Width, 1e-9)), (float)(h / Math.Max(plan.Area.Height, 1e-9)));
            canvas.Translate((float)-plan.Area.Left, (float)-plan.Area.Top);
            canvas.ClipRect(plan.Area.ToSK());
            using var renderer = new SceneRenderer(doc);
            foreach (var layer in plan.Layers)
                foreach (var o in layer.Objects)
                {
                    cancel.ThrowIfCancellationRequested();
                    renderer.DrawObject(canvas, o, Affine2.Identity, null, cache: false);
                }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void DrawBackdrop(SKCanvas canvas, int w, int h, ExportPreviewOptions options)
    {
        if (options.Solid is { } solid)
        {
            canvas.Clear(solid);
            return;
        }
        if (options.White)
        {
            canvas.Clear(SKColors.White);
            return;
        }
        int cell = Math.Max(2, options.Cell);
        using var tile = new SKBitmap(2 * cell, 2 * cell);
        using (var c = new SKCanvas(tile))
        {
            c.Clear(options.CheckerA);
            using var b = new SKPaint { Color = options.CheckerB };
            c.DrawRect(0, 0, cell, cell, b);
            c.DrawRect(cell, cell, cell, cell, b);
        }
        using var image = SKImage.FromBitmap(tile);
        using var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, w, h, paint);
    }
}
