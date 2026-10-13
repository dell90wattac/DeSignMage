using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

public class PasteTests
{
    private static readonly PageSetup Page = PageSetup.FromInches(24, 12);   // 1728 x 864 pt
    private static readonly Rect2 View = Rect2.FromXYWH(200, 100, 600, 400);

    [Fact]
    public void A_pasted_picture_without_resolution_has_its_screen_size_and_sits_in_the_middle_of_the_view()
    {
        var img = ImagePlacement.CreatePasted(new ImagePixels("a", 192, 96), null, Page, View);
        Assert.Equal((144.0, 72.0), (img.Width, img.Height));
        Assert.Equal(500, img.GetWorldBounds().Center.X, 6);
        Assert.Equal(300, img.GetWorldBounds().Center.Y, 6);
        Assert.Equal("Pasted Picture", img.Name);
        Assert.Null(img.SourceName);
        Assert.Equal((192, 96), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void A_declared_resolution_is_used()
    {
        var img = ImagePlacement.CreatePasted(new ImagePixels("a", 600, 300), 300, Page, View);
        Assert.Equal((144.0, 72.0), (img.Width, img.Height));
    }

    [Fact]
    public void A_big_picture_is_shrunk_to_nine_tenths_of_the_view_keeping_its_aspect()
    {
        var img = ImagePlacement.CreatePasted(new ImagePixels("a", 2400, 800), 96, Page, View);   // 1800 x 600 pt
        Assert.Equal(540, img.Width, 6);                                                         // 0.9 * 600
        Assert.Equal(180, img.Height, 6);
        Assert.Equal(3.0, img.Width / img.Height, 6);
    }

    [Fact]
    public void A_picture_taller_than_the_view_is_limited_by_the_height()
    {
        var img = ImagePlacement.CreatePasted(new ImagePixels("a", 500, 2000), 96, Page, View);
        Assert.Equal(360, img.Height, 6);                                                        // 0.9 * 400
        Assert.Equal(90, img.Width, 6);
    }

    [Fact]
    public void A_picture_is_never_bigger_than_the_page_even_in_a_huge_view()
    {
        var big = Rect2.FromXYWH(-5000, -5000, 20000, 20000);
        var img = ImagePlacement.CreatePasted(new ImagePixels("a", 40000, 20000), 96, Page, big);
        Assert.True(img.Width <= Page.Width + 1e-6 && img.Height <= Page.Height + 1e-6);
        Assert.Equal(Page.Width, img.Width, 6);
    }

    [Fact]
    public void An_empty_view_falls_back_to_the_page_center()
    {
        var img = ImagePlacement.CreatePasted(new ImagePixels("a", 96, 96), null, Page, Rect2.Empty);
        Assert.Equal(Page.Width / 2, img.GetWorldBounds().Center.X, 6);
        Assert.Equal(Page.Height / 2, img.GetWorldBounds().Center.Y, 6);
    }

    [Fact]
    public void Copy_and_Cut_publish_a_new_clipboard_token_and_the_objects()
    {
        var s = new EditorSession(NewDoc());
        var rect = Rect(10, 10, 20, 20);
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, rect));
        s.Selection.Set(rect);
        int raised = 0;
        s.ClipboardChanged += (_, _) => raised++;
        var first = s.ClipboardToken;

        s.Copy();
        var afterCopy = s.ClipboardToken;
        s.Cut();

        Assert.Equal(2, raised);
        Assert.NotEqual(first, afterCopy);
        Assert.NotEqual(afterCopy, s.ClipboardToken);
        Assert.Single(s.ClipboardObjects);
        Assert.NotSame(rect, s.ClipboardObjects[0]);           // a copy, not the object itself
        Assert.False(s.Document.Contains(rect));               // the cut removed it
    }

    [Fact]
    public void Placing_pictures_uses_the_given_undo_name()
    {
        var s = new EditorSession(NewDoc());
        s.PlaceImages([new ImageObject("a", 10, 10, 5, 5)], "Paste Picture");
        Assert.Equal("Paste Picture", s.History.UndoDescription);
    }
}
