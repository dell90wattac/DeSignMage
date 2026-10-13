using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The Job color palette (requirement R9): the distinct colors in use anywhere in the document.</summary>
public class JobColorTests
{
    private readonly EditorSession _s = new(NewDoc());
    private Layer L => _s.Document.ActiveLayer;

    private static readonly DocColor Gold = new(242, 183, 5, 255, "Golden Yellow", "Oracal 651");
    private static readonly DocColor Navy = new(10, 20, 80, 255, "Navy", "Oracal 651");

    private RectangleObject Add(double x, ObjectStyle style)
    {
        var r = Rect(x, 0, 20, 20, style);
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private IReadOnlyList<DocColor> Job => _s.JobPalette.Colors;

    [Fact]
    public void An_empty_document_has_no_job_colors()
    {
        Assert.Empty(Job);
    }

    [Fact]
    public void Lists_fills_and_strokes_distinct_in_order_of_first_use()
    {
        Add(0, new ObjectStyle(new SolidPaint(Gold), StrokeStyle.Solid(Navy, 2)));
        Add(30, new ObjectStyle(new SolidPaint(Navy), StrokeStyle.Solid(Gold, 1)));   // same two colors again
        Add(60, ObjectStyle.FillOnly(DocColor.Black));
        Assert.Equal([Gold, Navy, DocColor.Black], Job);
    }

    [Fact]
    public void Colors_keep_their_vinyl_identity_so_the_same_rgb_under_another_name_is_a_separate_entry()
    {
        Add(0, ObjectStyle.FillOnly(Gold));
        Add(30, ObjectStyle.FillOnly(Gold with { Name = "Other Yellow" }));
        Add(60, ObjectStyle.FillOnly(new DocColor(Gold.R, Gold.G, Gold.B)));           // unnamed
        Add(90, ObjectStyle.FillOnly(DocColor.FromCmyk(0, 50, 100, 20, "Mix", "Roland")));
        Assert.Equal(4, Job.Count);
        Assert.Contains(Job, c => c.Model == ColorModel.Cmyk && c.Cmyk == new Cmyk(0, 50, 100, 20));
    }

    [Fact]
    public void Includes_the_pasteboard_hidden_objects_and_hidden_layers()
    {
        Add(-5000, ObjectStyle.FillOnly(Gold));                                       // far off the page
        var hidden = Add(30, ObjectStyle.FillOnly(Navy));
        hidden.Visible = false;
        var layer = new Layer("Back") { Visible = false };
        _s.Document.InsertLayer(0, layer);
        _s.History.Execute(new AddObjectCommand(layer, Rect(0, 0, 10, 10, ObjectStyle.FillOnly(new DocColor(1, 2, 3)))));
        Assert.Equal(3, Job.Count);
    }

    [Fact]
    public void Looks_inside_groups_combined_objects_and_live_text_and_ignores_none_and_images()
    {
        var a = Add(0, ObjectStyle.FillOnly(Gold));
        var b = Add(30, new ObjectStyle(null, StrokeStyle.Solid(Navy, 1)));          // stroke only: fill none
        var group = _s.GroupObjects([a, b])!;
        var text = new TextObject("Hi", "Arial", 20) { Style = ObjectStyle.FillOnly(new DocColor(7, 7, 7)) };
        _s.History.Execute(new AddObjectCommand(L, text));
        var p = Add(100, ObjectStyle.FillOnly(new DocColor(9, 9, 9)));
        var q = Add(130, ObjectStyle.FillOnly(new DocColor(8, 8, 8)));
        _s.Selection.Set([p, q]);
        _s.CombineSelection();
        _s.History.Execute(new AddObjectCommand(L, new ImageObject("none", 10, 10)));

        Assert.Equal([Gold, Navy, new DocColor(7, 7, 7), new DocColor(9, 9, 9), new DocColor(8, 8, 8)], Job);
        Assert.True(_s.Document.Contains(group));
    }

    [Fact]
    public void Updates_live_when_objects_are_added_recolored_and_deleted()
    {
        var a = Add(0, ObjectStyle.FillOnly(Gold));
        Assert.Equal([Gold], Job);

        var b = Add(30, ObjectStyle.FillOnly(Navy));
        Assert.Equal([Gold, Navy], Job);

        _s.Selection.Set(a);
        _s.ApplyFill(new SolidPaint(Navy));                                          // Gold stops being used
        Assert.Equal([Navy], Job);
        _s.Undo();
        Assert.Equal([Gold, Navy], Job);

        _s.Selection.Set(b);
        _s.DeleteSelection();
        Assert.Equal([Gold], Job);
        _s.Undo();
        Assert.Equal([Gold, Navy], Job);
    }

    [Fact]
    public void A_drag_of_a_slider_keeps_the_list_current_with_the_live_color()
    {
        var a = Add(0, ObjectStyle.FillOnly(Gold));
        _s.Selection.Set(a);
        _s.BeginStyleGesture();
        _s.ApplyFill(new SolidPaint(new DocColor(1, 1, 1)));
        Assert.Equal([new DocColor(1, 1, 1)], Job);
        _s.ApplyFill(new SolidPaint(new DocColor(2, 2, 2)));
        Assert.Equal([new DocColor(2, 2, 2)], Job);
        _s.EndStyleGesture();
    }

    [Fact]
    public void The_list_is_cached_until_the_document_changes()
    {
        Add(0, ObjectStyle.FillOnly(Gold));
        var first = Job;
        Assert.Same(first, Job);                                                     // no change: no recompute
        Add(30, ObjectStyle.FillOnly(Navy));
        Assert.NotSame(first, Job);
    }

    [Fact]
    public void Stays_fast_on_a_large_document()
    {
        using (_s.History.BeginTransaction("many"))
            for (int i = 0; i < 20000; i++)
                _s.History.Execute(new AddObjectCommand(L, Rect(i % 500, i / 500, 5, 5, ObjectStyle.FillOnly(new DocColor((byte)(i % 40), 0, 0)))));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var colors = JobColors.Collect(_s.Document);
        Assert.Equal(40, colors.Count);
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }
}
