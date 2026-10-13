using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Effects ▸ Gradient Blend: the control-point tool. It edits the fill of the one selected object. The control line is drawn on the object with a
/// square handle on every color of the gradient (the current one larger):
/// <list type="bullet">
/// <item>Drag a handle to move it. The two ends of a Linear line move freely (Shift: snap to 45°); Vertical and Horizontal ends slide along their
/// axis only (the line cannot be rotated); a Circle or Ellipse center moves the whole shape, its end handle sets the radius; an Ellipse has a
/// second handle for its other axis, kept at right angles. A color between the ends slides along the line.</item>
/// <item>Ctrl+click the line adds a color there; Alt+click adds a highlight (a bright color between two copies of the base color, dragged as one);
/// Shift+click a color removes it (a highlight takes its two neighbors with it); Tab / Shift+Tab pick the next / previous color.</item>
/// <item>The picked color is edited in the Color Mixer or with a click on the Palette Strip / Project Color Bar (<see cref="EditorSession.ApplyFill"/> routes it here).</item>
/// </list>
/// Every drag is one undo step. The gradient is stored relative to the object's bounds (<see cref="GradientPaint"/>), so it follows the object.
/// </summary>
public sealed class GradientTool : Tool
{
    private const double MinGap = 0.002;   // closest two colors may get, as a fraction of the control line

    private enum Part { None, Node, Axis2 }

    private Part _dragPart;
    private int _dragIndex;
    private GradientPaint? _dragStart;
    private Vec2 _downScreen;
    private bool _moved, _shiftClick;

    public override string Id => "gradient";
    public override string DisplayName => "Gradient Blend";
    public override CursorKind Cursor => CursorKind.Cross;

    public override string Hint => Target is null
        ? "GRADIENT BLEND: Select one shape or text to blend its color   To ZOOM: Press right button"
        : Gradient is null
            ? "GRADIENT BLEND: Pick a gradient type in the Context Bar to blend this fill   To ZOOM: Press right button"
            : "GRADIENT BLEND: Drag the handles (Ctrl+click the line: add a color   Alt+click: add a highlight   Shift+click a color: remove it   Tab: next color)   To ZOOM: Press right button";

    /// <summary>The current color's index in the gradient; always valid for the gradient that is there.</summary>
    public int CurrentNode { get; private set; }

    /// <summary>Raised when the target, the gradient or the current color changed.</summary>
    public event EventHandler? Changed;

    // ---- What is being edited ----

    /// <summary>The object whose fill the tool edits: the single selected shape, path or text. Groups, combined objects and pictures have no one fill.</summary>
    public DocObject? Target => TargetOf(Session);

    public static DocObject? TargetOf(EditorSession session)
    {
        if (session.Selection.Count != 1) return null;
        var o = session.Selection.Items[0];
        if (o is GroupObject or CombinedObject or ImageObject || !o.Visible) return null;
        var g = o.GetLocalGeometry();
        return g is { IsEmpty: false } ? o : null;
    }

    public static bool CanTarget(EditorSession session) => TargetOf(session) is not null;

    public GradientPaint? Gradient => Target?.Style.Fill as GradientPaint;

    /// <summary>The color of the picked handle (null without a gradient).</summary>
    public DocColor? CurrentNodeColor => Gradient is { } g ? g.Stops[Math.Clamp(CurrentNode, 0, g.Stops.Count - 1)].Color : null;

    private Rect2 Bounds => Target?.GetLocalGeometry()?.GetBounds() ?? Rect2.Empty;

    private Affine2 World => Target?.GetWorldTransform() ?? Affine2.Identity;

    public override void Activate()
    {
        CurrentNode = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public override void Deactivate()
    {
        base.Deactivate();
        Session.EndStyleGesture();
    }

    // ---- Changing the gradient (the Context Bar and the Color Mixer call these) ----

    /// <summary>
    /// Sets the gradient type. Without a gradient the fill's color becomes the first color and a lighter or darker one the last. With one,
    /// the colors stay and the control points restart for the new kind.
    /// </summary>
    public void SetKind(GradientKind kind)
    {
        if (Target is not { } t) return;
        var next = t.Style.Fill is GradientPaint g
            ? g.WithKind(kind)
            : GradientPaint.Create(kind, (t.Style.Fill as SolidPaint)?.Color ?? DocColor.Black);
        Apply(next, "Gradient Type");
    }

    /// <summary>Removes the gradient: the fill becomes the plain color of the first stop.</summary>
    public void Remove()
    {
        if (Gradient is not { } g) return;
        CurrentNode = 0;
        Session.ApplyStyleChange("Remove Gradient", s => s.WithFill(g.ToSolid()));
        Raise();
    }

    public void SetAlternateColors(bool on)
    {
        if (Gradient is { } g && g.AlternateColors != on) Apply(g.WithFlags(on, g.Reflect), "Alternate Colors");
    }

    public void SetReflect(bool on)
    {
        if (Gradient is { } g && g.Reflect != on) Apply(g.WithFlags(g.AlternateColors, on), "Reflect Gradient");
    }

    /// <summary>Puts a whole gradient on the target (a preset); the shape is relative, so it fits any object.</summary>
    public void SetGradient(GradientPaint gradient, string description = "Gradient Preset")
    {
        if (Target is null) return;
        CurrentNode = Math.Clamp(CurrentNode, 0, gradient.Stops.Count - 1);
        Apply(gradient, description);
    }

    /// <summary>Gives the current color a new color (its transparency is kept). Inside a gesture (a slider drag) the changes are one undo step.</summary>
    public bool SetNodeColor(DocColor color)
    {
        if (Gradient is not { } g) return false;
        int i = Math.Clamp(CurrentNode, 0, g.Stops.Count - 1);
        var stops = g.Stops.ToArray();
        stops[i] = stops[i] with { Color = color with { A = stops[i].Color.A } };
        Apply(g.WithStops(stops), "Gradient Color");
        return true;
    }

    public void SelectNode(int index)
    {
        if (Gradient is not { } g) return;
        CurrentNode = Math.Clamp(index, 0, g.Stops.Count - 1);
        Raise();
        Redraw();
    }

    public void CycleNode(int step)
    {
        if (Gradient is not { } g) return;
        CurrentNode = ((CurrentNode + step) % g.Stops.Count + g.Stops.Count) % g.Stops.Count;
        Raise();
        Redraw();
    }

    private void Apply(GradientPaint gradient, string description)
    {
        Session.ApplyStyleChange(description, s => s.WithFill(gradient));
        CurrentNode = Math.Clamp(CurrentNode, 0, gradient.Stops.Count - 1);
        Raise();
    }

    private void Raise()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    // ---- Handles ----

    /// <summary>The handles in document coordinates: one per color, then (for an ellipse) the second axis.</summary>
    public IReadOnlyList<(bool IsAxis2, int Index, Vec2 Position)> Handles()
    {
        var list = new List<(bool, int, Vec2)>();
        if (Gradient is not { } g) return list;
        var f = g.Frame(Bounds);
        var w = World;
        for (int i = 0; i < g.Stops.Count; i++) list.Add((false, i, w.Transform(f.PointAt(g.Stops[i].Offset))));
        if (g.Kind == GradientKind.Ellipse) list.Add((true, -1, w.Transform(f.P2)));
        return list;
    }

    private Vec2? ToLocal(Vec2 doc)
    {
        var w = World;
        return w.IsInvertible ? w.Invert().Transform(doc) : null;
    }

    private (bool IsAxis2, int Index)? HitHandle(Vec2 screen)
    {
        double radius = Session.HandleRadiusPx + 3;
        (bool, int)? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (isAxis2, index, pos) in Handles())
        {
            double d = Vec2.Distance(Session.Viewport.ToScreen(pos), screen);
            // on a tie the later handle (the one drawn on top) wins
            if (d <= radius && d <= bestDistance) { best = (isAxis2, index); bestDistance = d; }
        }
        return best;
    }

    /// <summary>The parameter (0..1) on the control line closest to a screen point, or null when the point is farther than a few pixels from it.</summary>
    private double? HitLine(Vec2 screen)
    {
        if (Gradient is not { } g) return null;
        var f = g.Frame(Bounds);
        var a = Session.Viewport.ToScreen(World.Transform(f.P0));
        var b = Session.Viewport.ToScreen(World.Transform(f.P1));
        var ab = b - a;
        if (ab.LengthSquared < 1e-9) return null;
        double t = Math.Clamp(Vec2.Dot(screen - a, ab) / ab.LengthSquared, 0, 1);
        return Vec2.Distance(a + ab * t, screen) <= Session.PickTolerancePx + 2 ? t : null;
    }

    // ---- Pointer ----

    public override void PointerDown(PointerInput e)
    {
        if (Target is not { } target || Gradient is not { } g)
        {
            SelectObjectAt(e);
            return;
        }
        _dragPart = Part.None;

        if (HitHandle(e.Screen) is { } hit)
        {
            // Shift on a color is "remove" when it stays a click, and "snap the angle" when it becomes a drag (decided on release).
            _shiftClick = !hit.IsAxis2 && e.Shift;
            if (!hit.IsAxis2) CurrentNode = hit.Index;
            _dragPart = hit.IsAxis2 ? Part.Axis2 : Part.Node;
            _dragIndex = hit.Index;
            _dragStart = g;
            _downScreen = e.Screen;
            _moved = false;
            Session.BeginStyleGesture();
            Raise();
            return;
        }

        if ((e.Control || e.Alt) && HitLine(e.Screen) is { } t)
        {
            if (e.Alt) AddHighlight(g, t); else AddNode(g, t);
            return;
        }

        SelectObjectAt(e);
    }

    /// <summary>A click away from the control line picks another object (so the tool follows what is selected).</summary>
    private void SelectObjectAt(PointerInput e)
    {
        var hit = HitTesting.HitTest(Session.Document, e.Doc, Session.Viewport.ToDocLength(Session.PickTolerancePx));
        if (hit is null || Session.Selection.Contains(hit)) return;
        Session.Selection.Set(hit);
        CurrentNode = 0;
        Raise();
    }

    public override void PointerMove(PointerInput e)
    {
        if (_dragPart == Part.None || _dragStart is not { } start || ToLocal(e.Doc) is not { } local) return;
        if (!_moved && Vec2.Distance(_downScreen, e.Screen) < 2) return;   // a click, not a drag
        _moved = true;
        var next = _dragPart == Part.Axis2 ? DragAxis2(start, local) : DragNode(start, _dragIndex, local, e.Shift);
        Session.ApplyStyleChange("Move Gradient", s => s.WithFill(next));
        Raise();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_dragPart == Part.None) return;
        int index = _dragIndex;
        bool remove = _shiftClick && !_moved && _dragPart == Part.Node;
        _dragPart = Part.None;
        _dragStart = null;
        Session.EndStyleGesture();
        if (remove) RemoveNode(index);
        Raise();
    }

    public override void Cancel()
    {
        if (_dragPart == Part.None) return;
        _dragPart = Part.None;
        _dragStart = null;
        Session.EndStyleGesture();
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key != EditorKey.Tab || Gradient is null) return false;
        CycleNode((modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        return true;
    }

    // ---- Dragging (pure functions of the gradient at mouse-down and the pointer, in the object's local space) ----

    /// <summary>The gradient after the handle of color <paramref name="index"/> of <paramref name="g"/> was dragged to <paramref name="local"/>.</summary>
    internal GradientPaint DragNode(GradientPaint g, int index, Vec2 local, bool snap)
    {
        var b = Bounds;
        var f = g.Frame(b);
        int last = g.Stops.Count - 1;

        if (index > 0 && index < last)
            return SlideStop(g, f, index, local);

        // an end of the control line
        var p0 = f.P0; var p1 = f.P1; var p2 = f.P2;
        if (index == 0)
        {
            if (g.IsLinear) p0 = LockToAxis(g.Kind, p0, p1, local, snap);
            else
            {
                // the center of a circle or ellipse carries the whole shape
                var delta = local - p0;
                p0 += delta; p1 += delta; p2 += delta;
            }
        }
        else
        {
            if (g.IsLinear) p1 = LockToAxis(g.Kind, p1, p0, local, snap);
            else if (g.Kind == GradientKind.Circle) p1 = local;
            else
            {
                // the first axis of an ellipse: the second keeps its length and stays at right angles on the same side
                var axis = local - p0;
                if (axis.LengthSquared > 1e-12)
                {
                    double len2 = (p2 - p0).Length;
                    double side = Vec2.Cross(f.Axis1, p2 - p0) >= 0 ? 1 : -1;
                    var unit = axis / axis.Length;
                    p1 = local;
                    p2 = p0 + new Vec2(-unit.Y, unit.X) * (len2 * side);
                }
            }
        }
        return Rebuild(g, b, p0, p1, p2);
    }

    /// <summary>The second axis handle of an ellipse: its length follows the pointer, its direction stays at right angles to the first axis.</summary>
    internal GradientPaint DragAxis2(GradientPaint g, Vec2 local)
    {
        if (g.Kind != GradientKind.Ellipse) return g;
        var b = Bounds;
        var f = g.Frame(b);
        var dir = f.P2 - f.P0;
        if (dir.LengthSquared < 1e-12) return g;
        var unit = dir / dir.Length;
        double length = Math.Max(Vec2.Dot(local - f.P0, unit), 1e-6);
        return Rebuild(g, b, f.P0, f.P1, f.P0 + unit * length);
    }

    private static Vec2 LockToAxis(GradientKind kind, Vec2 moving, Vec2 fixedEnd, Vec2 local, bool snap)
    {
        switch (kind)
        {
            case GradientKind.Vertical: return new Vec2(moving.X, local.Y);
            case GradientKind.Horizontal: return new Vec2(local.X, moving.Y);
        }
        if (!snap) return local;
        // Shift: the line snaps to the nearest multiple of 45° about the other end
        var d = local - fixedEnd;
        double angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        double r = d.Length;
        return fixedEnd + new Vec2(Math.Cos(angle), Math.Sin(angle)) * r;
    }

    private GradientPaint SlideStop(GradientPaint g, GradientFrame f, int index, Vec2 local)
    {
        var axis = f.Axis;
        if (axis.LengthSquared < 1e-18) return g;
        double t = Vec2.Dot(local - f.P0, axis) / axis.LengthSquared;
        var stops = g.Stops.ToArray();
        double delta = t - stops[index].Offset;

        // A highlight carries the two colors next to it; the group may not cross its outer neighbors or the ends.
        int from = index, to = index;
        if (stops[index].Highlight && index > 1 && index < stops.Length - 2) { from = index - 1; to = index + 1; }
        double lowest = stops[from - 1].Offset + MinGap - stops[from].Offset;
        double highest = stops[to + 1].Offset - MinGap - stops[to].Offset;
        delta = Math.Clamp(delta, lowest, Math.Max(lowest, highest));
        for (int i = from; i <= to; i++) stops[i] = stops[i] with { Offset = stops[i].Offset + delta };
        return g.WithStops(stops);
    }

    private static GradientPaint Rebuild(GradientPaint g, Rect2 bounds, Vec2 p0, Vec2 p1, Vec2 p2)
    {
        var pts = new List<Vec2> { GradientPaint.ToRelative(p0, bounds), GradientPaint.ToRelative(p1, bounds) };
        if (g.Kind == GradientKind.Ellipse) pts.Add(GradientPaint.ToRelative(p2, bounds));
        return g.WithPoints(pts);
    }

    // ---- Adding and removing colors ----

    private void AddNode(GradientPaint g, double t)
    {
        if (g.Stops.Count >= GradientPaint.MaxStops) return;
        t = Math.Clamp(t, MinGap, 1 - MinGap);
        var stops = g.Stops.ToList();
        int at = stops.FindIndex(s => s.Offset > t);
        if (at < 0) at = stops.Count - 1;
        stops.Insert(at, new GradientNode(t, GradientRamp.ColorAt(g, t)));
        CurrentNode = at;
        Apply(g.WithStops(stops), "Add Gradient Color");
    }

    private void AddHighlight(GradientPaint g, double t)
    {
        if (g.Stops.Count + 3 > GradientPaint.MaxStops) return;
        double half = Math.Min(0.04, Math.Min(t, 1 - t) / 2);
        if (half < MinGap) return;
        var baseColor = GradientRamp.ColorAt(g, t);
        var stops = g.Stops.ToList();
        var add = new[]
        {
            new GradientNode(t - half, GradientRamp.ColorAt(g, t - half)),
            new GradientNode(t, DocColor.White, Highlight: true),
            new GradientNode(t + half, GradientRamp.ColorAt(g, t + half)),
        };
        // the flanking colors are the base color at the highlight, as the legacy blend stores them
        add[0] = add[0] with { Color = baseColor };
        add[2] = add[2] with { Color = baseColor };
        int at = stops.FindIndex(s => s.Offset > t - half);
        if (at < 0) at = stops.Count - 1;
        stops.InsertRange(at, add);
        CurrentNode = at + 1;
        Apply(g.WithStops(stops), "Add Gradient Highlight");
    }

    /// <summary>Removes a color (a highlight with its two neighbors). The two ends and the last two colors stay.</summary>
    public bool RemoveNode(int index)
    {
        if (Gradient is not { } g || index <= 0 || index >= g.Stops.Count - 1) return false;
        var stops = g.Stops.ToList();
        int from = index, count = 1;
        if (stops[index].Highlight && index > 1 && index < stops.Count - 2) { from = index - 1; count = 3; }
        if (stops.Count - count < GradientPaint.MinStops) return false;
        stops.RemoveRange(from, count);
        CurrentNode = Math.Clamp(from, 0, stops.Count - 1);
        Apply(g.WithStops(stops), "Remove Gradient Color");
        return true;
    }

    // ---- Overlay ----

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        if (Gradient is not { } g) return;
        var f = g.Frame(Bounds);
        var w = World;
        var marks = new List<GradientHandleMark>();
        foreach (var (isAxis2, index, pos) in Handles())
        {
            if (isAxis2) marks.Add(new GradientHandleMark(pos, null, false, false));
            else marks.Add(new GradientHandleMark(pos, g.Stops[index].Color, index == CurrentNode, g.Stops[index].Highlight));
        }
        PathGeometry? shape = null;
        if (!g.IsLinear && !f.IsDegenerate)
            shape = PathGeometry.Ellipse(new Rect2(-1, -1, 1, 1)).Transform(f.UnitToLocal * w);
        items.Add(new GradientOverlay(w.Transform(f.P0), w.Transform(f.P1), g.Kind == GradientKind.Ellipse ? w.Transform(f.P2) : null, shape, marks));
    }
}
