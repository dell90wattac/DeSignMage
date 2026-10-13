using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace DeSignMage.App.Canvas;

/// <summary>
/// Our own cursor art, drawn in code (R10 item 7): the "something to select here" cursor is the normal arrow with a small crosshair
/// at its tip. The hotspot is the crosshair's center. The bitmap is drawn at the Windows display scale (32 px at 100%, 48 at 150%,
/// 64 at 200%) so the lines stay one device pixel crisp instead of being stretched by the system.
/// </summary>
internal static class CursorArt
{
    /// <summary>Cursor size in logical units; the bitmap is this times the display scale.</summary>
    public const int LogicalSize = 32;

    // Where the arrow tip / crosshair center sits in the logical canvas (a pixel center, so the cross is one pixel wide at 100%).
    private const float Tip = 9.5f;
    private const float Arm = 6f;

    /// <summary>The select-hit cursor as pixels, and its hotspot (the crosshair center), for a display scale of <paramref name="scale"/>.</summary>
    public static (SKBitmap Bitmap, int HotX, int HotY) RenderSelectHit(double scale)
    {
        float s = (float)Math.Clamp(Math.Round(scale * 4) / 4, 1, 4);   // quarter steps: 100, 125, 150, ... %
        int size = (int)Math.Round(LogicalSize * s);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(s);

        float t = Tip;
        using var arrow = new SKPath();
        arrow.MoveTo(t, t);
        arrow.LineTo(t, t + 16);
        arrow.LineTo(t + 3.6f, t + 12.6f);
        arrow.LineTo(t + 6.4f, t + 19);
        arrow.LineTo(t + 9f, t + 17.8f);
        arrow.LineTo(t + 6.2f, t + 11.6f);
        arrow.LineTo(t + 11f, t + 11.6f);
        arrow.Close();

        using var white = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
        using var black = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1.2f / 1f, StrokeJoin = SKStrokeJoin.Miter };
        canvas.DrawPath(arrow, white);
        canvas.DrawPath(arrow, black);

        // Crosshair: a white halo under a black line, so it reads on dark and light artwork alike.
        using var halo = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 3f, StrokeCap = SKStrokeCap.Butt };
        using var line = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1f, StrokeCap = SKStrokeCap.Butt };
        foreach (var paint in new[] { halo, line })
        {
            canvas.DrawLine(t - Arm, t, t + Arm, t, paint);
            canvas.DrawLine(t, t - Arm, t, t + Arm, paint);
        }
        canvas.Flush();

        int hot = (int)Math.Floor(t * s);
        return (bitmap, hot, hot);
    }

    /// <summary>
    /// The Vector Draw cursor as pixels: the normal arrow with a small square vector node at its tip. The hotspot is the arrow tip (the
    /// node's center), where the next node will be placed.
    /// </summary>
    public static (SKBitmap Bitmap, int HotX, int HotY) RenderVectorDraw(double scale)
    {
        float s = (float)Math.Clamp(Math.Round(scale * 4) / 4, 1, 4);
        int size = (int)Math.Round(LogicalSize * s);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(s);

        float t = Tip;
        using var arrow = new SKPath();
        arrow.MoveTo(t, t);
        arrow.LineTo(t, t + 16);
        arrow.LineTo(t + 3.6f, t + 12.6f);
        arrow.LineTo(t + 6.4f, t + 19);
        arrow.LineTo(t + 9f, t + 17.8f);
        arrow.LineTo(t + 6.2f, t + 11.6f);
        arrow.LineTo(t + 11f, t + 11.6f);
        arrow.Close();
        using var white = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
        using var black = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1.2f, StrokeJoin = SKStrokeJoin.Miter };
        canvas.DrawPath(arrow, white);
        canvas.DrawPath(arrow, black);

        // The node: a 5 x 5 pixel square centered on the tip (at 100%), a black outline one pixel wide with a white inside, so it reads on any
        // artwork. Its edges run through pixel centers, so the lines are crisp.
        var node = new SKRect(t - 2f, t - 2f, t + 2f, t + 2f);
        using var inside = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = SKColors.White };
        using var outline = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1f };
        canvas.DrawRect(node, inside);
        canvas.DrawRect(node, outline);
        canvas.Flush();

        int hot = (int)Math.Floor(t * s);
        return (bitmap, hot, hot);
    }

    /// <summary>The Vector Draw cursor for an Avalonia control, drawn for the display scale.</summary>
    public static Cursor CreateVectorDraw(double scale) => ToCursor(RenderVectorDraw(scale));

    /// <summary>The select-hit cursor for an Avalonia control, drawn for the display scale.</summary>
    public static Cursor CreateSelectHit(double scale) => ToCursor(RenderSelectHit(scale));

    private static Cursor ToCursor((SKBitmap Bitmap, int HotX, int HotY) art)
    {
        var (bitmap, hx, hy) = art;
        using (bitmap)
        {
            using var avalonia = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul,
                bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);
            return new Cursor(avalonia, new PixelPoint(hx, hy));
        }
    }
}
