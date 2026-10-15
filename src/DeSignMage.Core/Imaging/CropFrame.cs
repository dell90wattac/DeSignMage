using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Imaging;

/// <summary>
/// The math of the crop frame in the Picture Editor: moving it, dragging its edges and corners (optionally keeping its proportions), drawing a new
/// one, all inside a canvas of <see cref="Width"/> x <see cref="Height"/> pixels. Pure functions of the frame when the drag began and the pointer, so
/// a drag never accumulates rounding.
/// </summary>
public static class CropFrame
{
    /// <summary>The frame moved by (dx, dy) pixels, kept inside the canvas.</summary>
    public static RectI Move(RectI start, double dx, double dy, int width, int height)
    {
        int x = (int)Math.Round(Math.Clamp(start.X + dx, 0, Math.Max(0, width - start.Width)));
        int y = (int)Math.Round(Math.Clamp(start.Y + dy, 0, Math.Max(0, height - start.Height)));
        return new RectI(x, y, start.Width, start.Height);
    }

    /// <summary>A frame drawn from one point to another (as dragged), inside the canvas.</summary>
    public static RectI Draw(Vec2 from, Vec2 to, int width, int height)
    {
        double x0 = Math.Clamp(Math.Min(from.X, to.X), 0, width), x1 = Math.Clamp(Math.Max(from.X, to.X), 0, width);
        double y0 = Math.Clamp(Math.Min(from.Y, to.Y), 0, height), y1 = Math.Clamp(Math.Max(from.Y, to.Y), 0, height);
        return FromEdges(x0, y0, x1, y1, width, height);
    }

    /// <summary>
    /// The frame with <paramref name="handle"/> dragged to <paramref name="pointer"/> (canvas pixels). With <paramref name="keepProportions"/> the frame keeps
    /// the width : height of the frame it started as: a corner drag keeps the opposite corner fixed, an edge drag keeps the frame centered across.
    /// </summary>
    public static RectI Resize(RectI start, Handle handle, Vec2 pointer, int width, int height, bool keepProportions)
    {
        double px = Math.Clamp(pointer.X, 0, width), py = Math.Clamp(pointer.Y, 0, height);
        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;

        bool left = handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        bool right = handle is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool top = handle is Handle.TopLeft or Handle.Top or Handle.TopRight;
        bool bottom = handle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;
        bool corner = SelectionHandles.IsCorner(handle);

        if (!keepProportions || start.Width < 1 || start.Height < 1)
        {
            if (left) l = px;
            if (right) r = px;
            if (top) t = py;
            if (bottom) b = py;
            return FromEdges(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b), width, height);
        }

        double aspect = (double)start.Width / start.Height;
        if (corner)
        {
            double ax = left ? r : l, ay = top ? b : t;                      // the corner that stays
            double w = Math.Abs(px - ax), h = Math.Abs(py - ay);
            if (w / aspect > h) h = w / aspect; else w = h * aspect;
            double sx = px >= ax ? 1 : -1, sy = py >= ay ? 1 : -1;
            double maxW = sx > 0 ? width - ax : ax, maxH = sy > 0 ? height - ay : ay;
            double k = Math.Min(1, Math.Min(maxW / Math.Max(w, 1e-9), maxH / Math.Max(h, 1e-9)));
            w *= k; h *= k;
            double x2 = ax + sx * w, y2 = ay + sy * h;
            return FromEdges(Math.Min(ax, x2), Math.Min(ay, y2), Math.Max(ax, x2), Math.Max(ay, y2), width, height);
        }

        double cx = (start.X + start.Right) / 2.0, cy = (start.Y + start.Bottom) / 2.0;
        if (left || right)
        {
            double fixedX = left ? r : l;
            double w = Math.Abs(px - fixedX), h = w / aspect;
            double maxW = (px >= fixedX ? width - fixedX : fixedX);
            double maxH = 2 * Math.Min(cy, height - cy);                      // centered across: limited by the nearer canvas edge
            double k = Math.Min(1, Math.Min(maxW / Math.Max(w, 1e-9), maxH / Math.Max(h, 1e-9)));
            w *= k; h *= k;
            double x2 = fixedX + (px >= fixedX ? w : -w);
            return FromEdges(Math.Min(fixedX, x2), cy - h / 2, Math.Max(fixedX, x2), cy + h / 2, width, height);
        }
        else
        {
            double fixedY = top ? b : t;
            double h = Math.Abs(py - fixedY), w = h * aspect;
            double maxH = (py >= fixedY ? height - fixedY : fixedY);
            double maxW = 2 * Math.Min(cx, width - cx);
            double k = Math.Min(1, Math.Min(maxH / Math.Max(h, 1e-9), maxW / Math.Max(w, 1e-9)));
            w *= k; h *= k;
            double y2 = fixedY + (py >= fixedY ? h : -h);
            return FromEdges(cx - w / 2, Math.Min(fixedY, y2), cx + w / 2, Math.Max(fixedY, y2), width, height);
        }
    }

    /// <summary>The handle (if any) of <paramref name="frame"/> within <paramref name="radius"/> canvas pixels of <paramref name="point"/>; corners win over edges.</summary>
    public static Handle? HandleAt(RectI frame, Vec2 point, double radius)
    {
        Handle? best = null;
        double bestD = radius;
        var bounds = new Rect2(frame.X, frame.Y, frame.Right, frame.Bottom);
        foreach (var h in SelectionHandles.All)
        {
            double d = Vec2.Distance(SelectionHandles.Position(bounds, h), point);
            if (d <= bestD) { bestD = d; best = h; }
        }
        return best;
    }

    private static RectI FromEdges(double l, double t, double r, double b, int width, int height)
    {
        int x0 = (int)Math.Round(Math.Clamp(l, 0, width)), y0 = (int)Math.Round(Math.Clamp(t, 0, height));
        int x1 = (int)Math.Round(Math.Clamp(r, 0, width)), y1 = (int)Math.Round(Math.Clamp(b, 0, height));
        if (x1 <= x0) x1 = Math.Min(width, x0 + 1);
        if (y1 <= y0) y1 = Math.Min(height, y0 + 1);
        if (x1 <= x0) x0 = Math.Max(0, x1 - 1);
        if (y1 <= y0) y0 = Math.Max(0, y1 - 1);
        return new RectI(x0, y0, x1 - x0, y1 - y0);
    }
}
