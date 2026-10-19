using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Geometry;
using DeSignMage.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// The records of a legacy layout that make an object soft or see-through: soft shadow and glow, feather, flat transparency (alone and over a gradient),
/// Alternate Colors, and the real gradient kinds of the icon library and effect samples (TASKS C.0d, C.27).
/// </summary>
public class LytEffectImportTests(ITestOutputHelper output)
{
    internal static readonly LytImportOptions WithEngines = new() { OffsetService = GeometryEngines.Offsets, BooleanService = GeometryEngines.Booleans };

    // ---------------------------------------------------------------- building made-up layouts

    private static LytNode Move(float x, float y, uint extra = 0) => new(0x1 | extra, LytNodeKind.Move, x, y, 0, 0, 0);
    private static LytNode Line(float x, float y, uint extra = 0) => new(0x2 | extra, LytNodeKind.Line, x, y, 0, 0, 0);

    private static readonly LytColor Blue = new("", "", LytColor.ModelRgb, 0, 0, 255, 0), Red = new("", "", LytColor.ModelRgb, 255, 0, 0, 0);

    /// <summary>A 2 x 1 in rectangle (node units are inches at scale 1) at (1, 1) of a 10 x 10 in page, with whatever the test puts in its style.</summary>
    private static LytObject Rectangle(string name, LytStyle style, int parent = 0, double scale = 1, double rotation = 0) => new()
    {
        Name = name, ParentGroup = parent, Scale = scale, XScale = 1, Rotation = rotation, NodeCount = 5, OrgX = 3, OrgY = 3,
        Nodes = [Move(0, 0), Line(2, 0), Line(2, 1), Line(0, 1), Line(0, 0, LytNode.FlagClose)],
        Style = style,
    };

    private static LytStyle Style(LytSoftShadow? soft = null, LytBlend? blend = null, bool hidden = false, bool glow = false, LytColor? color = null) => new()
    {
        Color = color ?? Blue, SoftShadow = soft, SoftShadowHidden = hidden, SoftShadowIsGlow = glow,
        Fill = blend is null ? null : new LytFill { Version = 6022, Type = blend.GradType, Blend = blend },
    };

    private static LytFile Layout(params LytObject[] objects)
    {
        var f = new LytFile { FormatVersion = 0x4A, VersionName = "7.0", PageWidth = 10, PageHeight = 10, DeclaredObjectCount = objects.Length };
        f.Layers.Add(new LytLayer(0, "Layer 1", default, 0));
        f.Objects.AddRange(objects);
        return f;
    }

    private static LytObject Group(int id) => new() { IsGroup = true, GroupId = id, Name = "Group", Scale = 1, XScale = 1 };

    private static LytSoftShadow Shadow(float opacity = 0.5f, float hs = 0.2f, float vs = -0.3f, float softness = 0.6f, float inset = 0, LytColor? color = null) =>
        new() { Version = 5, Opacity = opacity, Hs = hs, Vs = vs, Softness = softness, Inset = inset, Color = color ?? default };

    private static IEnumerable<PathObject> Effects(Document doc) => doc.AllObjects().OfType<PathObject>().Where(o => o.Style.Blur is not null);

    // ---------------------------------------------------------------- soft shadow and glow

    [Fact]
    public void A_soft_shadow_record_becomes_a_blurred_object_in_a_group_below_its_source_and_stays_linked_to_it()
    {
        var result = LytImporter.Import(Layout(Rectangle("Box", Style(Shadow(color: Red)))), WithEngines);
        var doc = result.Document;

        var effect = Assert.Single(Effects(doc));
        var source = Assert.Single(doc.AllObjects().OfType<PathObject>(), o => o.Style.Blur is null);
        Assert.Equal(1, result.SoftEffectCount);
        Assert.Equal(1, result.SoftEffectGroupCount);                          // the source was not in a group: it is now, with its shadow
        Assert.Equal(2 + 1, doc.ObjectCount);                                  // source, shadow, their group
        var group = Assert.IsType<GroupObject>(source.Parent);
        Assert.Same(group, effect.Parent);
        Assert.True(effect.Layer!.IndexOf(effect) < source.Layer!.IndexOf(source));   // just below the source

        var blur = effect.Style.Blur!;
        Assert.Equal(SoftKind.SoftShadow, blur.Kind);
        Assert.Equal(0.5, blur.Opacity, 3);
        Assert.Equal(0.6, blur.Softness, 3);
        Assert.Equal(0.2 * 72, blur.Hs, 3);                                    // inches of the part's own units, in points
        Assert.Equal(-0.3 * 72, blur.Vs, 3);                                   // the layout's y is up: down is negative
        Assert.Equal(1 * 72, blur.Reference, 3);                               // softness is a fraction of the source's height (1 in)
        Assert.Equal(new DocColor(255, 0, 0), ((SolidPaint)effect.Style.Fill!).Color with { Name = null, Vendor = null });
        Assert.Equal("Soft Shadow", effect.Name);

        // the live link the effect session added: the shadow follows its source
        var link = Assert.IsType<LiveSoft>(effect.Live!.Spec);
        Assert.Equal(SoftKind.SoftShadow, link.Kind);
        Assert.Same(source, effect.Live.Source);

        // and it sits where a shadow shifted right and down by (0.2, 0.3) in would
        var sb = source.GetWorldBounds(); var eb = effect.GetWorldBounds();
        Assert.Equal(sb.Left + 0.2 * 72, eb.Left, 2);
        Assert.Equal(sb.Top + 0.3 * 72, eb.Top, 2);
    }

    [Fact]
    public void A_record_without_offset_is_a_glow_and_its_negative_inset_is_the_thickness()
    {
        var doc = LytImporter.Import(Layout(Rectangle("Box", Style(Shadow(1, 0, 0, 1.35f, inset: -0.5f)))), WithEngines).Document;
        var effect = Assert.Single(Effects(doc));
        Assert.Equal(SoftKind.Glow, effect.Style.Blur!.Kind);
        Assert.Equal(-0.5, effect.Style.Blur.Inset, 3);                        // stored as the legacy layout stores it
        Assert.Equal("Glow", effect.Name);
        Assert.Equal(SoftKind.Glow, ((LiveSoft)effect.Live!.Spec).Kind);
        // the glow's outline grew by 0.04 x 0.5 x height on every side
        var sb = doc.AllObjects().OfType<PathObject>().First(o => o.Style.Blur is null).GetWorldBounds();
        Assert.Equal(sb.Left - 0.04 * 0.5 * 72, effect.GetWorldBounds().Left, 1);

        // an explicit glow mark does it too, even with an offset in the record
        var marked = LytImporter.Import(Layout(Rectangle("Box", Style(Shadow(), glow: true))), WithEngines).Document;
        Assert.Equal(SoftKind.Glow, Assert.Single(Effects(marked)).Style.Blur!.Kind);
    }

    [Fact]
    public void The_offset_goes_through_the_parts_own_scale_and_turn_and_the_inset_through_the_offset_engine()
    {
        // twice the size and turned a quarter counter-clockwise: node x (right) points up the page, 1 node unit = 2 in
        var turned = LytImporter.Import(Layout(Rectangle("Box", Style(Shadow(hs: 1, vs: 0)), scale: 2, rotation: Math.PI / 2)), WithEngines).Document;
        var blur = Assert.Single(Effects(turned)).Style.Blur!;
        Assert.Equal(0, blur.Hs, 3);
        Assert.Equal(2 * 72, blur.Vs, 3);                                      // up the page, y up
        Assert.Equal(4 * 72, blur.Reference, 3);                               // the 2 x 1 rectangle at twice the size, turned: 2 in wide and 4 in high

        // a positive inset pulls the shadow's outline in (here by 0.04 x 0.5 x 1 in on each side) when an offset engine is given, and is skipped without one
        var rec = new LytSoftShadow { Version = 5, Opacity = 0.5f, Hs = 0.1f, Vs = 0, Softness = 0.5f, Inset = 0.5f };
        var with = LytImporter.Import(Layout(Rectangle("Box", Style(rec))), WithEngines).Document;
        var without = LytImporter.Import(Layout(Rectangle("Box", Style(rec)))).Document;
        double narrower = Assert.Single(Effects(without)).GetWorldBounds().Width - Assert.Single(Effects(with)).GetWorldBounds().Width;
        Assert.Equal(2 * 0.04 * 0.5 * 72, narrower, 1);
    }

    [Fact]
    public void A_source_in_a_group_shares_it_with_its_shadow_and_a_hidden_or_arealess_shadow_is_handled()
    {
        var inGroup = LytImporter.Import(Layout(Rectangle("Box", Style(Shadow()), parent: 1), Group(1)), WithEngines);
        var effect = Assert.Single(Effects(inGroup.Document));
        var source = inGroup.Document.AllObjects().OfType<PathObject>().Single(o => o.Style.Blur is null);
        Assert.Same(source.Parent, effect.Parent);
        Assert.Equal(1, inGroup.GroupCount);
        Assert.Equal(0, inGroup.SoftEffectGroupCount);                         // no new group: it joined the one that was there

        var hidden = LytImporter.Import(Layout(Rectangle("Box", Style(Shadow(), hidden: true))), WithEngines).Document;
        Assert.False(Assert.Single(Effects(hidden)).Visible);

        // a line has no area to shade
        var line = new LytObject
        {
            Name = "Rule", Scale = 1, XScale = 1, NodeCount = 2, OrgX = 3, OrgY = 3, Nodes = [Move(0, 0), Line(2, 0)], Style = Style(Shadow()),
        };
        var lineResult = LytImporter.Import(Layout(line), WithEngines);
        Assert.Empty(Effects(lineResult.Document));
        Assert.Contains(lineResult.Warnings, w => w.Contains("no area to shade"));
    }

    // ---------------------------------------------------------------- feather and flat transparency

    private static LytBlend Block(uint grad = 0, uint trans = 0, double min = 0, double max = 1, float feather = 0, float inset = 0.5f) =>
        new() { GradType = grad, TransType = trans, TransMin = (float)min, TransMax = (float)max, FeatherAmount = feather, FeatherInset = inset };

    [Fact]
    public void A_feather_of_the_fill_block_is_the_objects_own_feather()
    {
        var doc = LytImporter.Import(Layout(Rectangle("Box", Style(blend: Block(trans: 1, feather: 0.25f, inset: 0.42f)))), WithEngines).Document;
        var o = Assert.Single(doc.AllObjects());
        Assert.Equal(0.25, o.Style.Feather!.Amount, 4);
        Assert.Equal(0.42, o.Style.Feather.Inset, 4);
        Assert.Null(o.Style.Blur);

        // none when the block has none, and an older block (no inset stored) takes the 0.5 the legacy program used then
        Assert.Null(Assert.Single(LytImporter.Import(Layout(Rectangle("Box", Style(blend: Block(trans: 1)))), WithEngines).Document.AllObjects()).Style.Feather);
        Assert.Equal(0.5, new LytBlend().FeatherInset);
    }

    [Fact]
    public void A_flat_transparency_makes_the_color_or_every_gradient_color_see_through()
    {
        // opacity is 1 - Min
        var solid = LytImporter.Import(Layout(Rectangle("Box", Style(blend: Block(trans: 1, min: 0.38)))), WithEngines).Document;
        var fill = Assert.IsType<SolidPaint>(Assert.Single(solid.AllObjects()).Style.Fill);
        Assert.Equal((byte)Math.Round(255 * 0.62), fill.Color.A);
        Assert.Equal((0, 0, 255), (fill.Color.R, fill.Color.G, fill.Color.B));

        // over a color gradient
        var b = Block(grad: 1, trans: 1, min: 0.5);
        b.NodeCount = 2;
        float[] pts = [0, 0, 2, 0];
        for (int i = 0; i < 4; i++) b.Points[i] = pts[i];
        b.Colors[0] = Red; b.Colors[1] = Blue;
        var gradient = Assert.IsType<GradientPaint>(Assert.Single(LytImporter.Import(Layout(Rectangle("Box", Style(blend: b))), WithEngines).Document.AllObjects()).Style.Fill);
        Assert.All(gradient.Stops, s => Assert.Equal(128, s.Color.A));

        // no transparency (or Min 0): nothing changes
        var plain = LytImporter.Import(Layout(Rectangle("Box", Style(blend: Block(trans: 1, min: 0)))), WithEngines).Document;
        Assert.Equal(255, ((SolidPaint)Assert.Single(plain.AllObjects()).Style.Fill!).Color.A);
    }

    [Fact]
    public void A_picture_set_to_be_see_through_is_reported_because_a_picture_has_no_opacity_yet()
    {
        using var bmp = new SKBitmap(2, 2);
        using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        var picture = new LytObject
        {
            Name = "Photo", Scale = 1, XScale = 1, NodeCount = 5, OrgX = 3, OrgY = 3,
            Nodes = [Move(0, 0), Line(2, 0), Line(2, 1), Line(0, 1), Line(0, 0, LytNode.FlagClose)],
            Style = new LytStyle
            {
                Fill = new LytFill { Version = 6022, Type = 4, Image = png.ToArray(), ImageMediaType = "image/png", Blend = Block(grad: 4, trans: 1, min: 0.4, feather: 0.04f) },
            },
        };
        var result = LytImporter.Import(Layout(picture), WithEngines);
        var image = Assert.IsType<ImageObject>(Assert.Single(result.Document.AllObjects()));
        Assert.Equal(0.04, image.Style.Feather!.Amount, 4);                    // the feather is kept on the picture
        Assert.Contains(result.Warnings, w => w.Contains("picture") && w.Contains("see-through"));
    }

    // ---------------------------------------------------------------- gradients

    private static LytBlend Gradient(uint kind, uint trans, params (float X, float Y, LytColor Color)[] nodes)
    {
        var b = new LytBlend { GradType = kind, TransType = trans, NodeCount = nodes.Length };
        for (int i = 0; i < nodes.Length; i++)
        {
            if (i < 4) { b.Points[2 * i] = nodes[i].X; b.Points[2 * i + 1] = nodes[i].Y; b.Colors[i] = nodes[i].Color; }
            else b.ExtraNodes.Add(new LytBlendNode(nodes[i].X, nodes[i].Y, nodes[i].Color, 0));
        }
        return b;
    }

    [Fact]
    public void Alternate_colors_of_the_fill_block_make_the_gradient_go_the_long_way_round()
    {
        var b = Gradient(1, 0, (0, 0.5f, Red), (2, 0.5f, Blue));
        var normal = (GradientPaint)LytImporter.Import(Layout(Rectangle("Box", Style(blend: b))), WithEngines).Document.AllObjects().Single().Style.Fill!;
        Assert.False(normal.AlternateColors);

        b.AlternateColors = true;
        var alt = (GradientPaint)LytImporter.Import(Layout(Rectangle("Box", Style(blend: b))), WithEngines).Document.AllObjects().Single().Style.Fill!;
        Assert.True(alt.AlternateColors);
        var (r, g, bl) = Middle(alt);
        Assert.True(g > 200 && r < 60 && bl < 60, $"the long way from red to blue passes through green: {(r, g, bl)}");
    }

    private static (byte R, byte G, byte B) Middle(GradientPaint g)
    {
        var ramp = GradientRamp.Build(g);
        var e = ramp.OrderBy(x => Math.Abs(x.T - 0.5)).First();
        return (e.R, e.G, e.B);
    }

    [Fact]
    public void A_transparency_ramp_over_a_gradient_joins_its_colors_when_both_run_along_the_same_line_and_is_dropped_otherwise()
    {
        // the colors and the fade both run left to right over the rectangle, the fade from clear to fully opaque
        var b = Gradient(1, 2, (0, 0.5f, Red), (2, 0.5f, Blue));
        float[] fade = [0, 0.5f, 2, 0.5f];
        for (int i = 0; i < 4; i++) b.TransPoints[i] = fade[i];
        var joined = (GradientPaint)LytImporter.Import(Layout(Rectangle("Box", Style(blend: b))), WithEngines).Document.AllObjects().Single().Style.Fill!;
        Assert.Equal([0, 255], joined.Stops.Select(s => (int)s.Color.A));
        Assert.Equal((255, 0, 0), (joined.Stops[0].Color.R, joined.Stops[0].Color.G, joined.Stops[0].Color.B));

        // a fade along another line cannot be put into the stops: the colors are kept and the user is told
        float[] other = [1, 0, 1, 1];
        for (int i = 0; i < 4; i++) b.TransPoints[i] = other[i];
        var result = LytImporter.Import(Layout(Rectangle("Box", Style(blend: b))), WithEngines);
        var kept = (GradientPaint)result.Document.AllObjects().Single().Style.Fill!;
        Assert.All(kept.Stops, s => Assert.Equal(255, s.Color.A));
        Assert.Contains(result.Warnings, w => w.Contains("transparency ramp"));

        // the block of an object that has no fade at all: nothing is changed
        var none = Gradient(1, 0, (0, 0.5f, Red), (2, 0.5f, Blue));
        Assert.All(((GradientPaint)LytImporter.Import(Layout(Rectangle("Box", Style(blend: none))), WithEngines).Document.AllObjects().Single().Style.Fill!).Stops, s => Assert.Equal(255, s.Color.A));
    }

    [Fact]
    public void A_flat_blend_shows_its_own_first_color_and_an_ellipse_keeps_its_axes()
    {
        var flat = Gradient(7, 0, (0, 0, Red), (2, 0, Red));
        var fill = Assert.IsType<SolidPaint>(LytImporter.Import(Layout(Rectangle("Box", Style(blend: flat))), WithEngines).Document.AllObjects().Single().Style.Fill);
        Assert.Equal((255, 0, 0), (fill.Color.R, fill.Color.G, fill.Color.B));       // not the object's own blue

        // node 0 the center, node 1 the end of the first axis, node 2 the end of the second (the legacy program draws it just so)
        var e = Gradient(3, 0, (1, 0.5f, Red), (2, 0.5f, Blue), (1, 1, Blue));
        var ellipse = (GradientPaint)LytImporter.Import(Layout(Rectangle("Box", Style(blend: e))), WithEngines).Document.AllObjects().Single().Style.Fill!;
        Assert.Equal(GradientKind.Ellipse, ellipse.Kind);
        Assert.Equal([new Vec2(0.5, 0.5), new Vec2(1, 0.5), new Vec2(0.5, 0)], ellipse.Points);
    }

    [Fact]
    public void Palette_colors_of_the_icon_library_are_hue_saturation_value_in_hundredths()
    {
        // "174,249,255": 184.44 degrees, 31.76 %, 100 %
        var c = new LytColor("174,249,255", "", LytColor.ModelHsv, 18444, 3176, 10000, 0);
        Assert.True(c.IsValid);
        var d = c.ToDocColor();
        Assert.InRange(d.R, 173, 175);
        Assert.InRange(d.G, 248, 250);
        Assert.Equal(255, d.B);
        // a named one keeps its name
        var named = new LytColor("Red", "Major Signs Vinyl", LytColor.ModelHsv, 35600, 9700, 8500, 0).ToDocColor();
        Assert.Equal("Major Signs Vinyl Red", named.Name);
    }

    // ---------------------------------------------------------------- reading the records (bytes)

    private static byte[] AfterFill(uint f69, uint f6d, uint f71, Action<ByteWriter>? blocks = null, uint? f79 = 0x80040020)
    {
        var w = new ByteWriter();
        w.U32(f69); w.U32(f6d); w.U32(f71);
        blocks?.Invoke(w);
        if (f79 is { } f) { w.U32(f); w.U32(0); }
        return w.ToArray();
    }

    private static void SoftBlock(ByteWriter w, byte version, float opacity, float hs, float vs, float softness, float inset, float slant = 0, float height = 1, uint options = 1)
    {
        w.U8(version);
        w.F32(opacity); w.F32(hs); w.F32(vs); w.F32(softness);
        w.U32(0xFFFFFF00); w.U32(0x12FA58FF);                // two words of leftover memory
        w.U32(options); w.F32(inset);
        if (version > 3) { w.F32(slant); w.F32(height); }
        if (version > 4) w.Color(new LytColor("0,0,0", "", LytColor.ModelRgb, 0, 0, 0, 0));
    }

    private static LytSoftShadow? Read(byte[] bytes, out bool hidden, out bool glow, bool v7 = true)
    {
        var r = new ByteReader(bytes, 0, bytes.Length);
        LytParser.ReadAfterFill(r, v7, out var soft, out hidden, out glow);
        return soft;
    }

    [Fact]
    public void The_soft_shadow_record_is_read_in_all_three_versions_and_its_unused_words_are_ignored()
    {
        var v5 = Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 5, 0.44f, -0.1146f, -0.1064f, 0.22f, 0.5f, slant: 10, height: 1.5f)), out _, out _)!;
        Assert.Equal(5, v5.Version);
        Assert.Equal((0.44f, -0.1146f, -0.1064f, 0.22f, 0.5f, 10f, 1.5f), (v5.Opacity, v5.Hs, v5.Vs, v5.Softness, v5.Inset, v5.Slant, v5.Height));
        Assert.True(v5.Color.IsValid);

        var v4 = Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 4, 1, 0.3f, -0.4f, 0.25f, 0)), out _, out _)!;
        Assert.Equal((1f, 1f), (v4.Opacity, v4.Height));
        Assert.False(v4.Color.IsValid);                                       // no color stored: black

        var v3 = Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 3, 0.85f, 0.83f, -1.21f, 0.7f, 0)), out _, out _)!;
        Assert.Equal(0f, v3.Slant);
        Assert.Equal(1f, v3.Height);                                           // the older record has neither: no slant, the same height
    }

    [Fact]
    public void Blocks_before_the_soft_shadow_are_skipped_and_the_glow_and_hidden_marks_come_from_the_last_flag_word()
    {
        // 0x1 announces 116 bytes before the shadow
        var soft = Read(AfterFill(0, 0x4000001, 0, w => { w.Zeros(116); SoftBlock(w, 5, 1, 0, 0, 1.35f, 0); }, f79: 0x400002), out bool hidden, out bool glow);
        Assert.NotNull(soft);
        Assert.True(hidden);
        Assert.True(glow);

        var plain = Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 5, 1, 0, 0, 1, 0)), out hidden, out glow);
        Assert.NotNull(plain);
        Assert.False(hidden || glow);

        // an older (6.x) record has no such word
        Assert.NotNull(Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 5, 1, 0, 0, 1, 0), f79: null), out hidden, out glow, v7: false));
        Assert.False(hidden || glow);

        // the blocks of the last flag word are skipped by their sizes: 260 + 4 + 8
        var more = Read(AfterFill(0, 0, 0x200 | 0x800 | 0x200000, w => w.Zeros(260 + 4 + 8), f79: 0x400000), out hidden, out _);
        Assert.Null(more);
        Assert.True(hidden);
    }

    [Fact]
    public void A_block_that_is_not_understood_or_not_plausible_is_ignored_without_failing()
    {
        // an unknown block ahead of the shadow: its place cannot be found
        Assert.Null(Read(AfterFill(0x40000000, 0x4000000, 0, w => SoftBlock(w, 5, 0.5f, 1, 1, 0.5f, 0)), out _, out _));
        Assert.Null(Read(AfterFill(0, 0x4000000 | 0x800000, 0, w => SoftBlock(w, 5, 0.5f, 1, 1, 0.5f, 0)), out _, out _));
        // opacity above 1, a NaN, a shadow that is switched off, an unknown version: not a record
        Assert.Null(Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 5, 7f, 1, 1, 0.5f, 0)), out _, out _));
        Assert.Null(Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 5, 0.5f, float.NaN, 1, 0.5f, 0)), out _, out _));
        Assert.Null(Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 5, 0.5f, 1, 1, 0.5f, 0, options: 0)), out _, out _));
        Assert.Null(Read(AfterFill(0, 0x4000000, 0, w => SoftBlock(w, 9, 0.5f, 1, 1, 0.5f, 0)), out _, out _));
        // too short to hold what it announces: the reader says so, the style reader turns that into "no soft shadow"
        Assert.Throws<LytFormatException>(() => Read(AfterFill(0, 0x4000000, 0, w => w.U8(5), f79: null), out _, out _));
    }

    // ---------------------------------------------------------------- written and read back

    [Fact]
    public void Alternate_colors_feather_and_opacity_survive_being_written_to_a_layout_and_read_back()
    {
        var doc = new Document(PageSetup.FromInches(10, 10));
        var layer = doc.Layers[0];
        var alt = new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear),
            [new GradientNode(0, new DocColor(255, 0, 0)), new GradientNode(1, new DocColor(0, 0, 255))], alternateColors: true);
        doc.Insert(layer, -1, new RectangleObject(100, 60) { Name = "alt", Transform = Affine2.Translation(30, 30), Style = new ObjectStyle(alt, null) });
        doc.Insert(layer, -1, new RectangleObject(100, 60) { Name = "feather", Transform = Affine2.Translation(30, 130), Style = new ObjectStyle(new SolidPaint(new DocColor(0, 120, 0)), null, null, new FeatherStyle(0.25, 0.42)) });
        doc.Insert(layer, -1, new RectangleObject(100, 60) { Name = "see-through", Transform = Affine2.Translation(30, 230), Style = new ObjectStyle(new SolidPaint(new DocColor(0, 0, 200, 158)), null) });
        var halfGradient = new GradientPaint(GradientKind.Linear, GradientPaint.DefaultPoints(GradientKind.Linear),
            [new GradientNode(0, new DocColor(255, 0, 0, 128)), new GradientNode(1, new DocColor(0, 0, 255, 128))]);
        doc.Insert(layer, -1, new RectangleObject(100, 60) { Name = "half", Transform = Affine2.Translation(30, 330), Style = new ObjectStyle(halfGradient, null, null, new FeatherStyle(0.1, 0.5)) });

        var bytes = LytWriter.ToBytes(doc, new LytWriteOptions { Title = "effects" }, out var written);
        Assert.DoesNotContain(written.Warnings, w => w.Contains("Feathered") || w.Contains("Alternate"));
        var parsed = LytParser.Parse(bytes);
        Assert.Empty(parsed.Warnings);
        var blends = parsed.Objects.Where(o => !o.IsGroup).Select(o => o.Style!.Fill!.Blend!).ToList();
        Assert.True(blends[0].AlternateColors);
        Assert.Equal((0.25f, 0.42f), (blends[1].FeatherAmount, blends[1].FeatherInset));
        Assert.Equal(1u, blends[2].TransType);
        Assert.Equal(1 - 158 / 255.0, blends[2].TransMin, 3);

        var back = LytImporter.Import(parsed, WithEngines).Document;
        var objs = back.AllObjects().ToList();
        Assert.True(((GradientPaint)objs[0].Style.Fill!).AlternateColors);
        Assert.Equal(new FeatherStyle(0.25f, 0.42f), objs[1].Style.Feather, new FeatherComparer());
        Assert.Equal(158, ((SolidPaint)objs[2].Style.Fill!).Color.A);
        var half = (GradientPaint)objs[3].Style.Fill!;
        Assert.All(half.Stops, s => Assert.InRange((int)s.Color.A, 127, 129));   // one opacity for all the colors comes back as it went
        Assert.Equal(0.1, objs[3].Style.Feather!.Amount, 4);
    }

    private sealed class FeatherComparer : IEqualityComparer<FeatherStyle?>
    {
        public bool Equals(FeatherStyle? x, FeatherStyle? y) => x is not null && y is not null && Math.Abs(x.Amount - y.Amount) < 1e-4 && Math.Abs(x.Inset - y.Inset) < 1e-4;
        public int GetHashCode(FeatherStyle? o) => 0;
    }

    // ---------------------------------------------------------------- the real files

    [LocalFact("layouts/sample-c.lyt")]
    public void The_icon_library_comes_in_with_its_soft_shadows_glows_feathers_and_gradients()
    {
        var r = LytImporter.Open(TestFiles.Layout("sample-c.lyt"), WithEngines);
        var doc = r.Document;
        output.WriteLine($"parts {r.PartCount}, groups {r.GroupCount}, soft effects {r.SoftEffectCount} (+{r.SoftEffectGroupCount} groups)");
        foreach (var w in r.Warnings) output.WriteLine("  warning: " + w);

        // three records: two soft shadows (one on a thin bar) and the glow of the Glow icon
        Assert.Equal(3, r.SoftEffectCount);
        var effects = Effects(doc).ToList();
        var glow = Assert.Single(effects, e => e.Style.Blur!.Kind == SoftKind.Glow);
        Assert.Equal(1.35, glow.Style.Blur!.Softness, 3);
        Assert.Equal(1, glow.Style.Blur.Opacity, 3);
        Assert.Equal(new DocColor(255, 255, 255), ((SolidPaint)glow.Style.Fill!).Color with { Name = null, Vendor = null });
        Assert.Equal(2, effects.Count(e => e.Style.Blur!.Kind == SoftKind.SoftShadow));
        var drop = effects.Single(e => e.Style.Blur!.Kind == SoftKind.SoftShadow && e.Style.Blur.Inset < -1);   // the S of the Soft Shadow icon: grown by 1.05 x 4 %
        Assert.Equal(0.55, drop.Style.Blur!.Softness, 3);
        Assert.True(drop.Style.Blur.Hs > 0 && drop.Style.Blur.Vs < 0);        // right and down
        Assert.All(effects, e =>
        {
            Assert.NotNull(e.Parent);
            Assert.IsType<LiveSoft>(e.Live!.Spec);
            Assert.NotNull(e.Live.Source.Layer);
        });

        // feathers: the Glow icon's white square, the red F of the Feather icon, the see-through bevel crescents
        var feathered = doc.AllObjects().Where(o => o.Style.Feather is not null).ToList();
        Assert.InRange(feathered.Count, 20, 40);
        Assert.Contains(feathered, o => Math.Abs(o.Style.Feather!.Amount - 0.38) < 1e-3 && Math.Abs(o.Style.Feather.Inset - 0.42) < 1e-3);
        var f = doc.AllObjects().Single(o => o.Name == "Effects  >  Feather");
        Assert.Equal(0.25, f.Style.Feather!.Amount, 3);
        var bevel = doc.AllObjects().Where(o => o.Name == "Effects  >  Easy Bevel" && o.Style.Fill is SolidPaint { Color.A: < 255 }).ToList();
        Assert.NotEmpty(bevel);
        Assert.Contains(bevel, o => ((SolidPaint)o.Style.Fill!).Color.A == 158);   // Min 38 %: 62 % opaque

        // the real gradient kinds: vertical (the printer icon's rainbow and the Gradient Blend icon), circle (the dropper's bulb)
        var rip = doc.AllObjects().Where(o => o.Name == "File  >  Send to RIP" && o.Style.Fill is GradientPaint).Select(o => (GradientPaint)o.Style.Fill!).Single();
        Assert.Equal(GradientKind.Vertical, rip.Kind);
        Assert.Equal(4, rip.Stops.Count);
        Assert.Equal([0, 0.2, 0.45, 1], rip.Stops.Select(s => Math.Round(s.Offset, 2)));
        Assert.Equal((217, 7, 21), (rip.Stops[0].Color.R, rip.Stops[0].Color.G, rip.Stops[0].Color.B));   // the palette color Red, from its hue/saturation/value
        Assert.Contains(doc.AllObjects(), o => o.Style.Fill is GradientPaint { Kind: GradientKind.Circle, Stops.Count: 2 });
        Assert.Contains(doc.AllObjects(), o => o.Style.Fill is GradientPaint { Kind: GradientKind.Vertical, Stops.Count: 2 });
        Assert.InRange(doc.AllObjects().Count(o => o.Style.Fill is GradientPaint { Kind: GradientKind.Linear }), 60, 80);   // the layout holds 69 linear blends
    }

    [LocalFact("layouts/art-b.lyt")]
    public void The_flyer_layouts_three_shadows_come_in_beside_their_rectangles()
    {
        var r = LytImporter.Open(TestFiles.Layout("art-b.lyt"), WithEngines);
        Assert.Equal(3, r.SoftEffectCount);
        var effects = Effects(r.Document).ToList();
        Assert.All(effects, e =>
        {
            Assert.Equal(SoftKind.SoftShadow, e.Style.Blur!.Kind);
            Assert.True(e.Style.Blur.Hs < 0 && e.Style.Blur.Vs < 0);           // left and down, a few points
            Assert.InRange(Math.Abs(e.Style.Blur.Hs), 2, 12);
            Assert.Equal(new DocColor(0, 0, 0), ((SolidPaint)e.Style.Fill!).Color with { Name = null, Vendor = null });
        });
        Assert.Contains(effects, e => Math.Abs(e.Style.Blur!.Opacity - 0.44) < 1e-3 && Math.Abs(e.Style.Blur.Softness - 0.22) < 1e-3);
        Assert.Contains(effects, e => Math.Abs(e.Style.Blur!.Opacity - 0.33) < 1e-3 && Math.Abs(e.Style.Blur.Softness - 0.33) < 1e-3);
    }

    [LocalFact("layouts/image2.lyt")]
    public void A_text_shadow_and_the_feathered_pictures_of_the_picture_sample_come_in()
    {
        var r = LytImporter.Open(TestFiles.Layout("image2.lyt"), WithEngines);
        var doc = r.Document;
        var effect = Effects(doc).First(e => Math.Abs(e.Style.Blur!.Softness - 0.7) < 1e-3);   // the 85 % shadow of the text
        Assert.Equal(0.85, effect.Style.Blur!.Opacity, 3);
        Assert.True(effect.Style.Blur.Hs > 0 && effect.Style.Blur.Vs < 0);
        Assert.Equal(2, Effects(doc).Count());

        var feathers = doc.AllObjects().OfType<ImageObject>().Select(i => i.Style.Feather).Where(f => f is not null).ToList();
        Assert.Contains(feathers, f => Math.Abs(f!.Amount - 0.04) < 1e-3 && Math.Abs(f.Inset - 0.5) < 1e-3);   // the circuit board
        Assert.Contains(feathers, f => Math.Abs(f!.Amount - 0.15) < 1e-3);                                       // the flag
    }

    [LocalFact("layouts/art-b.lyt")]
    public void The_flyer_layout_renders_with_the_soft_shadows_beside_its_boxes()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-b.lyt"), WithEngines).Document;
        var shadow = Effects(doc).First();
        var source = (DocObject)shadow.Live!.Source;
        var area = source.GetWorldBounds().Inflate(24);
        using var bmp = LytRenderTests.Render(doc, area, 4);
        LytRenderTests.Save(bmp, "art-b-soft-shadow.png");
        if (Environment.GetEnvironmentVariable("DSM_SHOTS") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "effect-import-art-b-shadow.png"), SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray());

        // the shadow is to the lower left of its box (a few points) and darkens what lies under it: some pixel outside the box on that side is no longer the page's white
        var sb = source.GetWorldBounds();
        bool dark = false;
        for (double y = sb.Bottom; y < sb.Bottom + 6 && !dark; y += 0.5)
            for (double x = sb.Left - 8; x < sb.Left + 20 && !dark; x += 0.5)
            {
                int px = (int)((x - area.Left) * 4), py = (int)((y - area.Top) * 4);
                if (px >= 0 && py >= 0 && px < bmp.Width && py < bmp.Height && bmp.GetPixel(px, py) is { Red: < 230 } p && p.Red == p.Green) dark = true;
            }
        Assert.True(dark, "expected a soft shadow beside the box");
    }

    [LocalFact("layouts/effects2.lyt")]
    public void The_effect_sample_with_a_gradient_in_palette_colors_gets_its_colors()
    {
        var r = LytImporter.Open(TestFiles.Layout("effects2.lyt"), WithEngines);
        var gradient = Assert.Single(r.Document.AllObjects(), o => o.Style.Fill is GradientPaint);
        var g = (GradientPaint)gradient.Style.Fill!;
        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.NotEqual(g.Stops[0].Color, g.Stops[1].Color);
        foreach (var w in r.Warnings) output.WriteLine("warning: " + w);
    }

    /// <summary>
    /// Pictures of the icon library next to the legacy program's own 30 x 30 icons for the same commands (the soft shadow, the glow with its feather, the
    /// vertical gradient, the printer's 4-color vertical gradient): the new render on the left of each pair. The legacy icons are small and drawn by its own
    /// renderer, so they are blurrier and the glow's feathered square fades more gently than here (see notes/effects/feather.md section 8).
    /// </summary>
    [LocalFact("layouts/sample-c.lyt")]
    public void The_icons_look_like_the_legacy_programs_icons_of_the_same_commands()
    {
        var doc = LytImporter.Open(TestFiles.Layout("sample-c.lyt"), WithEngines).Document;
        string refDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "notes", "ui-assets", "hc-icons"));
        if (!Directory.Exists(refDir)) { output.WriteLine("no reference icons here; skipped"); return; }
        var cases = new (string Name, int Id)[]
        {
            ("Effects  >  Soft Shadow", 23546), ("Effects  >  Glow", 25294), ("Effects  >  Gradient Blend", 23025), ("File  >  Send to RIP", 24052),
        };
        const int size = 200, gap = 12;
        using var sheet = new SKBitmap(2 * size + 3 * gap, cases.Length * (size + gap) + gap);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(200, 200, 200));
        for (int i = 0; i < cases.Length; i++)
        {
            var (name, id) = cases[i];
            var member = doc.AllObjects().First(o => o.Name == name);
            DocObject top = member;
            while (top.Parent is DocObject p) top = p;
            var area = top.GetWorldBounds();
            double side = Math.Max(area.Width, area.Height) * 1.05;
            var square = Rect2.FromXYWH(area.Left + area.Width / 2 - side / 2, area.Top + area.Height / 2 - side / 2, side, side);
            using var mine = LytRenderTests.Render(doc, square, size / side);
            canvas.DrawBitmap(mine, gap, gap + i * (size + gap));

            using var src = SKBitmap.Decode(Path.Combine(refDir, id + ".png"));
            using var up = new SKBitmap(size, size);
            canvas.DrawRect(SKRect.Create(2 * gap + size, gap + i * (size + gap), size, size), new SKPaint { Color = SKColors.White });
            src.ScalePixels(up, SKSamplingOptions.Default);
            canvas.DrawBitmap(up, 2 * gap + size, gap + i * (size + gap));

            if (name.EndsWith("Soft Shadow"))
            {
                // a shadow of the S falls to the lower right of it: gray where the icon's white-ish background would otherwise be
                var shadowPixel = mine.GetPixel((int)(size * 0.80), (int)(size * 0.55));
                output.WriteLine($"shadow pixel {shadowPixel}");
                Assert.True(shadowPixel.Red < 225, $"expected a shadow, found {shadowPixel}");
            }
        }
        LytRenderTests.Save(sheet, "effect-import-icons-vs-legacy.png");
        if (Environment.GetEnvironmentVariable("DSM_SHOTS") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "effect-import-icons-vs-legacy.png"), SKImage.FromBitmap(sheet).Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }
}
