using System.Runtime.CompilerServices;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using DeSignMage.Formats.Lyt;
using DeSignMage.Text;
using Xunit.Abstractions;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

/// <summary>The importer needs real glyph outlines (Windows' installed fonts), as the application has at startup.</summary>
internal static class TestFonts
{
    [ModuleInitializer]
    internal static void Install() => SkiaFontService.Install();
}

/// <summary>How closely a live text matches the outlines stored for it.</summary>
internal sealed record TextMatch(LytObject Source, TextObject Text, int Letters, double MaxLetterError, double MeanLetterError, double BoundsError, double CapHeight)
{
    /// <summary>Worst distance of any letter edge, as a fraction of the capital height (errors are in node units, cap height = 10).</summary>
    public double Worst => MaxLetterError / CapHeight;

    /// <summary>Distance of any edge of the whole text's bounds, as a fraction of the capital height.</summary>
    public double BoundsFraction => BoundsError / CapHeight;

    /// <summary>Compares in node space (cap height = 10 units), so rotation and stretch do not blur the result.</summary>
    public static TextMatch Measure(LytObject o, TextObject t, double pageHeightPoints)
    {
        var toNode = t.Transform * LytImporter.PartTransform(o, pageHeightPoints).Invert();
        var groups = new List<List<LytNode>>();
        foreach (var n in o.Nodes)
        {
            if (n.Kind == LytNodeKind.Char) groups.Add([]);
            else if (groups.Count > 0) groups[^1].Add(n);
        }
        var letters = t.Layout.Letters;
        double max = 0, sum = 0;
        int count = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Count == 0 || letters[i].Outline.IsEmpty) continue;
            double e = Edges(LytImporter.BuildPath(groups[i]).GetBounds(), letters[i].Outline.Transform(toNode).GetBounds());
            max = Math.Max(max, e);
            sum += e;
            count++;
        }
        var all = Edges(LytImporter.BuildPath(o.Nodes).GetBounds(), t.Layout.Outline.Transform(toNode).GetBounds());
        return new TextMatch(o, t, count, max, count == 0 ? 0 : sum / count, all, LytTextImporter.CapHeightUnits);
    }
    private static double Edges(Rect2 a, Rect2 b) =>
        Math.Max(Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Right - b.Right)), Math.Max(Math.Abs(a.Top - b.Top), Math.Abs(a.Bottom - b.Bottom)));
}

/// <summary>Layout text becomes live text, placed where its stored outlines were drawn.</summary>
public class LytTextTests(ITestOutputHelper output)
{
    private static IEnumerable<(LytObject Source, TextObject Text)> LiveTexts(LytFile file)
    {
        double pageH = file.PageHeight * Units.PointsPerInch;
        foreach (var o in file.Objects.Where(o => !o.IsGroup && o.IsText))
        {
            var style = ObjectStyle.FillOnly(DocColor.Black);
            if (LytTextImporter.TryCreate(o, pageH, style, out var t) == LytTextKeptAs.None) yield return (o, t!);
        }
    }

    private static string Describe(TextMatch m) =>
        $"'{m.Source.Name}' {m.Text.FontFamily}{(m.Text.IsFontMissing ? " (MISSING -> " + m.Text.ResolvedFontFamily + ")" : "")} " +
        $"size={m.Text.FontSize:0.##}pt align={m.Text.Alignment} tracking={m.Text.Tracking:0.####} letters={m.Letters} " +
        $"letter edges worst={m.Worst * 100:0.00}% mean={m.MeanLetterError / m.CapHeight * 100:0.00}% | whole bounds={m.BoundsFraction * 100:0.00}% (of cap height)";

    // ---------------------------------------------------------------- what becomes live

    [LocalFact("layouts/sample.lyt")]
    public void Sample_text_that_is_still_text_becomes_live_text_and_the_rest_stays_shapes()
    {
        var result = LytImporter.Open(TestFiles.Sample);
        var all = result.Document.AllObjects().ToList();
        var texts = all.OfType<TextObject>().ToList();

        Assert.Equal(result.LiveTextCount, texts.Count);
        Assert.Equal(37, texts.Count);                                    // 27 + 3 + 6 + 1 TrueType texts with markers
        Assert.Equal(["Corbel", "Lato Semibold", "Olivia"], texts.Select(t => t.FontFamily).Distinct().Order());
        Assert.Contains(texts, t => t.FontFamily == "Lato Semibold" && t.Alignment == TextAlignment.Center);

        // Text already converted to vectors ("... - Outlined 0 in.", no character markers) stays a shape, next to the plain copy;
        // so does the only .FNT sign-font text with markers.
        var paths = all.OfType<PathObject>().ToList();
        var names = paths.Select(p => p.Name ?? "").ToHashSet();
        Assert.Contains(names, n => n.EndsWith(" - Outlined 0 in."));
        Assert.Contains(names, n => names.Contains(n + " - Outlined 0 in."));
        Assert.Equal(1, result.TextKeptAsShapes[LytTextKeptAs.SignFont]);
        Assert.Equal(2, result.TextKeptAsShapes[LytTextKeptAs.ConvertedToVectors]);

        Assert.Empty(result.MissingFonts);                                 // Olivia, Lato Semibold and Corbel are installed here
        Assert.DoesNotContain(result.Warnings, w => w.Contains("outline"));   // nothing was lost, so a clean file stays free of dialogs
        foreach (var w in result.Warnings) output.WriteLine("warning: " + w);
    }

    [LocalFact("layouts/sample.lyt")]
    public void Live_text_can_be_switched_off_to_get_shapes_only()
    {
        var result = LytImporter.Open(TestFiles.Sample, new LytImportOptions { LiveText = false });

        Assert.Equal(0, result.LiveTextCount);
        Assert.Empty(result.Document.AllObjects().OfType<TextObject>());
        Assert.Equal(237, result.PartCount);
    }

    [LocalFact("layouts/sample.lyt")]
    public void Imported_text_keeps_its_color_and_its_placement_matches_the_old_shape_import()
    {
        var liveTexts = LytImporter.Open(TestFiles.Sample).Document.AllObjects().OfType<TextObject>().ToList();
        var shapes = LytImporter.Open(TestFiles.Sample, new LytImportOptions { LiveText = false }).Document.AllObjects().OfType<PathObject>().ToList();

        // A live text and the shape the same text imports as (same name, same color, nearest to it) sit in the same place.
        int compared = 0;
        foreach (var live in liveTexts)
        {
            var a = live.GetWorldBounds();
            var twin = shapes.Where(p => p.Name == live.Text && Equals(p.Style.Fill, live.Style.Fill))
                .OrderBy(p => Math.Abs(p.GetWorldBounds().Left - a.Left) + Math.Abs(p.GetWorldBounds().Top - a.Top)).FirstOrDefault();
            if (twin is null) continue;
            var b = twin.GetWorldBounds();
            if (Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top) > 3) continue;
            Assert.InRange(a.Left - b.Left, -0.3, 0.3);
            Assert.InRange(a.Top - b.Top, -0.3, 0.3);
            Assert.InRange(a.Width / b.Width, 0.97, 1.03);
            Assert.InRange(a.Height / b.Height, 0.97, 1.03);
            compared++;
        }
        Assert.True(compared >= 1, "no live text had a matching shape to compare with");
    }

    // ---------------------------------------------------------------- match against the stored outlines

    [LocalTheory("layouts/art-a.lyt", "layouts/sample.lyt")]
    [InlineData("sample")]
    [InlineData("art-a")]
    public void Live_text_lands_on_the_stored_outlines_for_every_installed_font(string which)
    {
        var file = LytParser.Parse(which == "sample" ? TestFiles.Sample : TestFiles.Layout("art-a.lyt"));
        double pageH = file.PageHeight * Units.PointsPerInch;
        var matches = LiveTexts(file).Select(x => TextMatch.Measure(x.Source, x.Text, pageH)).ToList();
        foreach (var m in matches.OrderBy(m => m.Source.Name)) output.WriteLine(Describe(m));

        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.False(m.Text.IsFontMissing, m.Source.Name));
        // Measured: every letter edge within 0.3 % of the capital height, whole texts within 0.05 %. The limits leave room for a different font build.
        Assert.All(matches, m => Assert.True(m.Worst < 0.01, Describe(m)));
        Assert.All(matches, m => Assert.True(m.BoundsFraction < 0.005, Describe(m)));
    }

    [LocalTheory("layouts/art-b.lyt", "layouts/art-c.lyt", "layouts/sample-c.lyt")]
    [InlineData("art-c.lyt")]
    [InlineData("art-b.lyt")]
    [InlineData("sample-c.lyt")]
    public void Texts_whose_font_is_not_installed_stay_live_in_the_fallback_and_keep_the_name(string path)
    {
        var file = LytParser.Parse(TestFiles.Layout(path));
        double pageH = file.PageHeight * Units.PointsPerInch;
        var live = LiveTexts(file).ToList();
        var missing = live.Where(x => x.Text.IsFontMissing).ToList();
        foreach (var m in live.Select(x => TextMatch.Measure(x.Source, x.Text, pageH)).OrderBy(m => m.Text.FontFamily).ThenBy(m => m.Source.Name))
            output.WriteLine(Describe(m));

        foreach (var (src, t) in missing)
        {
            Assert.Equal(LytTextImporter.FamilyOf(src.Style!.FontFile), t.FontFamily);   // the original name is kept
            Assert.Contains(t.ResolvedFontFamily, new[] { "Arial", "Segoe UI" });
        }
        var result = LytImporter.Import(file);
        Assert.Equal(missing.Count, result.MissingFonts.Values.Sum());
        foreach (var font in result.MissingFonts.Keys)
            Assert.Contains(result.Warnings, w => w.Contains($"'{font}' is not installed"));
        output.WriteLine("missing: " + string.Join(", ", result.MissingFonts.Select(kv => $"{kv.Key} x{kv.Value}")));
    }

    // ---------------------------------------------------------------- synthetic records (Arial is always installed)

    private static LytNode Marker(double x) => new(LytNode.FlagMarker, LytNodeKind.Char, (float)x, 0, 0, 10, 0);

    /// <summary>
    /// A text record would store it for Arial: a marker at every letter's ink-left and a box where its ink is, in node units
    /// (cap height 10), with <paramref name="extraGap"/> ems added after the letter at index 2 and <paramref name="tracking"/> after all.
    /// </summary>
    private static LytObject ArialRecord(string text, short align, double scale, double xScale, double rotation, double tracking, double extraGap, string fontFile = "_Arial.ptt")
    {
        var resolved = TextEngine.Fonts.Resolve("Arial", 400, false);
        double em = 10 / LytTextImporter.CapHeightOf(TextEngine.Fonts, resolved);
        var natural = new TextObject(text, "Arial", em).Layout.Letters;

        var nodes = new List<LytNode>();
        var inkLeft = new List<double>();
        double shift = 0;
        for (int i = 0; i < natural.Count; i++)
        {
            if (i > 0) shift += tracking * em + (i - 1 == 2 ? extraGap * em : 0);
            inkLeft.Add(natural[i].Outline.IsEmpty ? natural[i].X + shift : natural[i].Outline.GetBounds().Left + shift);
        }
        double extent = natural[^1].X + shift + natural[^1].Advance - inkLeft[0];
        double offset = align switch { 1 => -extent / 2, 2 => -extent, _ => 0 };

        for (int i = 0; i < natural.Count; i++)
        {
            nodes.Add(Marker(inkLeft[i] - inkLeft[0]));
            if (natural[i].Outline.IsEmpty) continue;
            var b = natural[i].Outline.GetBounds();
            double x0 = b.Left + (inkLeft[i] - natural[i].Outline.GetBounds().Left) + offset - inkLeft[0] + inkLeft[0], x1 = x0 + b.Width;
            // node y is up: the local y of the outline is down, so negate it
            float l = (float)(inkLeft[i] - inkLeft[0] + offset), r = (float)(l + b.Width), lo = (float)-b.Bottom, hi = (float)-b.Top;
            _ = x0; _ = x1;
            nodes.Add(new LytNode(0x2041, LytNodeKind.Move, l, lo, 0, 0, 0));
            nodes.Add(new LytNode(0x2042, LytNodeKind.Line, r, lo, 0, 0, 0));
            nodes.Add(new LytNode(0x2042, LytNodeKind.Line, r, hi, 0, 0, 0));
            nodes.Add(new LytNode(0x2042, LytNodeKind.Line, l, hi, 0, 0, 0));
            nodes.Add(new LytNode(0x2052, LytNodeKind.Line, l, lo, 0, 0, 0));
        }
        return new LytObject
        {
            Name = text, OrgX = 3, OrgY = 4, Scale = scale, XScale = xScale, Rotation = rotation, Italic = rotation, SizeW = extent, SizeH = 10,   // text that is only rotated stores its rotation as the italic field too
            Nodes = [.. nodes], NodeCount = nodes.Count,
            Style = new LytStyle { FontFile = fontFile, Align = align, Color = new LytColor("", "", 1, 200, 30, 20, 0) },
        };
    }

    private static LytFile Page(params LytObject[] objects)
    {
        var f = new LytFile { FormatVersion = 0x4A, VersionName = "7.0", PageWidth = 20, PageHeight = 10, DeclaredObjectCount = objects.Length };
        f.Layers.Add(new LytLayer(0, "Layer 1", default, 0));
        f.Objects.AddRange(objects);
        return f;
    }

    [Theory]
    [InlineData(0, 0.0, 1.0)]
    [InlineData(1, 0.0, 1.0)]
    [InlineData(2, 0.0, 1.0)]
    [InlineData(1, 0.5, 1.5)]       // rotated 0.5 rad and stretched in x
    [InlineData(2, -0.9, 0.8)]
    public void Placement_is_exact_for_every_alignment_rotation_and_stretch(short align, double rotation, double xScale)
    {
        var record = ArialRecord("Sample Text", align, 0.03, xScale, rotation, tracking: 0.05, extraGap: 0);
        var result = LytImporter.Import(Page(record));

        var text = Assert.IsType<TextObject>(Assert.Single(result.Document.AllObjects()));
        var m = TextMatch.Measure(record, text, result.Document.Page.Height);
        output.WriteLine(Describe(m));
        Assert.Equal("Arial", text.FontFamily);
        Assert.Equal((TextAlignment)align, text.Alignment);
        Assert.True(m.Worst < 0.002, Describe(m));
        Assert.True(m.BoundsFraction < 0.002, Describe(m));
    }

    [Fact]
    public void Size_follows_the_capital_height_and_spacing_becomes_tracking_plus_per_gap_kerning()
    {
        // Cap height = 10 node units = 10 x 0.04 in = 28.8 pt; Arial's capital H is 0.716 em, so the em is about 40.2 pt.
        var record = ArialRecord("Gaps", 0, 0.04, 1, 0, tracking: 0.08, extraGap: 0.3);
        var text = Assert.IsType<TextObject>(Assert.Single(LytImporter.Import(Page(record)).Document.AllObjects()));

        Assert.Equal(28.8 / 0.716, text.FontSize, 0);
        Assert.Equal(0.08, text.Tracking, 3);
        Assert.Equal(0.3, text.KerningAt(2), 3);                      // the gap after the third letter ("p")
        Assert.Equal(0, text.KerningAt(0), 3);
        Assert.Equal(1, text.Transform.M11, 9);                      // no scaling in the transform: the size is in the font
        Assert.Null(text.Name);
    }

    [Fact]
    public void Text_that_cannot_be_live_stays_its_outline_and_says_why()
    {
        var sign = ArialRecord("Sign", 0, 0.03, 1, 0, 0, 0, fontFile: "HELVM.FNT");
        var converted = ArialRecord("Gone", 0, 0.03, 1, 0, 0, 0);
        converted = Copy(converted, nodes: converted.Nodes.Where(n => n.Kind != LytNodeKind.Char).ToArray());
        var wrongMarkers = ArialRecord("Mismatch", 0, 0.03, 1, 0, 0, 0);
        wrongMarkers = Copy(wrongMarkers, nodes: [Marker(0), .. wrongMarkers.Nodes.Where(n => n.Kind != LytNodeKind.Char)]);
        var gradient = ArialRecord("Gradient", 0, 0.03, 1, 0, 0, 0);
        gradient = Copy(gradient, style: new LytStyle { FontFile = "_Arial.ptt", Color = gradient.Style!.Color, Fill = new LytFill { Type = 1 } });
        var slanted = ArialRecord("Slanted", 0, 0.03, 1, 0, 0, 0);
        slanted = Copy(slanted, italic: 0.26);

        var result = LytImporter.Import(Page(sign, converted, wrongMarkers, gradient, slanted));
        var objects = result.Document.AllObjects().ToList();

        Assert.Equal(5, objects.Count);
        Assert.All(objects, o => Assert.IsType<PathObject>(o));
        Assert.Equal(0, result.LiveTextCount);
        Assert.Equal(1, result.TextKeptAsShapes[LytTextKeptAs.SignFont]);
        Assert.Equal(1, result.TextKeptAsShapes[LytTextKeptAs.ConvertedToVectors]);
        Assert.Equal(3, result.TextKeptAsShapes[LytTextKeptAs.Unmappable]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_font_that_is_not_installed_stays_live_in_arial_with_its_name_and_the_letters_where_they_were()
    {
        var record = ArialRecord("Sling", 0, 0.03, 1, 0, 0.1, 0, fontFile: "_Worstveld Sling Extra.ptt");
        var result = LytImporter.Import(Page(record));

        var text = Assert.IsType<TextObject>(Assert.Single(result.Document.AllObjects()));
        Assert.True(text.IsFontMissing);
        Assert.Equal("Worstveld Sling Extra", text.FontFamily);
        Assert.Equal("Arial", text.ResolvedFontFamily);
        Assert.Equal(1, result.MissingFonts["Worstveld Sling Extra"]);
        Assert.Contains(result.Warnings, w => w.Contains("'Worstveld Sling Extra' is not installed") && w.Contains("Arial"));
    }

    private static LytObject Copy(LytObject o, LytNode[]? nodes = null, LytStyle? style = null, double? italic = null) => new()
    {
        Name = o.Name, OrgX = o.OrgX, OrgY = o.OrgY, Scale = o.Scale, XScale = o.XScale, Rotation = o.Rotation, SizeW = o.SizeW, SizeH = o.SizeH,
        Italic = italic ?? o.Italic, Nodes = nodes ?? o.Nodes, NodeCount = (nodes ?? o.Nodes).Length, Style = style ?? o.Style,
    };
}
