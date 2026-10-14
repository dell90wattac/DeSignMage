using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Guides from Layout ▸ Guides and a page from Layout Properties / Size to Fit survive saving and loading.</summary>
public class LayoutGuideFileTests
{
    private static Document RoundTrip(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc))).Document;

    [Fact]
    public void Edge_center_and_text_guides_and_the_fitted_page_round_trip()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(24, 12)));
        var rect = new RectangleObject(200, 50) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.Black) };
        var text = new TextObject("Hello", "Arial", 48) { Transform = Affine2.Rotation(20 * Math.PI / 180) * Affine2.Translation(400, 300) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, rect));
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, text));

        s.Selection.Set(rect);
        s.AddGuides(GuidePlace.AllEdges);
        s.AddGuides(GuidePlace.BothCenters);
        s.Selection.Set(text);
        s.AddGuides(GuidePlace.TextBaseline);
        s.AddGuides(GuidePlace.TextCapHeight);
        s.SetGuideLocked(s.Document.Guides[0], true);
        Assert.True(s.SizeToFit());

        var loaded = RoundTrip(s.Document);

        Assert.Equal(s.Document.Page, loaded.Page);
        Assert.Equal(8, loaded.Guides.Count);
        for (int i = 0; i < loaded.Guides.Count; i++)
        {
            var a = s.Document.Guides[i];
            var b = loaded.Guides[i];
            Assert.Equal(a.Origin.X, b.Origin.X, 6);
            Assert.Equal(a.Origin.Y, b.Origin.Y, 6);
            Assert.Equal(a.AngleDegrees, b.AngleDegrees, 6);
            Assert.Equal(a.Locked, b.Locked);
        }
        Assert.Contains(loaded.Guides, g => g.Orientation == GuideOrientation.Angled);   // the rotated text's lines
    }

    [Fact]
    public void A_page_set_by_number_round_trips_with_its_bottom_left_corner()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(24, 12)));
        s.SetPageSize(Units.FromInches(10), Units.FromInches(8));
        var loaded = RoundTrip(s.Document);
        Assert.Equal(new PageSetup(720, 576, 0, 288), loaded.Page);
    }
}
