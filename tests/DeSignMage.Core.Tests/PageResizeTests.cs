using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Dragging the page's border with the select tool (R20): an edge changes one dimension, a corner two; objects stay put.</summary>
public class PageResizeTests
{
    private readonly EditorSession _s;

    // Page 400 x 300 at the origin; the view is 1:1 with the document, so screen == document coordinates.
    public PageResizeTests()
    {
        _s = new EditorSession(new Document(new PageSetup(400, 300)));
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, m);

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private DocObject AddRect(double x, double y, double w, double h)
    {
        var r = Rect(x, y, w, h);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, r));
        return r;
    }

    private PageSetup Page => _s.Document.Page;

    [Fact]
    public void Dragging_the_right_edge_changes_only_the_width()
    {
        Drag(400, 150, 520, 150);
        Assert.Equal(new PageSetup(520, 300), Page);
    }

    [Fact]
    public void Dragging_the_bottom_edge_changes_only_the_height_and_the_top_edge_moves_the_page_origin()
    {
        Drag(200, 300, 200, 360);
        Assert.Equal(new PageSetup(400, 360), Page);

        Drag(200, 0, 200, 40);   // the top edge: the page now starts lower and is shorter, its bottom edge is where it was
        Assert.Equal(new PageSetup(400, 320, 0, 40), Page);
        Assert.Equal(new Rect2(0, 40, 400, 360), Page.Bounds);
    }

    [Fact]
    public void Dragging_the_left_edge_moves_the_page_origin()
    {
        Drag(0, 150, -60, 150);
        Assert.Equal(new PageSetup(460, 300, -60, 0), Page);
    }

    [Fact]
    public void A_corner_changes_two_dimensions()
    {
        Drag(400, 300, 450, 340);   // bottom-right
        Assert.Equal(new PageSetup(450, 340), Page);
        Drag(0, 0, 20, 30);         // top-left
        Assert.Equal(new PageSetup(430, 310, 20, 30), Page);
        Assert.Equal(new Rect2(20, 30, 450, 340), Page.Bounds);
        Drag(450, 30, 470, 10);     // top-right
        Assert.Equal(new Rect2(20, 10, 470, 340), Page.Bounds);
        Drag(20, 340, 10, 360);     // bottom-left
        Assert.Equal(new Rect2(10, 10, 470, 360), Page.Bounds);
    }

    [Fact]
    public void Objects_do_not_move_and_undo_restores_the_old_page_in_one_step()
    {
        var r = AddRect(100, 100, 50, 50);
        var before = r.GetWorldBounds();
        int steps = _s.History.UndoList.Count;

        Drag(0, 0, 30, 20);   // the top-left corner is clear of the rectangle
        Assert.Equal(new PageSetup(370, 280, 30, 20), Page);
        Assert.Equal(before, r.GetWorldBounds());
        Assert.Equal(steps + 1, _s.History.UndoList.Count);   // one command for the whole drag, not one per move
        Assert.Equal("Resize Page", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(new PageSetup(400, 300), Page);
        Assert.Equal(before, r.GetWorldBounds());
        _s.Redo();
        Assert.Equal(new PageSetup(370, 280, 30, 20), Page);
        Assert.True(_s.History.IsModified);
    }

    [Fact]
    public void The_page_preview_follows_the_drag_and_nothing_changes_until_it_ends()
    {
        _s.PointerDown(At(400, 150, PointerButton.Left));
        _s.PointerMove(At(480, 150));
        Assert.Equal(new Rect2(0, 0, 480, 300), _s.PagePreview);
        Assert.Equal(new Rect2(0, 0, 480, 300), _s.PageBounds);
        Assert.Equal(new PageSetup(400, 300), Page);          // the document still has the old page
        Assert.Equal(CursorKind.SizeWE, _s.CurrentCursor);    // the resize cursor stays for the whole drag
        Assert.Contains("RESIZE", _s.Hint);
        _s.PointerUp(At(480, 150, PointerButton.Left));
        Assert.Null(_s.PagePreview);
        Assert.Equal(new PageSetup(480, 300), Page);
    }

    [Fact]
    public void Escape_cancels_the_drag()
    {
        int steps = _s.History.UndoList.Count;
        _s.PointerDown(At(400, 150, PointerButton.Left));
        _s.PointerMove(At(480, 150));
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.Null(_s.PagePreview);
        _s.PointerUp(At(480, 150, PointerButton.Left));
        Assert.Equal(new PageSetup(400, 300), Page);
        Assert.Equal(steps, _s.History.UndoList.Count);
    }

    [Fact]
    public void A_click_on_the_border_changes_nothing_but_deselects()
    {
        var r = AddRect(100, 100, 50, 50);
        _s.Selection.Set(r);
        int steps = _s.History.UndoList.Count;
        _s.PointerDown(At(400, 150, PointerButton.Left));
        _s.PointerUp(At(400, 150, PointerButton.Left));
        Assert.Equal(new PageSetup(400, 300), Page);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.True(_s.Selection.IsEmpty);
    }

    [Fact]
    public void The_width_cannot_be_dragged_below_the_minimum()
    {
        Drag(400, 150, 10, 150);   // right edge dragged far past the left edge
        Assert.Equal(PageBorder.MinSize, Page.Width, 9);
        Assert.Equal(300, Page.Height);
        Drag(PageBorder.MinSize, 150, 500, 150);   // and the right edge can come back out
        Assert.Equal(500, Page.Bounds.Right, 9);
        Drag(0, 150, 900, 150);                    // the left edge is stopped by the minimum too, from the other side
        Assert.Equal(PageBorder.MinSize, Page.Width, 9);
        Assert.Equal(500, Page.Bounds.Right, 9);
    }

    [Fact]
    public void The_height_cannot_be_dragged_below_the_minimum_and_the_far_edge_stays()
    {
        Drag(200, 0, 200, 5000);   // the top edge far below the bottom edge: it stops at the minimum height, the bottom edge stays at 300
        Assert.Equal(PageBorder.MinSize, Page.Height, 9);
        Assert.Equal(300, Page.Bounds.Bottom, 9);
        Assert.Equal(300 - PageBorder.MinSize, Page.Y, 9);
        Drag(200, 300, 200, -900); // the bottom edge far above the top edge
        Assert.Equal(PageBorder.MinSize, Page.Height, 9);
        Assert.True(Page.Width > 0 && Page.Height > 0);
    }

    [Fact]
    public void Dragging_never_makes_the_page_bigger_than_the_maximum()
    {
        Drag(400, 150, 1_000_000, 150);   // far outside the window, but the pointer is captured
        Assert.Equal(PageBorder.MaxSize, Page.Width, 6);
    }

    // ---- cursors and who wins ----

    [Theory]
    [InlineData(400, 150, CursorKind.SizeWE)]      // right edge
    [InlineData(0, 150, CursorKind.SizeWE)]        // left edge
    [InlineData(200, 0, CursorKind.SizeNS)]        // top edge
    [InlineData(200, 300, CursorKind.SizeNS)]      // bottom edge
    [InlineData(0, 0, CursorKind.SizeNWSE)]        // top-left
    [InlineData(400, 300, CursorKind.SizeNWSE)]    // bottom-right
    [InlineData(400, 0, CursorKind.SizeNESW)]      // top-right
    [InlineData(0, 300, CursorKind.SizeNESW)]      // bottom-left
    [InlineData(398, 150, CursorKind.SizeWE)]      // a few pixels off still grabs it
    [InlineData(404, 150, CursorKind.SizeWE)]
    public void Hovering_the_border_shows_the_resize_cursor(double x, double y, CursorKind expected)
    {
        _s.PointerMove(At(x, y));
        Assert.Equal(expected, _s.CurrentCursor);
    }

    [Theory]
    [InlineData(200, 150)]   // inside the page
    [InlineData(420, 150)]   // outside, well clear of the border
    [InlineData(200, 280)]
    public void Away_from_the_border_the_cursor_is_the_arrow(double x, double y)
    {
        _s.PointerMove(At(x, y));
        Assert.Equal(CursorKind.Arrow, _s.CurrentCursor);
    }

    [Fact]
    public void An_object_on_the_border_wins_so_a_drag_there_moves_it_and_the_cursor_says_so()
    {
        var r = AddRect(380, 100, 50, 50);   // straddles the right edge (x = 400)
        _s.PointerMove(At(400, 120));
        Assert.Equal(CursorKind.SelectHit, _s.CurrentCursor);
        Drag(400, 120, 400, 160);
        Assert.Equal(new PageSetup(400, 300), Page);
        Assert.Equal(new Rect2(380, 140, 430, 190), r.GetWorldBounds());
    }

    [Fact]
    public void A_selection_handle_on_the_border_wins()
    {
        var r = AddRect(350, 100, 50, 50);   // right edge of the object = the page's right edge; its right-middle handle is at (400, 125)
        _s.Selection.Set(r);
        _s.PointerMove(At(400, 125));
        Assert.Equal(CursorKind.SizeWE, _s.CurrentCursor);   // the Extend handle, found because handles are checked first
        Drag(400, 125, 440, 125);
        Assert.Equal(new PageSetup(400, 300), Page);
        Assert.Equal(new Rect2(350, 100, 440, 150), r.GetWorldBounds());
    }

    [Fact]
    public void Only_the_select_tool_resizes_the_page()
    {
        _s.SetTool("rectangle");
        _s.PointerMove(At(400, 150));
        Assert.NotEqual(CursorKind.SizeWE, _s.CurrentCursor);
        Drag(400, 150, 520, 150);
        Assert.Equal(new PageSetup(400, 300), Page);
    }

    // ---- the page and the rest of the program ----

    [Fact]
    public void User_coordinates_follow_the_page_origin()
    {
        var page = new PageSetup(Units.FromInches(10), Units.FromInches(6), Units.FromInches(2), Units.FromInches(1));
        // The page's bottom-left is document (2in, 7in): that point is user (0, 0), and the top-left is user (0, 6).
        Assert.Equal((0.0, 0.0), Units.ToUser(new Vec2(Units.FromInches(2), Units.FromInches(7)), page));
        var (x, y) = Units.ToUser(new Vec2(Units.FromInches(2), Units.FromInches(1)), page);
        Assert.Equal(0, x, 9);
        Assert.Equal(6, y, 9);
        var back = Units.FromUser(3.5, 2.25, page);
        var (bx, by) = Units.ToUser(back, page);
        Assert.Equal(3.5, bx, 9);
        Assert.Equal(2.25, by, 9);
    }

    [Fact]
    public void Zoom_layout_and_guides_use_the_resized_page()
    {
        Drag(0, 0, 50, 40);   // top-left corner: the page is now (50,40)-(400,300)
        _s.ZoomToPage();
        var corner = _s.Viewport.ToScreen(new Vec2(50, 40));
        Assert.True(corner.X >= 0 && corner.Y >= 0);
        Assert.Equal(225, Guide.Vertical(10).CenteredOn(Page).X);   // centered on the new page: (50 + 400) / 2
        Assert.Equal(170, Guide.Horizontal(10).CenteredOn(Page).Y); // (40 + 300) / 2
    }

    [Fact]
    public void The_info_line_text_shows_the_page_size_in_inches()
    {
        Assert.Equal("Page: 5.5556 x 4.1667", PageBorder.SizeText(new Rect2(0, 0, 400, 300)));
    }
}
