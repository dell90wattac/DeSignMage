using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

public class HitTestTests
{
    private readonly Document _doc = NewDoc();

    private T Add<T>(T o) where T : DocObject
    {
        _doc.Insert(_doc.ActiveLayer, -1, o);
        return o;
    }

    [Fact]
    public void Topmost_filled_object_wins()
    {
        var below = Add(Rect(0, 0, 100, 100));
        var above = Add(Rect(50, 50, 100, 100));
        Assert.Same(above, HitTesting.HitTest(_doc, new Vec2(75, 75), 1));
        Assert.Same(below, HitTesting.HitTest(_doc, new Vec2(25, 25), 1));
        Assert.Null(HitTesting.HitTest(_doc, new Vec2(500, 500), 1));
    }

    [Fact]
    public void Painted_pick_of_an_unfilled_shape_is_only_near_its_stroke()
    {
        // HitPainted is the "what is visible here" pick (eyedropper, text and vector tools); click-to-select is HitTest (R10).
        var outline = Add(Rect(0, 0, 100, 100, new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 4))));
        Assert.Same(outline, HitTesting.HitPainted(_doc, new Vec2(1, 50), 1));
        Assert.Same(outline, HitTesting.HitPainted(_doc, new Vec2(-2.5, 50), 1));   // half stroke + tolerance
        Assert.Null(HitTesting.HitPainted(_doc, new Vec2(50, 50), 1));
        Assert.Same(outline, HitTesting.HitTest(_doc, new Vec2(50, 50), 1));         // but the empty middle selects it
    }

    [Fact]
    public void Ellipse_corners_are_not_hit()
    {
        var e = Add(new EllipseObject(100, 100) { Style = ObjectStyle.FillOnly(DocColor.Black) });
        Assert.Same(e, HitTesting.HitTest(_doc, new Vec2(50, 50), 0.5));
        Assert.Null(HitTesting.HitTest(_doc, new Vec2(3, 3), 0.5));
    }

    [Fact]
    public void Hidden_locked_objects_and_layers_are_skipped()
    {
        var r = Add(Rect(0, 0, 10, 10));
        r.Locked = true;
        Assert.Null(HitTesting.HitTest(_doc, new Vec2(5, 5), 1));
        r.Locked = false;
        _doc.ActiveLayer.Visible = false;
        Assert.Null(HitTesting.HitTest(_doc, new Vec2(5, 5), 1));
    }

    [Fact]
    public void Hitting_a_group_child_returns_the_group()
    {
        var g = Add(new GroupObject());
        _doc.Insert(g, -1, Rect(0, 0, 10, 10));
        Assert.Same(g, HitTesting.HitTest(_doc, new Vec2(5, 5), 1));
    }

    [Fact]
    public void Marquee_selects_only_fully_enclosed_objects()
    {
        var inside = Add(Rect(10, 10, 10, 10));
        Add(Rect(15, 15, 100, 100));
        var found = HitTesting.EnclosedBy(_doc, new Rect2(0, 0, 50, 50)).ToList();
        Assert.Equal([inside], found);
    }
}
