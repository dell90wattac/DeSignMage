using Avalonia.Headless.XUnit;
using DeSignMage.App.Canvas;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The select-hit cursor (R10 item 7): the arrow with a small crosshair at its tip, hotspot at the crosshair center.</summary>
public class CursorArtTests
{
    [Theory]
    [InlineData(1.0, 32)]
    [InlineData(1.5, 48)]
    [InlineData(2.0, 64)]
    public void The_bitmap_is_drawn_at_the_display_scale_with_the_hotspot_on_the_crosshair_center(double scale, int size)
    {
        var (bmp, hx, hy) = CursorArt.RenderSelectHit(scale);
        using (bmp)
        {
            Assert.Equal(size, bmp.Width);
            Assert.Equal(size, bmp.Height);
            Assert.Equal(hx, hy);
            Assert.True(hx > 0 && hx < size / 2);

            var center = bmp.GetPixel(hx, hy);
            Assert.Equal((byte)255, center.Alpha);                       // the center of the cross is solid black
            Assert.True(center.Red < 40 && center.Green < 40 && center.Blue < 40);

            int arm = (int)(5 * scale);                                  // the arms reach out from the center in all four directions
            foreach (var (dx, dy) in new[] { (arm, 0), (-arm, 0), (0, arm), (0, -arm) })
                Assert.True(bmp.GetPixel(hx + dx, hy + dy).Alpha > 200, $"no crosshair arm at ({dx},{dy})");

            Assert.Equal((byte)0, bmp.GetPixel(size - 1, 0).Alpha);      // the rest of the bitmap is transparent
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void The_arrow_body_sits_below_and_right_of_the_tip(double scale)
    {
        var (bmp, hx, hy) = CursorArt.RenderSelectHit(scale);
        using (bmp)
        {
            // Inside the arrow's white body, a little down from the tip: opaque white or black outline, never transparent.
            Assert.True(bmp.GetPixel(hx + (int)(2 * scale), hy + (int)(8 * scale)).Alpha > 0);
        }
    }

    [AvaloniaFact]
    public void An_Avalonia_cursor_can_be_made_from_it()
    {
        Assert.NotNull(CursorArt.CreateSelectHit(1));
        Assert.NotNull(CursorArt.CreateSelectHit(1.5));
    }

    [Theory]
    [InlineData(1.0, 32)]
    [InlineData(1.5, 48)]
    [InlineData(2.0, 64)]
    public void The_vector_draw_cursor_is_the_arrow_with_a_square_node_at_the_tip_and_the_hotspot_in_the_node(double scale, int size)
    {
        var (bmp, hx, hy) = CursorArt.RenderVectorDraw(scale);
        using (bmp)
        {
            Assert.Equal(size, bmp.Width);
            Assert.Equal(size, bmp.Height);
            Assert.Equal(hx, hy);
            Assert.True(hx > 0 && hx < size / 2);

            var inside = bmp.GetPixel(hx, hy);                            // the middle of the node is solid white
            Assert.Equal((byte)255, inside.Alpha);
            Assert.True(inside.Red > 230 && inside.Green > 230 && inside.Blue > 230);

            bool outline = false;                                         // its edge is a black line a couple of pixels away
            for (int d = 1; d <= (int)(3 * scale); d++)
            {
                var edge = bmp.GetPixel(hx - d, hy);
                outline |= edge.Alpha == 255 && edge.Red < 40;
            }
            Assert.True(outline, "no black node outline left of the hotspot");
            Assert.Equal((byte)0, bmp.GetPixel(hx - (int)(6 * scale), hy).Alpha);   // nothing further out to the left
            Assert.True(bmp.GetPixel(hx + (int)(2 * scale), hy + (int)(8 * scale)).Alpha > 0);   // the arrow body below and right of the tip
        }
    }

    [AvaloniaFact]
    public void An_Avalonia_vector_draw_cursor_can_be_made_from_it()
    {
        Assert.NotNull(CursorArt.CreateVectorDraw(1));
        Assert.NotNull(CursorArt.CreateVectorDraw(2));
    }
}