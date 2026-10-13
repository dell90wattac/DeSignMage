using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// Turns a <see cref="GradientPaint"/> into a Skia shader. The color ramp comes from <see cref="GradientRamp"/> (HSV blending, alternate
/// colors, reflect), as dense plain stops; the shader only places it: a linear gradient along the control line, or a unit-radius radial gradient
/// squeezed into the circle/ellipse by the local matrix. Everything is built in the object's own space and carried to the page by
/// <paramref name="localToTarget"/>, so a gradient follows any move, scale, rotation or skew of the object exactly.
/// </summary>
public static class GradientShaders
{
    /// <summary>The shader, or null when the gradient has no extent (callers then fill with the last color).</summary>
    public static SKShader? Create(GradientPaint g, Rect2 localBounds, Affine2 localToTarget)
    {
        var frame = g.Frame(localBounds);
        if (frame.IsDegenerate) return null;

        var ramp = GradientRamp.Build(g);
        var colors = new SKColor[ramp.Count];
        var positions = new float[ramp.Count];
        for (int i = 0; i < ramp.Count; i++)
        {
            colors[i] = new SKColor(ramp[i].R, ramp[i].G, ramp[i].B, ramp[i].A);
            positions[i] = (float)ramp[i].T;
        }

        if (g.IsLinear)
            return SKShader.CreateLinearGradient(frame.P0.ToSK(), frame.P1.ToSK(), colors, positions, SKShaderTileMode.Clamp, localToTarget.ToSK());
        return SKShader.CreateRadialGradient(new SKPoint(0, 0), 1, colors, positions, SKShaderTileMode.Clamp, (frame.UnitToLocal * localToTarget).ToSK());
    }

    /// <summary>The color to fill with when <see cref="Create"/> has nothing to blend along.</summary>
    public static SKColor FallbackColor(GradientPaint g) => g.Stops[^1].Color.ToSK();
}
