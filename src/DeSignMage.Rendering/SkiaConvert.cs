using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>Conversions between Core value types and SkiaSharp types.</summary>
public static class SkiaConvert
{
    public static SKMatrix ToSK(this Affine2 m) => new(
        (float)m.M11, (float)m.M21, (float)m.OffsetX,
        (float)m.M12, (float)m.M22, (float)m.OffsetY,
        0, 0, 1);

    public static SKColor ToSK(this DocColor c) => new(c.R, c.G, c.B, c.A);

    public static SKRect ToSK(this Rect2 r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);

    public static SKPoint ToSK(this Vec2 p) => new((float)p.X, (float)p.Y);

    public static SKPath ToSKPath(this PathGeometry g)
    {
        g = g.WithArcsAsCubics();   // Skia has no three-point circular arc verb
        var path = new SKPath { FillType = g.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        var pts = g.Points;
        int pi = 0;
        foreach (var v in g.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo: path.MoveTo(pts[pi++].ToSK()); break;
                case PathVerb.LineTo: path.LineTo(pts[pi++].ToSK()); break;
                case PathVerb.CubicTo: path.CubicTo(pts[pi].ToSK(), pts[pi + 1].ToSK(), pts[pi + 2].ToSK()); pi += 3; break;
                case PathVerb.Close: path.Close(); break;
            }
        }
        return path;
    }

    public static SKStrokeJoin ToSK(this LineJoin j) => j switch
    {
        LineJoin.Round => SKStrokeJoin.Round,
        LineJoin.Bevel => SKStrokeJoin.Bevel,
        _ => SKStrokeJoin.Miter,
    };

    public static SKStrokeCap ToSK(this LineCap c) => c switch
    {
        LineCap.Round => SKStrokeCap.Round,
        LineCap.Square => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt,
    };
}
