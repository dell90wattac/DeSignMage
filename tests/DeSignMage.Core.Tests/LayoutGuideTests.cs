using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Layout ▸ Guides (edges, centers, text cap height and baseline), Layout Properties (page size) and Size to Fit.</summary>
public class LayoutGuideTests
{
    // Page is 24 x 12 in = 1728 x 864 pt.
    private readonly EditorSession _s = new(NewDoc());

    private DocObject AddRect(double x, double y, double w, double h, ObjectStyle? style = null)
    {
        var r = Rect(x, y, w, h, style);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, r));
        return r;
    }

    private TextObject AddText(string text, double size, Affine2 transform)
    {
        var t = new TextObject(text, "Arial", size) { Transform = transform };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t));
        return t;
    }

    private static double CapOf(TextObject t) => TextEngine.CapHeightEm(t.FontFamily, t.FontWeight, t.Italic) * t.FontSize;

    // ---- Edges and centers ----

    [Fact]
    public void Along_all_edges_puts_four_guides_on_the_selection_bounds()
    {
        var r = AddRect(100, 100, 200, 50);
        _s.Selection.Set(r);
        Assert.Equal(4, _s.AddGuides(GuidePlace.AllEdges));

        var g = _s.Document.Guides;
        Assert.Equal(4, g.Count);
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Horizontal && Math.Abs(x.Origin.Y - 100) < 1e-9);   // top
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Horizontal && Math.Abs(x.Origin.Y - 150) < 1e-9);   // bottom
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Vertical && Math.Abs(x.Origin.X - 100) < 1e-9);     // left
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Vertical && Math.Abs(x.Origin.X - 300) < 1e-9);     // right
        Assert.Equal([r], _s.Selection.Items);   // the selection stays
    }

    [Theory]
    [InlineData(GuidePlace.TopEdge, GuideOrientation.Horizontal, 100.0)]
    [InlineData(GuidePlace.BottomEdge, GuideOrientation.Horizontal, 150.0)]
    [InlineData(GuidePlace.LeftEdge, GuideOrientation.Vertical, 100.0)]
    [InlineData(GuidePlace.RightEdge, GuideOrientation.Vertical, 300.0)]
    [InlineData(GuidePlace.HorizontalCenter, GuideOrientation.Horizontal, 125.0)]
    [InlineData(GuidePlace.VerticalCenter, GuideOrientation.Vertical, 200.0)]
    public void Each_single_edge_or_center_adds_one_guide_at_its_position(GuidePlace place, GuideOrientation orientation, double position)
    {
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        Assert.Equal(1, _s.AddGuides(place));
        var guide = Assert.Single(_s.Document.Guides);
        Assert.Equal(orientation, guide.Orientation);
        Assert.Equal(position, orientation == GuideOrientation.Horizontal ? guide.Origin.Y : guide.Origin.X, 9);
    }

    [Fact]
    public void Both_centers_adds_the_vertical_and_the_horizontal_guide_through_the_center()
    {
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        Assert.Equal(2, _s.AddGuides(GuidePlace.BothCenters));
        Assert.Contains(_s.Document.Guides, g => g.Orientation == GuideOrientation.Vertical && Math.Abs(g.Origin.X - 200) < 1e-9);
        Assert.Contains(_s.Document.Guides, g => g.Orientation == GuideOrientation.Horizontal && Math.Abs(g.Origin.Y - 125) < 1e-9);
    }

    [Fact]
    public void Edge_guides_follow_a_group_as_a_whole()
    {
        var a = AddRect(100, 100, 50, 50);
        var b = AddRect(300, 220, 60, 40);
        var group = _s.GroupObjects([a, b])!;
        _s.Selection.Set(group);
        _s.AddGuides(GuidePlace.AllEdges);
        Assert.Contains(_s.Document.Guides, g => g.Orientation == GuideOrientation.Vertical && Math.Abs(g.Origin.X - 360) < 1e-9);
        Assert.Contains(_s.Document.Guides, g => g.Orientation == GuideOrientation.Horizontal && Math.Abs(g.Origin.Y - 260) < 1e-9);
    }

    [Fact]
    public void With_nothing_selected_the_edges_and_centers_are_the_pages()
    {
        _s.AddGuides(GuidePlace.AllEdges);
        _s.AddGuides(GuidePlace.BothCenters);
        var g = _s.Document.Guides;
        Assert.Equal(6, g.Count);
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Vertical && Math.Abs(x.Origin.X) < 1e-9);
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Vertical && Math.Abs(x.Origin.X - 1728) < 1e-9);
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Vertical && Math.Abs(x.Origin.X - 864) < 1e-9);
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Horizontal && Math.Abs(x.Origin.Y - 864) < 1e-9);
        Assert.Contains(g, x => x.Orientation == GuideOrientation.Horizontal && Math.Abs(x.Origin.Y - 432) < 1e-9);
    }

    [Fact]
    public void With_a_resized_page_the_page_edges_follow_the_page()
    {
        _s.ResizePage(new Rect2(-36, 12, 300, 200));
        _s.AddGuides(GuidePlace.AllEdges);
        Assert.Contains(_s.Document.Guides, g => g.Orientation == GuideOrientation.Vertical && Math.Abs(g.Origin.X + 36) < 1e-9);
        Assert.Contains(_s.Document.Guides, g => g.Orientation == GuideOrientation.Horizontal && Math.Abs(g.Origin.Y - 200) < 1e-9);
    }

    [Fact]
    public void A_set_of_guides_is_one_undo_step_and_undo_removes_exactly_those()
    {
        _s.History.Execute(new AddGuideCommand(Guide.Vertical(5)));
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        int steps = _s.History.UndoList.Count;

        _s.AddGuides(GuidePlace.AllEdges);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Add Guides", _s.History.UndoDescription);
        Assert.Equal(5, _s.Document.Guides.Count);

        _s.Undo();
        Assert.Single(_s.Document.Guides);
        Assert.Equal(5, _s.Document.Guides[0].Origin.X);
        _s.Redo();
        Assert.Equal(5, _s.Document.Guides.Count);
    }

    [Fact]
    public void Placing_the_same_guide_twice_adds_nothing_the_second_time()
    {
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        Assert.Equal(1, _s.AddGuides(GuidePlace.TopEdge));
        int steps = _s.History.UndoList.Count;
        Assert.Equal(0, _s.AddGuides(GuidePlace.TopEdge));
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(1, _s.AddGuides(GuidePlace.BottomEdge));   // a different edge still works
    }

    [Fact]
    public void All_edges_after_one_edge_adds_only_the_missing_ones()
    {
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        _s.AddGuides(GuidePlace.TopEdge);
        Assert.Equal(3, _s.AddGuides(GuidePlace.AllEdges));
        Assert.Equal(4, _s.Document.Guides.Count);
    }

    [Fact]
    public void The_new_guides_are_normal_guides_selectable_lockable_removable()
    {
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        _s.AddGuides(GuidePlace.LeftEdge);
        var g = _s.Document.Guides[0];

        _s.SelectedGuide = g;
        Assert.Same(g, _s.SelectedGuide);
        _s.SetGuideLocked(g, true);
        Assert.True(g.Locked);
        _s.RemoveGuide(g);
        Assert.Empty(_s.Document.Guides);
    }

    [Fact]
    public void Adding_guides_shows_hidden_guides()
    {
        _s.ShowGuides = false;
        _s.AddGuides(GuidePlace.BothCenters);
        Assert.True(_s.ShowGuides);
    }

    // ---- Text: cap height and baseline ----

    [Fact]
    public void Baseline_and_cap_height_guides_sit_at_the_text_s_baseline_and_a_capital_height_above()
    {
        var t = AddText("Hello", 72, Affine2.Translation(100, 300));
        _s.Selection.Set(t);

        Assert.Equal(1, _s.AddGuides(GuidePlace.TextBaseline));
        Assert.Equal(1, _s.AddGuides(GuidePlace.TextCapHeight));

        var baseline = _s.Document.Guides[0];
        var cap = _s.Document.Guides[1];
        Assert.Equal(GuideOrientation.Horizontal, baseline.Orientation);
        Assert.Equal(300, baseline.Origin.Y, 9);
        Assert.Equal(GuideOrientation.Horizontal, cap.Orientation);
        Assert.InRange(CapOf(t), 1, 72);
        Assert.Equal(300 - CapOf(t), cap.Origin.Y, 9);
    }

    [Fact]
    public void Cap_height_follows_the_font_size()
    {
        var small = AddText("Hi", 20, Affine2.Translation(0, 100));
        var big = AddText("Hi", 100, Affine2.Translation(0, 500));
        _s.Selection.Set(small); _s.AddGuides(GuidePlace.TextCapHeight);
        _s.Selection.Set(big); _s.AddGuides(GuidePlace.TextCapHeight);
        double capSmall = 100 - _s.Document.Guides[0].Origin.Y, capBig = 500 - _s.Document.Guides[1].Origin.Y;
        Assert.Equal(5, capBig / capSmall, 6);
    }

    [Fact]
    public void Rotated_text_gets_guides_along_its_own_baseline_a_cap_height_apart()
    {
        var t = AddText("Hello", 72, Affine2.Rotation(30 * Math.PI / 180) * Affine2.Translation(400, 300));
        _s.Selection.Set(t);
        _s.AddGuides(GuidePlace.TextBaseline);
        _s.AddGuides(GuidePlace.TextCapHeight);
        var baseline = _s.Document.Guides[0];
        var cap = _s.Document.Guides[1];

        // The text runs 30° clockwise on screen (y down), which is -30° in the layout's counter-clockwise angles: 150°.
        Assert.Equal(GuideOrientation.Angled, baseline.Orientation);
        Assert.Equal(150, baseline.AngleDegrees, 6);
        Assert.Equal(150, cap.AngleDegrees, 6);
        Assert.Equal(0, baseline.DistanceTo(t.GetWorldTransform().Transform(new Vec2(0, 0))), 6);
        Assert.Equal(CapOf(t), baseline.DistanceTo(cap.Origin), 4);
    }

    [Fact]
    public void Text_guides_look_inside_a_selected_group_and_cover_every_text()
    {
        var a = AddText("One", 40, Affine2.Translation(10, 100));
        var b = AddText("Two", 40, Affine2.Translation(10, 200));
        var box = AddRect(0, 0, 50, 50);
        var group = _s.GroupObjects([a, b, box])!;
        _s.Selection.Set(group);
        Assert.True(_s.CanAddGuides(GuidePlace.TextBaseline));
        Assert.Equal(2, _s.AddGuides(GuidePlace.TextBaseline));
        Assert.Equal([100.0, 200.0], _s.Document.Guides.Select(g => g.Origin.Y).OrderBy(y => y).ToArray());
    }

    [Fact]
    public void Two_texts_on_one_baseline_give_one_guide()
    {
        var a = AddText("One", 40, Affine2.Translation(10, 100));
        var b = AddText("Two", 60, Affine2.Translation(300, 100));
        _s.Selection.Set([a, b]);
        Assert.Equal(1, _s.AddGuides(GuidePlace.TextBaseline));
    }

    [Fact]
    public void Text_guides_are_not_available_without_text_in_the_selection()
    {
        _s.Selection.Set(AddRect(100, 100, 200, 50));
        Assert.False(_s.CanAddGuides(GuidePlace.TextBaseline));
        Assert.False(_s.CanAddGuides(GuidePlace.TextCapHeight));
        Assert.Equal(0, _s.AddGuides(GuidePlace.TextBaseline));
        Assert.Empty(_s.Document.Guides);

        _s.Selection.Clear();
        Assert.False(_s.CanAddGuides(GuidePlace.TextCapHeight));
        Assert.True(_s.CanAddGuides(GuidePlace.AllEdges));
    }

    // ---- Layout Properties: page size ----

    [Fact]
    public void Setting_the_page_size_keeps_the_bottom_left_corner_and_every_object_in_place()
    {
        var r = AddRect(100, 700, 80, 60);   // 700..760 in a page 864 tall: 104 pt (1.4444 in) above the bottom
        var before = Units.ToUser(r.GetWorldBounds().BottomRight, _s.Document.Page);

        Assert.True(_s.SetPageSize(Units.FromInches(10), Units.FromInches(8)));
        var page = _s.Document.Page;
        Assert.Equal(720, page.Width, 9);
        Assert.Equal(576, page.Height, 9);
        Assert.Equal(0, page.X, 9);
        Assert.Equal(288, page.Y, 9);                    // the top moved, the bottom (864) did not
        Assert.Equal(864, page.Y + page.Height, 9);
        Assert.Equal(new Rect2(100, 700, 180, 760), r.GetWorldBounds());   // never moved
        var after = Units.ToUser(r.GetWorldBounds().BottomRight, page);
        Assert.Equal(before.X, after.X, 9);              // same inches from the page's bottom-left
        Assert.Equal(before.Y, after.Y, 9);
    }

    [Fact]
    public void The_page_size_change_is_one_undo_step_and_undo_restores_the_old_page()
    {
        var old = _s.Document.Page;
        int steps = _s.History.UndoList.Count;
        Assert.True(_s.SetPageSize(300, 200));
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Layout Properties", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal(old, _s.Document.Page);
        _s.Redo();
        Assert.Equal(300, _s.Document.Page.Width);
    }

    [Fact]
    public void An_unchanged_size_makes_no_undo_step()
    {
        var p = _s.Document.Page;
        Assert.False(_s.SetPageSize(p.Width, p.Height));
        Assert.False(_s.History.CanUndo);
    }

    [Fact]
    public void The_page_size_is_held_between_the_minimum_and_maximum()
    {
        _s.SetPageSize(1, 1);
        Assert.Equal(PageBorder.MinSize, _s.Document.Page.Width);
        Assert.Equal(PageBorder.MinSize, _s.Document.Page.Height);
        _s.SetPageSize(1e9, 1e9);
        Assert.Equal(PageBorder.MaxSize, _s.Document.Page.Width);
        Assert.False(_s.SetPageSize(double.NaN, 5));
    }

    // ---- Size to Fit ----

    [Fact]
    public void Size_to_fit_makes_the_page_the_artwork_s_bounds_and_nothing_moves()
    {
        var a = AddRect(100, 100, 200, 50);
        var b = AddRect(500, 400, 100, 100);
        Assert.True(_s.SizeToFit());
        Assert.Equal(new PageSetup(500, 400, 100, 100), _s.Document.Page);
        Assert.Equal(new Rect2(100, 100, 300, 150), a.GetWorldBounds());
        Assert.Equal(new Rect2(500, 400, 600, 500), b.GetWorldBounds());
    }

    [Fact]
    public void Size_to_fit_counts_the_stroke_width()
    {
        var style = new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.Black, 10));
        AddRect(100, 100, 200, 50, style);
        _s.SizeToFit();
        Assert.Equal(new Rect2(95, 95, 305, 155), _s.Document.Page.Bounds);
    }

    [Fact]
    public void Size_to_fit_includes_the_reach_of_a_soft_shadow()
    {
        var plain = AddRect(100, 100, 200, 50);
        var blur = new BlurStyle(SoftKind.SoftShadow, Opacity: 0.5, Softness: 1, Reference: 50);
        var shadow = AddRect(110, 110, 200, 50, ObjectStyle.FillOnly(DocColor.Black).WithBlur(blur));
        double reach = 3 * blur.Sigma;
        Assert.True(reach > 5);

        _s.SizeToFit();
        var page = _s.Document.Page.Bounds;
        Assert.Equal(110 - reach, page.Left, 6);                 // the shadow object reaches left of the plain rectangle too
        Assert.Equal(110 - reach, page.Top, 6);
        Assert.Equal(310 + reach, page.Right, 6);                // the shadow object's right edge plus its blur
        Assert.Equal(160 + reach, page.Bottom, 6);
        Assert.NotNull(plain);
        Assert.NotNull(shadow);
    }

    [Fact]
    public void Size_to_fit_ignores_guides_hidden_objects_and_hidden_layers()
    {
        AddRect(100, 100, 200, 50);
        var hidden = AddRect(900, 900, 50, 50);
        hidden.Visible = false;
        _s.History.Execute(new AddGuideCommand(Guide.Vertical(5000)));
        _s.SizeToFit();
        Assert.Equal(new Rect2(100, 100, 300, 150), _s.Document.Page.Bounds);
    }

    [Fact]
    public void Size_to_fit_can_grow_the_page_to_reach_objects_on_the_pasteboard()
    {
        AddRect(100, 100, 200, 50);
        AddRect(3000, -500, 100, 100);
        _s.SizeToFit();
        var page = _s.Document.Page.Bounds;
        Assert.Equal(new Rect2(100, -500, 3100, 150), page);
    }

    [Fact]
    public void Size_to_fit_is_one_undo_step_and_undo_restores_the_old_page()
    {
        AddRect(100, 100, 200, 50);
        var old = _s.Document.Page;
        int steps = _s.History.UndoList.Count;
        _s.SizeToFit();
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Size to Fit", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal(old, _s.Document.Page);
        _s.Redo();
        Assert.Equal(new PageSetup(200, 50, 100, 100), _s.Document.Page);
    }

    [Fact]
    public void Size_to_fit_does_nothing_for_an_empty_design_or_a_page_that_already_fits()
    {
        Assert.False(_s.CanSizeToFit);
        Assert.False(_s.SizeToFit());
        Assert.False(_s.History.CanUndo);

        AddRect(0, 0, 1728, 864);   // exactly the page
        Assert.False(_s.SizeToFit());
    }

    [Fact]
    public void Size_to_fit_never_makes_a_page_smaller_than_the_minimum()
    {
        AddRect(100, 100, 2, 2);
        _s.SizeToFit();
        var page = _s.Document.Page;
        Assert.Equal(PageBorder.MinSize, page.Width, 9);
        Assert.Equal(PageBorder.MinSize, page.Height, 9);
        Assert.Equal(101, page.Bounds.Center.X, 9);   // the small thing stays in the middle
    }

    [Fact]
    public void Guides_and_center_on_page_follow_the_fitted_page()
    {
        AddRect(100, 100, 200, 50);
        _s.SizeToFit();
        var g = Guide.Vertical(0);
        _s.History.Execute(new AddGuideCommand(g));
        _s.CenterGuideOnPage(g);
        Assert.Equal(200, g.Origin.X, 9);   // the middle of the fitted page, not of the old one
    }
}
