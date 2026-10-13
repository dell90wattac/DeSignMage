using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Rendering;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The letter-spacing dots under selected text are drawn at half the size they used to have (user, 2026-10-13).</summary>
public class LetterDotSizeTests
{
    /// <summary>Width and height in pixels of everything the overlay paints around one dot at (60, 60), at 1:1.</summary>
    private static (int Width, int Height) PaintedSize(int active)
    {
        var view = new Viewport();
        view.SetViewSize(new Vec2(120, 120));
        view.Set(1, Vec2.Zero);
        using var renderer = new OverlayRenderer();
        var picture = renderer.Record([new LetterDotsOverlay([new Vec2(60, 60)], active)], view, 120, 120);
        using var bmp = new SKBitmap(120, 120);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawPicture(picture.Picture);
        }
        picture.Release();

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 120; x++)
                if (bmp.GetPixel(x, y).Alpha > 40)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? (0, 0) : (maxX - minX + 1, maxY - minY + 1);
    }

    [Theory]
    [InlineData(-1)]   // resting dot
    [InlineData(0)]    // the dot being dragged (filled)
    public void A_dot_is_about_half_as_wide_as_before(int active)
    {
        var (w, h) = PaintedSize(active);
        // Before: radius 3.5 plus a one pixel ring = 8 pixels across. Now radius 1.75 plus the ring = 5 pixels at most.
        Assert.InRange(w, 3, 5);
        Assert.InRange(h, 3, 5);
    }

    [Fact]
    public void The_dot_radius_is_half_of_the_old_radius()
    {
        Assert.Equal(1.75f, LetterDotsOverlay.RadiusPx);
    }
}
