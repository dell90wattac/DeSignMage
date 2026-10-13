using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Tests;

public class DocumentTests
{
    internal static Document NewDoc() => new(PageSetup.FromInches(24, 12));

    internal static RectangleObject Rect(double x, double y, double w, double h, ObjectStyle? style = null) =>
        new(w, h) { Transform = Affine2.Translation(x, y), Style = style ?? ObjectStyle.FillOnly(DocColor.Black) };

    [Fact]
    public void New_document_has_one_active_layer()
    {
        var doc = NewDoc();
        Assert.Single(doc.Layers);
        Assert.Same(doc.Layers[0], doc.ActiveLayer);
        Assert.Equal(1728, doc.Page.Width);
    }

    [Fact]
    public void Insert_assigns_unique_ids_and_indexes_objects()
    {
        var doc = NewDoc();
        var a = Rect(0, 0, 1, 1);
        var b = Rect(0, 0, 1, 1);
        doc.Insert(doc.ActiveLayer, -1, a);
        doc.Insert(doc.ActiveLayer, -1, b);
        Assert.False(a.Id.IsNone);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Same(a, doc.Find(a.Id));
        Assert.Same(doc.ActiveLayer, a.Parent);
        Assert.Equal(2, doc.ObjectCount);
    }

    [Fact]
    public void Group_children_are_indexed_and_unindexed_with_the_group()
    {
        var doc = NewDoc();
        var g = new GroupObject();
        var child = Rect(0, 0, 1, 1);
        doc.Insert(doc.ActiveLayer, -1, g);
        doc.Insert(g, -1, child);
        Assert.Same(child, doc.Find(child.Id));
        Assert.Same(doc.ActiveLayer, child.Layer);
        Assert.Same(g, child.TopLevel);

        doc.Remove(g);
        Assert.Null(doc.Find(child.Id));
        Assert.False(doc.Contains(child));
        Assert.Equal(0, doc.ObjectCount);
    }

    [Fact]
    public void Remove_reports_original_position()
    {
        var doc = NewDoc();
        var objs = Enumerable.Range(0, 3).Select(_ => Rect(0, 0, 1, 1)).ToArray();
        foreach (var o in objs) doc.Insert(doc.ActiveLayer, -1, o);
        var removal = doc.Remove(objs[1]);
        Assert.Same(doc.ActiveLayer, removal.Slot.Layer);
        Assert.Equal(1, removal.Slot.StackIndex);
        Assert.Null(removal.Slot.Group);
        Assert.Null(objs[1].Parent);
        doc.Restore(removal);
        Assert.Equal(objs, doc.ActiveLayer.Children);
    }

    [Fact]
    public void Reinserted_object_keeps_its_id_and_next_ids_do_not_collide()
    {
        var doc = NewDoc();
        var a = Rect(0, 0, 1, 1);
        doc.Insert(doc.ActiveLayer, -1, a);
        var id = a.Id;
        doc.Remove(a);
        doc.Insert(doc.ActiveLayer, -1, a);
        Assert.Equal(id, a.Id);

        var imported = Rect(0, 0, 1, 1);
        imported.Id = new ObjectId(100);
        doc.Insert(doc.ActiveLayer, -1, imported);
        var next = Rect(0, 0, 1, 1);
        doc.Insert(doc.ActiveLayer, -1, next);
        Assert.Equal(101, next.Id.Value);
    }

    [Fact]
    public void Inserting_an_object_that_already_has_a_parent_throws()
    {
        var doc = NewDoc();
        var a = Rect(0, 0, 1, 1);
        doc.Insert(doc.ActiveLayer, -1, a);
        Assert.Throws<InvalidOperationException>(() => doc.Insert(doc.ActiveLayer, -1, a));
    }

    [Fact]
    public void World_transform_composes_through_groups()
    {
        var doc = NewDoc();
        var g = new GroupObject { Transform = Affine2.Translation(100, 0) };
        var r = Rect(10, 20, 30, 40);
        doc.Insert(doc.ActiveLayer, -1, g);
        doc.Insert(g, -1, r);
        Assert.Equal(new Rect2(110, 20, 140, 60), r.GetWorldBounds());
        Assert.Equal(r.GetWorldBounds(), g.GetWorldBounds());
    }

    [Fact]
    public void Property_changes_bump_version()
    {
        var r = Rect(0, 0, 1, 1);
        int v = r.Version;
        r.Width = 5;
        Assert.True(r.Version > v);
    }

    [Fact]
    public void Content_bounds_skip_hidden_objects_and_layers()
    {
        var doc = NewDoc();
        var visible = Rect(0, 0, 10, 10);
        var hidden = Rect(500, 500, 10, 10);
        hidden.Visible = false;
        doc.Insert(doc.ActiveLayer, -1, visible);
        doc.Insert(doc.ActiveLayer, -1, hidden);
        Assert.Equal(new Rect2(0, 0, 10, 10), doc.GetContentBounds());

        doc.ActiveLayer.Visible = false;
        Assert.True(doc.GetContentBounds().IsEmpty);
    }

    [Fact]
    public void User_coordinates_are_inches_with_y_up_from_page_bottom()
    {
        var page = PageSetup.FromInches(24, 12);
        var (x, y) = Units.ToUser(new Vec2(72, 72), page);
        Assert.Equal(1, x, 9);
        Assert.Equal(11, y, 9);
        Assert.Equal(new Vec2(72, 72), Units.FromUser(1, 11, page));
    }

    [Fact]
    public void Colors_parse_and_format_hex()
    {
        var c = DocColor.FromHex("#F2B705");
        Assert.Equal((byte)0xF2, c.R);
        Assert.Equal("#F2B705", c.ToHex());
        Assert.Equal((byte)0x80, DocColor.FromHex("#80FF0000").A);
        Assert.NotEqual(c, c with { Name = "Golden Yellow" });
    }

    [Fact]
    public void Assets_are_content_addressed()
    {
        var doc = NewDoc();
        var id1 = doc.Assets.Add([1, 2, 3], "image/png");
        var id2 = doc.Assets.Add([1, 2, 3], "image/png");
        Assert.Equal(id1, id2);
        Assert.Single(doc.Assets.All);
    }
}
