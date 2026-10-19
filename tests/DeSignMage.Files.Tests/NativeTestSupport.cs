using System.IO.Compression;
using System.Reflection;
using System.Text;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>A document that uses every kind of object and every property the file has to keep.</summary>
internal static class SampleDocuments
{
    public static readonly DocColor VinylGold = new(0xF2, 0xB7, 0x05, 255, "Oracal 651-020 Golden Yellow", "Oracal 651");
    public static readonly DocColor WarmGrey = DocColor.FromCmyk(10.5, 20, 30.25, 40, "Warm Grey 4", "Pantone");
    public static readonly DocColor GlassBlue = new(0x10, 0x40, 0xC0, 128, "Translucent \"Blue\" & <co>");

    public static byte[] TinyPng()
    {
        using var bmp = new SKBitmap(4, 3);
        bmp.Erase(new SKColor(200, 30, 60));
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static Document Everything()
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        var l1 = doc.Layers[0];
        l1.Name = "Artwork";
        l1.Color = new DocColor(10, 20, 30, 255, "Layer chip", "Chips Inc");
        var l2 = new Layer("Hidden stuff") { Visible = false, Locked = true, Printable = false, Color = new DocColor(250, 0, 0) };
        doc.InsertLayer(1, l2);
        var l3 = new Layer("Top ünïcode 層") { Color = DocColor.FromHex("#8040A0") };
        doc.InsertLayer(2, l3);
        doc.ActiveLayer = l3;

        // rectangle with everything on: corner radius, name, rotation, named+alpha fill, round-joined stroke
        doc.Insert(l1, -1, new RectangleObject(120.5, 60.25, 8)
        {
            Name = "Panel \"A\" & <B>",
            Transform = Affine2.Rotation(0.3) * Affine2.Translation(50.123456789, 70.987654321),
            Style = new ObjectStyle(new SolidPaint(VinylGold), new StrokeStyle(new SolidPaint(GlassBlue), 3.5, LineJoin.Round, LineCap.Square, 7)),
        });
        doc.Insert(l1, -1, new EllipseObject(40, 20) { Transform = Affine2.Scale(1.5, -0.75) * Affine2.Translation(300, 200), Style = ObjectStyle.FillOnly(WarmGrey), Visible = false });
        doc.Insert(l1, -1, new RectangleObject(10, 10) { Locked = true, Style = ObjectStyle.None });

        // path: lines, cubic, arcs (incl. a near full circle), close, plus an open sub-path; even-odd
        var b = new PathBuilder { FillRule = FillRule.EvenOdd };
        b.MoveTo(0, 0).LineTo(100, 0).CubicTo(120, 10, 130, 40, 100, 50)
         .ArcTo(new Vec2(60, 80), new Vec2(20, 50)).LineTo(0, 30).Close()
         .MoveTo(300, 300).ArcTo(new Vec2(330, 330), new Vec2(300.0001, 300.0001)).LineTo(310, 305);
        doc.Insert(l1, -1, new PathObject(b.Build())
        {
            Name = "Vector Graphic 1",
            Transform = Affine2.Translation(-40, -25),   // off the page: pasteboard
            Style = new ObjectStyle(new SolidPaint(DocColor.FromHex("#112233")), null),
        });

        // groups nested three deep
        var g1 = new GroupObject { Name = "outer", Transform = Affine2.Translation(500, 100) };
        doc.Insert(l1, -1, g1);
        var g2 = new GroupObject { Transform = Affine2.Rotation(0.1) };
        doc.Insert(g1, -1, g2);
        var g3 = new GroupObject { Name = "inner" };
        doc.Insert(g2, -1, g3);
        doc.Insert(g3, -1, new EllipseObject(30, 30) { Style = ObjectStyle.FillOnly(VinylGold) });
        doc.Insert(g2, -1, new RectangleObject(5, 5) { Style = ObjectStyle.FillOnly(DocColor.Black), Transform = Affine2.Translation(40, 0) });
        doc.Insert(g1, -1, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 9, 9))) { Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 0.5)) });

        // combined object: parts of different kinds keep their own transforms and styles
        var parts = new DocObject[]
        {
            new RectangleObject(80, 40) { Style = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000")), Name = "red" },
            new EllipseObject(50, 50) { Transform = Affine2.Translation(30, 10), Style = new ObjectStyle(new SolidPaint(VinylGold), StrokeStyle.Solid(DocColor.Black, 2)) },
            new TextObject("Hi", "Segoe UI", 24) { Transform = Affine2.Translation(5, 35), Style = ObjectStyle.FillOnly(DocColor.White) },
            new PathObject(PathGeometry.Ellipse(new Rect2(0, 0, 20, 10))) { Transform = Affine2.Translation(60, 5) },
        };
        doc.Insert(l3, -1, new CombinedObject(parts) { Name = "combo", Transform = Affine2.Translation(200, 400) });

        // live text, several flavours
        doc.Insert(l3, -1, new TextObject("Kerned Text\nsecond line", "Segoe UI", 36)
        {
            Name = "headline",
            FontWeight = 700,
            Italic = true,
            Tracking = 0.05,
            Kerning = [0.1, 0, -0.05, 0.25],
            Alignment = TextAlignment.Center,
            LineSpacing = 1.2,
            Transform = Affine2.Translation(400, 300),
            Style = new ObjectStyle(new SolidPaint(VinylGold), StrokeStyle.Solid(DocColor.Black, 1.25)),
        });
        doc.Insert(l3, -1, new TextObject("Right aligned", "Arial", 18) { Alignment = TextAlignment.Right });
        doc.Insert(l3, -1, new TextObject("Missing font", "Zzyzx Display Not Installed", 20) { Style = ObjectStyle.FillOnly(DocColor.FromHex("#00AA55")) });
        doc.Insert(l3, -1, new TextObject("", "Arial", 12));

        // bitmap
        string asset = doc.Assets.Add(TinyPng(), "image/png");
        doc.Insert(l3, -1, new ImageObject(asset, 72, 54, 4, 3, "customer logo.png") { Name = "photo", Transform = Affine2.Translation(600, 20) });

        // shapes with their own settings: a star, a plain polygon, an arrow
        doc.Insert(l3, -1, new PolygonObject(7, 33.5, 0.42, 0.7) { Name = "star", Transform = Affine2.Translation(120.25, 80.5), Style = ObjectStyle.FillOnly(VinylGold) });
        doc.Insert(l3, -1, new PolygonObject(6, 20) { Style = ObjectStyle.None, Transform = Affine2.Rotation(0.2) * Affine2.Translation(300, 90) });
        doc.Insert(l3, -1, new ArrowObject(150.5, 45.25, 0.35, 0.55) { Name = "arrow", Transform = Affine2.Rotation(0.5) * Affine2.Translation(10, 10), Style = ObjectStyle.FillOnly(GlassBlue) });

        // live effects of every kind, following the star and the headline (format 4); their outlines are stand-ins, the file keeps them as they are.
        // (Outlines, inlines and shadows are plain objects: their links are dropped when a file is read, see LiveEffectFileTests.)
        var star = (PolygonObject)l3.Children.First(o => o.Name == "star");
        var headline = l3.Children.First(o => o.Name == "headline");
        var box = PathGeometry.Rectangle(new Rect2(0, 0, 40, 30));
        doc.Insert(l3, -1, new PathObject(box)
        {
            Name = "Soft Shadow",
            Style = new ObjectStyle(new SolidPaint(WarmGrey), null, new BlurStyle(SoftKind.SoftShadow, 0.4, 0.6, 36, Hs: 3, Vs: -2.5, Slant: 12, HeightScale: 0.8, Inset: 0.1)),
            Live = new LiveLink(headline, new LiveSoft(SoftKind.SoftShadow), Affine2.Rotation(-0.1), Affine2.Translation(30, 0) * Affine2.Rotation(-0.1)),
        });
        doc.Insert(l3, -1, new PathObject(box)
        {
            Name = "Glow",
            Style = new ObjectStyle(new SolidPaint(DocColor.White), null, new BlurStyle(SoftKind.Glow, 0.5, 0.75, 33.5, Inset: -0.5)),
            Live = new LiveLink(star, new LiveSoft(SoftKind.Glow)),
        });
        doc.Insert(l3, -1, new PathObject(box)
        {
            Name = "Bevel Shadow",
            Live = new LiveLink(star, new LiveBevel(new Core.Effects.BevelSettings
            {
                Inset = 0.02, Width = 0.033, Softness = 0.06, Highlight = 0.9, Shadow = 0.7, IncludeShadow = true, Neutral = true, LightAngle = 90,
                HighlightColor = VinylGold, ShadowColor = WarmGrey,
            }, BevelPart.Shadow)),
        });

        // guides
        new AddGuideCommand(Guide.Horizontal(100.5)).Do(doc);
        new AddGuideCommand(Guide.Vertical(250)).Do(doc);
        new AddGuideCommand(new Guide(new Vec2(300, 200), 33.5, locked: true)).Do(doc);
        return doc;
    }
}

/// <summary>Structural comparison of two documents through reflection, so a new property that a codec forgets is caught automatically.</summary>
internal static class ModelCompare
{
    // Id and the change counters (Version, ShapeVersion, LayoutStamp, DependencyStamp) are runtime bookkeeping, not part of the file.
    private static readonly HashSet<string> Ignored = ["Id", "Version", "ShapeVersion", "LayoutStamp", "DependencyStamp"];

    public static void AssertEqual(Document expected, Document actual)
    {
        var diffs = Diff(expected, actual);
        Assert.True(diffs.Count == 0, "Documents differ:\n" + string.Join("\n", diffs.Take(30)));
    }

    public static List<string> Diff(Document a, Document b)
    {
        var d = new List<string>();
        if (a.Page != b.Page) d.Add($"page {a.Page} vs {b.Page}");
        if (a.Layers.Count != b.Layers.Count) { d.Add($"layer count {a.Layers.Count} vs {b.Layers.Count}"); return d; }
        if (a.Layers.ToList().IndexOf(a.ActiveLayer) != b.Layers.ToList().IndexOf(b.ActiveLayer)) d.Add("active layer");
        if (a.GroupCount != b.GroupCount) d.Add($"group count {a.GroupCount} vs {b.GroupCount}");
        var pairs = new Dictionary<GroupObject, GroupObject>();
        for (int i = 0; i < a.Layers.Count; i++)
        {
            var la = a.Layers[i]; var lb = b.Layers[i];
            string at = $"layer[{i}] '{la.Name}'";
            Props(la, lb, at, d);
            Children(la.Children, lb.Children, at, d);
            Groups(la.Children, lb.Children, at, d, pairs);
        }
        if (a.Guides.Count != b.Guides.Count) d.Add($"guide count {a.Guides.Count} vs {b.Guides.Count}");
        else
            for (int i = 0; i < a.Guides.Count; i++)
                if (a.Guides[i].Origin != b.Guides[i].Origin || a.Guides[i].AngleDegrees != b.Guides[i].AngleDegrees || a.Guides[i].Locked != b.Guides[i].Locked)
                    d.Add($"guide[{i}] {a.Guides[i]} vs {b.Guides[i]}");
        var aa = a.Assets.All.OrderBy(x => x.Id).ToList(); var ab = b.Assets.All.OrderBy(x => x.Id).ToList();
        if (aa.Count != ab.Count) d.Add($"asset count {aa.Count} vs {ab.Count}");
        else
            for (int i = 0; i < aa.Count; i++)
                if (aa[i].Id != ab[i].Id || aa[i].MediaType != ab[i].MediaType || !aa[i].Data.AsSpan().SequenceEqual(ab[i].Data)) d.Add($"asset {aa[i].Id}");
        return d;
    }

    private static void Children(IReadOnlyList<DocObject> a, IReadOnlyList<DocObject> b, string at, List<string> d)
    {
        if (a.Count != b.Count) { d.Add($"{at}: child count {a.Count} vs {b.Count}"); return; }
        for (int i = 0; i < a.Count; i++) Object(a[i], b[i], $"{at}/{i}:{a[i].KindName}", d);
    }

    private static void Object(DocObject a, DocObject b, string at, List<string> d)
    {
        if (a.GetType() != b.GetType()) { d.Add($"{at}: type {a.GetType().Name} vs {b.GetType().Name}"); return; }
        Props(a, b, at, d);
        // a live effect's link: same effect and settings, same frames, and a source of the same kind in the same place
        if ((a.Live is null) != (b.Live is null)) d.Add($"{at}: live {a.Live is not null} vs {b.Live is not null}");
        else if (a.Live is { } la && b.Live is { } lb)
        {
            if (la.Spec != lb.Spec) d.Add($"{at}: live effect {la.Spec} vs {lb.Spec}");
            if (la.Frame != lb.Frame || la.SourceFrame != lb.SourceFrame) d.Add($"{at}: live frames differ");
            if (la.Source.KindName != lb.Source.KindName || la.Source.GetWorldBounds() != lb.Source.GetWorldBounds()) d.Add($"{at}: live source {la.Source} vs {lb.Source}");
        }
        if (a is GroupObject ga) Children(ga.Children, ((GroupObject)b).Children, at, d);
        if (a is CombinedObject ca) Children(ca.Parts, ((CombinedObject)b).Parts, at + "/parts", d);
    }

    /// <summary>
    /// Grouping is membership, separate from the drawing order (R22): every drawn object must be in the same groups (same properties, same nesting,
    /// and the same groups shared by the same objects) as before.
    /// </summary>
    private static void Groups(IReadOnlyList<DocObject> a, IReadOnlyList<DocObject> b, string at, List<string> d, Dictionary<GroupObject, GroupObject> pairs)
    {
        if (a.Count != b.Count) return;
        for (int i = 0; i < a.Count; i++)
        {
            GroupObject? ga = a[i].Parent as GroupObject, gb = b[i].Parent as GroupObject;
            while (ga is not null || gb is not null)
            {
                if (ga is null || gb is null) { d.Add($"{at}/{i}: group nesting differs"); break; }
                if (pairs.TryGetValue(ga, out var known)) { if (!ReferenceEquals(known, gb)) d.Add($"{at}/{i}: group '{ga.Name}' is a different group in the copy"); }
                else
                {
                    pairs[ga] = gb;
                    Props(ga, gb, $"{at}/{i}: group '{ga.Name}'", d);
                }
                ga = ga.Parent as GroupObject;
                gb = gb.Parent as GroupObject;
            }
        }
    }

    private static void Props(object a, object b, string at, List<string> d)
    {
        foreach (var p in a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || Ignored.Contains(p.Name) || !ValueLike(p.PropertyType)) continue;
            object? va = p.GetValue(a), vb = p.GetValue(b);
            if (!Same(va, vb)) d.Add($"{at}: {p.Name} = {Show(va)} vs {Show(vb)}");
        }
    }

    private static bool ValueLike(Type t) =>
        t.IsValueType || t == typeof(string) || typeof(Paint).IsAssignableFrom(t) || t == typeof(ObjectStyle) || t == typeof(PathGeometry) ||
        typeof(IReadOnlyList<double>).IsAssignableFrom(t);

    private static bool Same(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is PathGeometry pa && b is PathGeometry pb)
            return pa.FillRule == pb.FillRule && pa.Verbs.SequenceEqual(pb.Verbs) && pa.Points.SequenceEqual(pb.Points);
        if (a is IReadOnlyList<double> la && b is IReadOnlyList<double> lb) return la.SequenceEqual(lb);
        return a.Equals(b);
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        PathGeometry g => $"path({g.Verbs.Count} verbs)",
        IReadOnlyList<double> l => "[" + string.Join(",", l) + "]",
        _ => v.ToString() ?? "",
    };
}

internal static class Zip
{
    /// <summary>Returns a copy of a .dsm with document.json passed through <paramref name="edit"/>.</summary>
    public static byte[] EditDocumentJson(byte[] dsm, Func<string, string> edit)
    {
        using var input = new ZipArchive(new MemoryStream(dsm), ZipArchiveMode.Read);
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in input.Entries)
            {
                using var src = e.Open();
                using var dst = zip.CreateEntry(e.FullName).Open();
                if (e.FullName == "document.json")
                {
                    using var reader = new StreamReader(src, Encoding.UTF8);
                    dst.Write(Encoding.UTF8.GetBytes(edit(reader.ReadToEnd())));
                }
                else src.CopyTo(dst);
            }
        return output.ToArray();
    }

    public static string DocumentJson(byte[] dsm)
    {
        using var zip = new ZipArchive(new MemoryStream(dsm), ZipArchiveMode.Read);
        using var s = zip.GetEntry("document.json")!.Open();
        return new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }

    public static byte[] Single(string name, byte[] content)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var s = zip.CreateEntry(name).Open()) s.Write(content);
        return ms.ToArray();
    }
}




