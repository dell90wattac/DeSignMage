using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Editing;

public enum Handle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// What dragging a handle does. Each handle has one dedicated job:
/// corners scale proportionally, left/right middles extend/condense the width, the top middle slants,
/// the bottom middle rotates. With Ctrl held the top middle instead extends the height upward (the bottom edge stays) and the
/// bottom middle extends it downward (the top edge stays); those two are plain one-axis scales (requirement R21).
/// </summary>
public enum HandleRole { Scale, Extend, Slant, Rotate, ExtendUp, ExtendDown }

/// <summary>Positions, roles and picking of the 8 handles around a selection's bounds.</summary>
public static class SelectionHandles
{
    public static readonly Handle[] All = Enum.GetValues<Handle>();

    public static Vec2 Position(Rect2 b, Handle h) => h switch
    {
        Handle.TopLeft => new(b.Left, b.Top),
        Handle.Top => new(b.Center.X, b.Top),
        Handle.TopRight => new(b.Right, b.Top),
        Handle.Right => new(b.Right, b.Center.Y),
        Handle.BottomRight => new(b.Right, b.Bottom),
        Handle.Bottom => new(b.Center.X, b.Bottom),
        Handle.BottomLeft => new(b.Left, b.Bottom),
        Handle.Left => new(b.Left, b.Center.Y),
        _ => b.Center,
    };

    public static Handle Opposite(Handle h) => (Handle)(((int)h + 4) % 8);

    public static bool IsCorner(Handle h) => ((int)h & 1) == 0;

    /// <summary>
    /// The top corners scale proportionally; the bottom corners' job is not settled yet,
    /// so they scale too until confirmed.
    /// </summary>
    public static HandleRole Role(Handle h, bool ctrl = false) => h switch
    {
        Handle.Left or Handle.Right => HandleRole.Extend,
        Handle.Top => ctrl ? HandleRole.ExtendUp : HandleRole.Slant,
        Handle.Bottom => ctrl ? HandleRole.ExtendDown : HandleRole.Rotate,
        _ => HandleRole.Scale,
    };

    public static CursorKind Cursor(Handle h, bool ctrl = false) => Role(h, ctrl) switch
    {
        HandleRole.Extend => CursorKind.SizeWE,
        HandleRole.ExtendUp or HandleRole.ExtendDown => CursorKind.SizeNS,
        HandleRole.Slant => CursorKind.Slant,
        HandleRole.Rotate => CursorKind.Rotate,
        _ => h is Handle.TopLeft or Handle.BottomRight ? CursorKind.SizeNWSE : CursorKind.SizeNESW,
    };

    /// <summary>The handle within <paramref name="radiusPx"/> screen DIPs of <paramref name="screen"/>, if any.</summary>
    public static Handle? HitTest(Viewport view, Rect2 docBounds, Vec2 screen, double radiusPx)
    {
        if (docBounds.IsEmpty) return null;
        Handle? best = null;
        double bestD = radiusPx;
        foreach (var h in All)
        {
            double d = Vec2.Distance(view.ToScreen(Position(docBounds, h)), screen);
            if (d <= bestD) { bestD = d; best = h; }
        }
        return best;
    }
}
