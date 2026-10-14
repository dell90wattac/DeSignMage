using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>A layout text that carries a text-on-a-path effect becomes live text on a path (TASKS C.28).</summary>
public class LytTextOnPathTests(ITestOutputHelper output)
{
    [LocalFact("layouts/textonpath.lyt")]
    public void The_effect_record_is_found_with_its_path_and_compensation()
    {
        var file = LytParser.Parse(File.ReadAllBytes(TestFiles.Layout("textonpath.lyt")));
        var text = Assert.Single(file.Objects, o => !o.IsGroup && o.TextOnPath is not null);
        Assert.Equal("Text on a Path", text.Name);
        var effect = text.TextOnPath!;
        Assert.Equal(2, effect.Nodes.Count);
        Assert.Equal(LytNodeKind.Move, effect.Nodes[0].Kind);
        Assert.Equal(LytNodeKind.Bezier, effect.Nodes[1].Kind);
        Assert.Equal(0.76, effect.CurveCompensation, 2);
        Assert.All(effect.Nodes, n => Assert.Equal(0u, n.Flags & LytNode.FlagClose));   // "last node" is not a close
        // The default path the legacy tool makes: the controls 0.3 x the chord above (first, y up in the file) and below (second) the baseline, a gentle S.
        double chord = effect.Nodes[1].X - effect.Nodes[0].X;
        Assert.Equal(0.3 * chord, effect.Nodes[1].C1Y - effect.Nodes[0].Y, 1);       // relative to the path's own height
        Assert.Equal(-0.3 * chord, effect.Nodes[1].Cy - effect.Nodes[0].Y, 1);
        Assert.Equal(0.4 * chord, effect.Nodes[1].C1X - effect.Nodes[0].X, 1);
        Assert.Equal(0.6 * chord, effect.Nodes[1].Cx - effect.Nodes[0].X, 1);
    }

    [LocalFact("layouts/textonpath.lyt")]
    public void The_text_imports_as_live_text_on_its_path_in_the_fallback_font()
    {
        var result = LytImporter.Open(TestFiles.Layout("textonpath.lyt"));
        var text = Assert.Single(result.Document.AllObjects().OfType<TextObject>());
        Assert.Equal("Text on a Path", text.Text);
        Assert.True(text.IsOnPath);
        Assert.Equal("UNV67", text.FontFamily);                       // the legacy sign font's name, kept so it can be resolved later
        Assert.True(text.IsFontMissing);                              // no such installed font: drawn in the generic fallback
        Assert.NotEmpty(text.Layout.Outline.Verbs);
        Assert.Equal(0.76, text.OnPath!.CurveCompensation, 2);
        Assert.Equal(TextPathSpacing.Fit, text.OnPath.Spacing);
        Assert.Contains(result.Warnings, w => w.Contains("UNV67") && w.Contains("not installed"));
        Assert.Equal(1, result.LiveTextCount);
        foreach (var w in result.Warnings) output.WriteLine("warning: " + w);
    }

    [LocalFact("layouts/textonpath.lyt")]
    public void The_live_text_lands_where_the_stored_letters_were_drawn()
    {
        var live = LytImporter.Open(TestFiles.Layout("textonpath.lyt")).Document.AllObjects().OfType<TextObject>().Single();
        var shape = LytImporter.Open(TestFiles.Layout("textonpath.lyt"), new LytImportOptions { LiveText = false }).Document.AllObjects().OfType<PathObject>().Single();

        var stored = shape.GetWorldBounds();
        var flowed = live.GetWorldBounds();
        output.WriteLine($"stored {stored}   live {flowed}");
        // Same start, same end of the path and the same bends: the boxes agree to within what the two fonts' letter widths change
        // (half a letter at each end) and the letter heights.
        double tolerance = 0.2 * Math.Max(stored.Width, stored.Height);
        Assert.InRange(flowed.Left, stored.Left - tolerance, stored.Left + tolerance);
        Assert.InRange(flowed.Right, stored.Right - tolerance, stored.Right + tolerance);
        Assert.InRange(flowed.Top, stored.Top - tolerance, stored.Top + tolerance);
        Assert.InRange(flowed.Bottom, stored.Bottom - tolerance, stored.Bottom + tolerance);
    }

    [LocalFact("layouts/textonpath.lyt")]
    public void The_other_text_files_are_not_mistaken_for_text_on_a_path()
    {
        foreach (var name in new[] { "sample.lyt", "text3.lyt", "effects2.lyt", "stroke.lyt" })
        {
            var path = TestFiles.Layout(name);
            if (!File.Exists(path)) continue;
            var file = LytParser.Parse(File.ReadAllBytes(path));
            Assert.DoesNotContain(file.Objects, o => o.TextOnPath is not null);
        }
    }
}
