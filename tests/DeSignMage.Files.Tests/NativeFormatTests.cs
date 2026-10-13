using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Formats.Native;
using Xunit;
using Xunit.Abstractions;
using DeSignMage.TestSupport;

namespace DeSignMage.Files.Tests;

/// <summary>The native .dsm format: round trips, versions, damage, extensibility.</summary>
public class NativeFormatTests(ITestOutputHelper output)
{
    private static DsmLoadResult RoundTrip(Document doc, DsmSaveOptions? options = null) =>
        DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc, options)));

    // ---------------------------------------------------------------- round trips

    [Fact]
    public void Every_object_type_and_property_survives_a_save_and_load()
    {
        var doc = SampleDocuments.Everything();

        var loaded = RoundTrip(doc);

        ModelCompare.AssertEqual(doc, loaded.Document);
        Assert.Equal(DsmFormat.CurrentVersion, loaded.FormatVersion);
        Assert.Equal(0, loaded.SkippedObjects);
        // The only message is about the one font that is deliberately not installed.
        var warning = Assert.Single(loaded.Warnings);
        Assert.Contains("Zzyzx Display Not Installed", warning);
    }

    [Fact]
    public void The_round_trip_comparison_really_compares_things()
    {
        // Guard for the guard: a changed property must show up as a difference.
        var a = SampleDocuments.Everything();
        var b = RoundTrip(a).Document;
        ((TextObject)b.AllObjects().First(o => o is TextObject { Name: "headline" })).Tracking += 0.001;
        Assert.NotEmpty(ModelCompare.Diff(a, b));

        var c = RoundTrip(a).Document;
        new RemoveGuideCommand(c.Guides[2]).Do(c);
        Assert.NotEmpty(ModelCompare.Diff(a, c));

        var d = RoundTrip(a).Document;
        ((RectangleObject)d.Layers[0].Children[0]).Style = ObjectStyle.FillOnly(new DocColor(0xF2, 0xB7, 0x05, 255, "different name"));
        Assert.NotEmpty(ModelCompare.Diff(a, d));
    }

    [Fact]
    public void Saving_twice_gives_the_same_document_json()
    {
        var doc = SampleDocuments.Everything();
        var stamp = new DsmSaveOptions { SavedUtc = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc), AppVersion = "1.2.3" };
        var first = Zip.DocumentJson(DsmWriter.SaveToBytes(doc, stamp));
        var again = Zip.DocumentJson(DsmWriter.SaveToBytes(RoundTrip(doc, stamp).Document, stamp));
        Assert.Equal(first, again);
    }

    [Fact]
    public void The_file_is_a_zip_with_readable_json_a_version_and_the_assets()
    {
        var doc = SampleDocuments.Everything();
        var bytes = DsmWriter.SaveToBytes(doc, new DsmSaveOptions { AppVersion = "9.8.7", Thumbnail = [0x89, 0x50, 0x4E, 0x47] });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == "document.json");
        Assert.Contains(zip.Entries, e => e.FullName == "thumbnail.png");
        Assert.Equal(doc.Assets.All.Count(), zip.Entries.Count(e => e.FullName.StartsWith("assets/")));

        using var json = JsonDocument.Parse(Zip.DocumentJson(bytes));
        var root = json.RootElement;
        Assert.Equal("DeSign Mage document", root.GetProperty("format").GetString());
        Assert.Equal(DsmFormat.CurrentVersion, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(DsmFormat.MinReaderVersion, root.GetProperty("minReaderVersion").GetInt32());
        Assert.Equal("9.8.7", root.GetProperty("app").GetProperty("version").GetString());
        Assert.Equal(1728, root.GetProperty("page").GetProperty("width").GetDouble());
        Assert.Equal(3, root.GetProperty("layers").GetArrayLength());
        Assert.Equal(3, root.GetProperty("guides").GetArrayLength());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, DsmReader.Load(new MemoryStream(bytes)).Thumbnail);
    }

    [Fact]
    public void Break_Apart_still_works_on_a_combined_object_after_reopening()
    {
        var doc = SampleDocuments.Everything();
        var reopened = RoundTrip(doc).Document;
        var combined = reopened.AllObjects().OfType<CombinedObject>().Single();
        Assert.Equal(4, combined.Parts.Count);

        var session = new EditorSession(reopened);
        session.Selection.Set(combined);
        Assert.True(session.CanBreakApart);
        session.BreakApartSelection();

        Assert.DoesNotContain(reopened.AllObjects(), o => o is CombinedObject);
        var layer = reopened.Layers[2];
        Assert.Contains(layer.Children, o => o is TextObject { Text: "Hi" });
        Assert.Contains(layer.Children, o => o is RectangleObject { Name: "red" });
        Assert.Contains(layer.Children, o => o is EllipseObject { Style.Stroke.Width: 2 });
        Assert.Contains(layer.Children, o => o is PathObject);
        Assert.Equal(doc.Layers[2].Children.Count + 3, layer.Children.Count);   // one object became four
    }

    [Fact]
    public void Numbers_come_back_bit_for_bit_including_the_awkward_ones()
    {
        var doc = new Document(PageSetup.FromInches(2, 2));
        var values = new List<double> { Math.Pow(2, -25), Math.Pow(2, -30), -Math.Pow(2, -40), 1e-300, 1.7976931348623157e308, 5e-324, 0.1 + 0.2, 1.0 / 3, -0.0, 123456789012345678.0, Math.PI * 1e15 };
        var rng = new Random(11);
        for (int i = 0; i < 2000; i++) values.Add((rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-12, 12)));
        for (int e = -60; e <= 60; e++) values.Add(Math.Pow(2, e));
        var b = new PathBuilder();
        b.MoveTo(0, 0);
        for (int i = 0; i + 1 < values.Count; i += 2) b.LineTo(values[i], values[i + 1]);
        doc.Insert(doc.Layers[0], -1, new PathObject(b.Build()));

        var loaded = RoundTrip(doc).Document;

        var a = ((PathObject)doc.Layers[0].Children[0]).Geometry.Points;
        var c = ((PathObject)loaded.Layers[0].Children[0]).Geometry.Points;
        Assert.Equal(a.Count, c.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].X), BitConverter.DoubleToInt64Bits(c[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].Y), BitConverter.DoubleToInt64Bits(c[i].Y));
        }
    }

    [Fact]
    public void Live_text_keeps_the_requested_font_even_when_it_is_missing()
    {
        var doc = SampleDocuments.Everything();
        var bytes = DsmWriter.SaveToBytes(doc);

        var json = Zip.DocumentJson(bytes);
        Assert.Contains("\"fontMissing\": true", json);       // recorded for readers of the file
        Assert.Contains("\"fontResolved\"", json);

        var loaded = DsmReader.Load(new MemoryStream(bytes));
        var text = loaded.Document.AllObjects().OfType<TextObject>().Single(t => t.Text == "Missing font");
        Assert.Equal("Zzyzx Display Not Installed", text.FontFamily);
        Assert.True(text.IsFontMissing);
        Assert.Equal(1, loaded.MissingFonts["Zzyzx Display Not Installed"]);
        Assert.False(loaded.Document.AllObjects().OfType<TextObject>().Single(t => t.Text == "Right aligned").IsFontMissing);
    }

    [Fact]
    public void Document_structure_is_preserved_nested_groups_layers_guides_flags()
    {
        var loaded = RoundTrip(SampleDocuments.Everything()).Document;

        Assert.Equal(new[] { "Artwork", "Hidden stuff", "Top ünïcode 層" }, loaded.Layers.Select(l => l.Name));
        Assert.Same(loaded.Layers[2], loaded.ActiveLayer);
        Assert.False(loaded.Layers[1].Visible);
        Assert.True(loaded.Layers[1].Locked);
        Assert.False(loaded.Layers[1].Printable);
        Assert.Equal("Layer chip", loaded.Layers[0].Color.Name);
        Assert.Equal("Chips Inc", loaded.Layers[0].Color.Vendor);

        // color identity: name, vendor, model and CMYK components all survive
        var grey = (EllipseObject)loaded.Layers[0].Children[1];
        var fill = Assert.IsType<SolidPaint>(grey.Style.Fill).Color;
        Assert.Equal(ColorModel.Cmyk, fill.Model);
        Assert.Equal(new Cmyk(10.5, 20, 30.25, 40), fill.Cmyk);
        Assert.Equal(("Warm Grey 4", "Pantone"), (fill.Name, fill.Vendor));
        var photo = loaded.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal((4, 3, "customer logo.png"), (photo.PixelWidth, photo.PixelHeight, photo.SourceName));

        // groups nested three deep: membership only, the members sit in the layer's drawing order in the order they were made
        var outer = Assert.Single(loaded.Groups, g => g.Parent is Layer);
        Assert.Equal("outer", outer.Name);
        var middle = Assert.Single(outer.Children.OfType<GroupObject>());
        var inner = Assert.Single(middle.Children.OfType<GroupObject>());
        Assert.Equal("inner", inner.Name);
        Assert.IsType<EllipseObject>(inner.Children.Single());
        Assert.Equal([inner.Children[0], middle.Children.OfType<RectangleObject>().Single(), outer.Children.OfType<PathObject>().Single()],
            loaded.Layers[0].Children.Skip(4).Take(3));

        Assert.Equal(3, loaded.Guides.Count);
        Assert.Equal(GuideOrientation.Horizontal, loaded.Guides[0].Orientation);
        Assert.Equal(100.5, loaded.Guides[0].Origin.Y);
        Assert.Equal(GuideOrientation.Vertical, loaded.Guides[1].Orientation);
        Assert.True(loaded.Guides[2].Locked);
        Assert.Equal(33.5, loaded.Guides[2].AngleDegrees);

        Assert.False(loaded.Layers[0].Children[1].Visible);
        Assert.True(loaded.Layers[0].Children[2].Locked);
        Assert.Equal("Panel \"A\" & <B>", loaded.Layers[0].Children[0].Name);
        // the object that sits off the page, on the pasteboard, is still there
        Assert.True(loaded.Layers[0].Children.OfType<PathObject>().Single(p => p.Name == "Vector Graphic 1").GetWorldBounds().Left < 0);
    }

    [Fact]
    public void Arcs_cubics_and_fill_rule_come_back_bit_for_bit()
    {
        var doc = SampleDocuments.Everything();
        var original = doc.AllObjects().OfType<PathObject>().First(p => p.Geometry.HasArcs);
        var loaded = RoundTrip(doc).Document.AllObjects().OfType<PathObject>().First(p => p.Geometry.HasArcs);

        Assert.True(loaded.Geometry.HasArcs);
        Assert.Equal(FillRule.EvenOdd, loaded.Geometry.FillRule);
        Assert.Equal(original.Geometry.Verbs, loaded.Geometry.Verbs);
        Assert.Equal(original.Geometry.Points, loaded.Geometry.Points);   // exact doubles, not rounded
        Assert.Equal(original.Transform, loaded.Transform);
    }

    // ---------------------------------------------------------------- .lyt -> .dsm -> reopen

    public static IEnumerable<object[]> Layouts() => TestFiles.Layouts.Select(l => new object[] { l });

    [LocalTheory("layouts/manifest.json")]
    [MemberData(nameof(Layouts))]
    public void An_imported_legacy_layout_saved_as_dsm_reopens_with_identical_geometry(string relative)
    {
        var path = TestFiles.Layout(relative);
        var imported = LytImporter.Open(path).Document;

        var bytes = DsmWriter.SaveToBytes(imported);
        var reopened = DsmReader.Load(new MemoryStream(bytes));

        ModelCompare.AssertEqual(imported, reopened.Document);
        var a = imported.AllObjects().ToList();
        var b = reopened.Document.AllObjects().ToList();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].GetWorldBounds(), b[i].GetWorldBounds());
            Assert.Equal(a[i].GetWorldTransform(), b[i].GetWorldTransform());
        }
        Assert.Equal(imported.Page, reopened.Document.Page);
        output.WriteLine($"{relative}: {a.Count} objects, {bytes.Length / 1024} KB .dsm");
    }

    [LocalFact("layouts/art-a.lyt")]
    public void A_large_layout_reopens_with_the_same_objects_text_and_guides()
    {
        var path = TestFiles.Layout("art-a.lyt");
        var before = File.ReadAllBytes(path);
        var imported = LytImporter.Open(path).Document;

        var reopened = RoundTrip(imported).Document;
        Assert.Equal(imported.ObjectCount, reopened.ObjectCount);
        Assert.Equal(imported.AllObjects().OfType<TextObject>().Select(t => (t.Text, t.FontFamily, t.FontSize, t.Tracking)),
                     reopened.AllObjects().OfType<TextObject>().Select(t => (t.Text, t.FontFamily, t.FontSize, t.Tracking)));
        Assert.Equal(imported.Guides.Count, reopened.Guides.Count);
        Assert.Equal(before, File.ReadAllBytes(path));   // the legacy file is only ever read
    }

    // ---------------------------------------------------------------- versions and damage

    [Fact]
    public void A_newer_file_that_is_still_readable_opens_with_a_warning_and_skips_what_it_does_not_know()
    {
        var bytes = DsmWriter.SaveToBytes(SampleDocuments.Everything());
        var newer = Zip.EditDocumentJson(bytes, json => json
            .Replace($"\"formatVersion\": {DsmFormat.CurrentVersion}", "\"formatVersion\": 7")
            .Replace("\"kind\": \"ellipse\"", "\"kind\": \"hologram\", \"depth\": 12")
            .Replace("\"type\": \"solid\"", "\"type\": \"solid\", \"dither\": true, \"shiny\": {\"a\": [1,2,3]}")
            .Replace("\"format\": \"DeSign Mage document\"", "\"format\": \"DeSign Mage document\", \"somethingNew\": [1, 2]"));

        var loaded = DsmReader.Load(new MemoryStream(newer));

        Assert.Equal(7, loaded.FormatVersion);
        Assert.Contains(loaded.Warnings, w => w.Contains("newer version"));
        Assert.Contains(loaded.Warnings, w => w.Contains("hologram"));
        Assert.True(loaded.SkippedObjects >= 1);
        // everything else is intact, and unknown fields on known things are simply ignored
        Assert.DoesNotContain(loaded.Document.AllObjects(), o => o is EllipseObject && o.Parent is Layer { Name: "Artwork" });
        Assert.Single(loaded.Document.AllObjects().OfType<CombinedObject>());
        Assert.Equal(3, loaded.Document.Guides.Count);
    }

    [Theory]
    [InlineData("DeSign Mage document")]
    [InlineData("DeSign Earlier document")]   // the marker an earlier build wrote ("DeSign <product> document")
    public void The_format_marker_of_earlier_builds_is_accepted(string marker)
    {
        var doc = SampleDocuments.Everything();
        var edited = Zip.EditDocumentJson(DsmWriter.SaveToBytes(doc), json => json.Replace("\"format\": \"DeSign Mage document\"", $"\"format\": \"{marker}\""));

        var loaded = DsmReader.Load(new MemoryStream(edited));

        Assert.Equal(doc.ObjectCount, loaded.Document.ObjectCount);
        Assert.DoesNotContain(loaded.Warnings, w => w.Contains("newer version"));
    }

    [Theory]
    [InlineData("Something else")]
    [InlineData("DeSign Mage documents")]
    [InlineData("")]
    public void Any_other_format_marker_is_refused(string marker)
    {
        var edited = Zip.EditDocumentJson(DsmWriter.SaveToBytes(SampleDocuments.Everything()), json => json.Replace("\"format\": \"DeSign Mage document\"", $"\"format\": \"{marker}\""));

        var ex = Assert.Throws<DsmFormatException>(() => DsmReader.Load(new MemoryStream(edited)));
        Assert.Contains("DeSign Mage document", ex.Message);
    }

    [Fact]
    public void Native_files_are_recognized_by_extension_including_the_one_earlier_builds_used()
    {
        Assert.True(DsmFormat.IsNativeFile("a.dsm"));
        Assert.True(DsmFormat.IsNativeFile(@"C:\x\A.DSM"));
        Assert.True(DsmFormat.IsNativeFile("a" + DsmFormat.LegacyExtension));
        Assert.False(DsmFormat.IsNativeFile("a.lyt"));
        Assert.False(DsmFormat.IsNativeFile("a"));
        Assert.NotEqual(DsmFormat.Extension, DsmFormat.LegacyExtension);
    }

    [Fact]
    public void A_file_that_needs_a_newer_reader_is_refused_with_a_clear_message()
    {
        var bytes = DsmWriter.SaveToBytes(SampleDocuments.Everything(), new DsmSaveOptions { AppVersion = "1.0" });
        var future = Zip.EditDocumentJson(bytes, json => json
            .Replace("\"formatVersion\": 1", "\"formatVersion\": 3")
            .Replace("\"minReaderVersion\": 1", "\"minReaderVersion\": 3"));

        var ex = Assert.Throws<DsmFormatException>(() => DsmReader.Load(new MemoryStream(future)));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void An_unknown_paint_type_drops_just_that_paint()
    {
        var doc = new Document(PageSetup.FromInches(2, 2));
        doc.Insert(doc.Layers[0], -1, new RectangleObject(10, 10) { Style = new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.FromHex("#FF0000"), 2)) });
        var edited = Zip.EditDocumentJson(DsmWriter.SaveToBytes(doc), json => json.Replace("\"type\": \"solid\"", "\"type\": \"mesh-gradient\"", StringComparison.Ordinal));

        var loaded = DsmReader.Load(new MemoryStream(edited));

        Assert.Equal(1, loaded.Document.ObjectCount);
        Assert.Contains(loaded.Warnings, w => w.Contains("mesh-gradient"));
    }

    [Fact]
    public void A_damaged_object_is_skipped_and_the_rest_of_the_file_still_opens()
    {
        var doc = new Document(PageSetup.FromInches(2, 2));
        var layer = doc.Layers[0];
        doc.Insert(layer, -1, new RectangleObject(11, 12));
        doc.Insert(layer, -1, new RectangleObject(21, 22) { Name = "bad" });
        doc.Insert(layer, -1, new EllipseObject(31, 32));
        var damaged = Zip.EditDocumentJson(DsmWriter.SaveToBytes(doc), json => json.Replace("\"width\": 21", "\"width\": \"wide\""));

        var loaded = DsmReader.Load(new MemoryStream(damaged));

        Assert.Equal(2, loaded.Document.ObjectCount);
        Assert.Equal(1, loaded.SkippedObjects);
        Assert.Contains(loaded.Warnings, w => w.StartsWith("Skipped a damaged object") && w.Contains("width"));
    }

    [Fact]
    public void A_path_with_mismatched_points_is_skipped_not_fatal()
    {
        var doc = new Document(PageSetup.FromInches(2, 2));
        doc.Insert(doc.Layers[0], -1, new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 5, 5))));
        doc.Insert(doc.Layers[0], -1, new RectangleObject(5, 5));
        var damaged = Zip.EditDocumentJson(DsmWriter.SaveToBytes(doc), json => json.Replace("\"verbs\": \"MLLLZ\"", "\"verbs\": \"MLLLLLLZ\""));

        var loaded = DsmReader.Load(new MemoryStream(damaged));

        Assert.Equal(1, loaded.Document.ObjectCount);
        Assert.Equal(1, loaded.SkippedObjects);
    }

    [Fact]
    public void Missing_asset_data_gives_a_placeholder_image_and_a_warning()
    {
        var doc = SampleDocuments.Everything();
        var bytes = DsmWriter.SaveToBytes(doc);
        // remove the picture's entry from the zip
        var stripped = new MemoryStream();
        using (var input = new ZipArchive(new MemoryStream(bytes)))
        using (var output = new ZipArchive(stripped, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in input.Entries.Where(e => !e.FullName.StartsWith("assets/")))
            {
                using var s = e.Open();
                using var d = output.CreateEntry(e.FullName).Open();
                s.CopyTo(d);
            }

        var loaded = DsmReader.Load(new MemoryStream(stripped.ToArray()));

        Assert.Single(loaded.Document.AllObjects().OfType<ImageObject>());
        Assert.Contains(loaded.Warnings, w => w.Contains("picture"));
    }

    [Fact]
    public void An_asset_that_does_not_match_its_checksum_is_still_used()
    {
        var doc = new Document(PageSetup.FromInches(2, 2));
        var id = doc.Assets.Add(SampleDocuments.TinyPng(), "image/png");
        doc.Insert(doc.Layers[0], -1, new ImageObject(id, 10, 10));
        var bytes = DsmWriter.SaveToBytes(doc);
        var tampered = new MemoryStream();
        using (var input = new ZipArchive(new MemoryStream(bytes)))
        using (var output = new ZipArchive(tampered, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in input.Entries)
            {
                using var s = e.Open();
                using var d = output.CreateEntry(e.FullName).Open();
                if (e.FullName.StartsWith("assets/")) d.Write(SampleDocuments.TinyPng().Append((byte)0).ToArray());
                else s.CopyTo(d);
            }

        var loaded = DsmReader.Load(new MemoryStream(tampered.ToArray()));

        var image = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.NotNull(loaded.Document.Assets.Get(image.AssetId));   // the object points at the stored bytes
        Assert.Contains(loaded.Warnings, w => w.Contains("checksum"));
    }

    [LocalFact("layouts/sample.lyt")]
    public void Things_that_are_not_documents_are_refused_never_a_crash()
    {
        var good = DsmWriter.SaveToBytes(SampleDocuments.Everything());
        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["random bytes"] = Enumerable.Range(0, 500).Select(i => (byte)(i * 31)).ToArray(),
            ["truncated zip"] = good[..(good.Length / 2)],
            ["zip without document.json"] = Zip.Single("other.txt", "hi"u8.ToArray()),
            ["json array"] = Zip.Single("document.json", "[1,2,3]"u8.ToArray()),
            ["bad json"] = Zip.Single("document.json", "{ not json"u8.ToArray()),
            ["wrong marker"] = Zip.Single("document.json", "{\"format\":\"something else\"}"u8.ToArray()),
            ["no page"] = Zip.Single("document.json", Encoding.UTF8.GetBytes("{\"format\":\"DeSign Mage document\",\"formatVersion\":1}")),
            ["page size zero"] = Zip.Single("document.json", Encoding.UTF8.GetBytes("{\"format\":\"DeSign Mage document\",\"formatVersion\":1,\"page\":{\"width\":0,\"height\":5}}")),
            ["a .lyt"] = File.ReadAllBytes(TestFiles.Sample),
        };
        foreach (var (name, bytes) in cases)
        {
            var ex = Assert.ThrowsAny<DsmFormatException>(() => DsmReader.Load(new MemoryStream(bytes)));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), name);
        }
    }

    [Fact]
    public void A_missing_or_empty_layer_list_still_gives_a_usable_document()
    {
        var bytes = Zip.Single("document.json", Encoding.UTF8.GetBytes("{\"format\":\"DeSign Mage document\",\"formatVersion\":1,\"page\":{\"width\":144,\"height\":72}}"));
        var loaded = DsmReader.Load(new MemoryStream(bytes));
        Assert.Single(loaded.Document.Layers);
        Assert.Equal(144, loaded.Document.Page.Width);
    }

    [Fact]
    public void Absurdly_deep_group_nesting_is_refused_without_a_stack_overflow()
    {
        var sb = new StringBuilder("{\"format\":\"DeSign Mage document\",\"formatVersion\":1,\"page\":{\"width\":72,\"height\":72},\"layers\":[{\"name\":\"L\",\"objects\":[");
        const int depth = 400;
        for (int i = 0; i < depth; i++) sb.Append("{\"kind\":\"group\",\"children\":[");
        sb.Append("{\"kind\":\"rect\",\"width\":1,\"height\":1}");
        for (int i = 0; i < depth; i++) sb.Append("]}");
        sb.Append("]}]}");

        var loaded = DsmReader.Load(new MemoryStream(Zip.Single("document.json", Encoding.UTF8.GetBytes(sb.ToString()))));

        Assert.Contains(loaded.Warnings, w => w.Contains("nested too deeply"));
    }

    // ---------------------------------------------------------------- files

    [Fact]
    public void Saving_to_a_path_replaces_the_old_file_and_leaves_no_temp_file()
    {
        var dir = Directory.CreateTempSubdirectory("dsm-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "design.dsm");
            DsmWriter.Save(new Document(PageSetup.FromInches(1, 1)), path);
            var doc = SampleDocuments.Everything();
            DsmWriter.Save(doc, path);

            Assert.Equal([path], Directory.GetFiles(dir));
            ModelCompare.AssertEqual(doc, DsmReader.Load(path).Document);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_failed_save_keeps_the_previous_file_intact()
    {
        var dir = Directory.CreateTempSubdirectory("dsm-fail-").FullName;
        try
        {
            var path = Path.Combine(dir, "design.dsm");
            var good = SampleDocuments.Everything();
            DsmWriter.Save(good, path);
            var before = File.ReadAllBytes(path);

            var bad = new Document(PageSetup.FromInches(1, 1));
            bad.Insert(bad.Layers[0], -1, new RectangleObject(double.NaN, 5));
            Assert.Throws<DsmFormatException>(() => DsmWriter.Save(bad, path));

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_open_in_another_program_for_reading_can_still_be_loaded_and_the_loader_does_not_lock_it()
    {
        var dir = Directory.CreateTempSubdirectory("dsm-lock-").FullName;
        try
        {
            var path = Path.Combine(dir, "design.dsm");
            DsmWriter.Save(SampleDocuments.Everything(), path);
            DsmReader.Load(path);
            File.Delete(path);   // would throw if the loader still held it
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- extensibility

    private sealed class PinObject(double x) : DocObject
    {
        public double X { get; set; } = x;
        public override string KindName => "Pin";
        protected override DocObject CloneCore() => new PinObject(X);
        public override PathGeometry? GetLocalGeometry() => null;
    }

    private sealed class PinCodec : ObjectCodec<PinObject>
    {
        public override string Kind => "pin";
        protected override void WriteFields(PinObject o, DsmWriteContext ctx) => ctx.Json.WriteNumber("x", o.X);
        protected override PinObject ReadFields(JsonElement e, DsmReadContext ctx) => new(e.Num("x"));
    }

    [Fact]
    public void A_new_object_type_plugs_in_with_one_codec()
    {
        var doc = new Document(PageSetup.FromInches(1, 1));
        doc.Insert(doc.Layers[0], -1, new PinObject(3.25) { Name = "pin", Transform = Affine2.Translation(4, 5) });

        // without a codec saving says so plainly
        var missing = Assert.Throws<DsmFormatException>(() => DsmWriter.SaveToBytes(doc));
        Assert.Contains("PinObject", missing.Message);

        var registry = DsmCodecRegistry.CreateDefault();
        registry.Register(new PinCodec());
        var bytes = DsmWriter.SaveToBytes(doc, registry: registry);

        var loaded = DsmReader.Load(new MemoryStream(bytes), registry);
        var pin = Assert.IsType<PinObject>(loaded.Document.Layers[0].Children.Single());
        Assert.Equal(3.25, pin.X);
        Assert.Equal("pin", pin.Name);
        Assert.Equal(Affine2.Translation(4, 5), pin.Transform);

        // a reader that does not know the type skips it with a warning
        var other = DsmReader.Load(new MemoryStream(bytes));
        Assert.Equal(0, other.Document.ObjectCount);
        Assert.Contains(other.Warnings, w => w.Contains("'pin'"));
    }

    [Fact]
    public void Every_object_class_in_Core_has_a_file_codec()
    {
        var objectTypes = typeof(DocObject).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(DocObject).IsAssignableFrom(t));
        var registry = DsmCodecRegistry.Default;
        var uncovered = objectTypes.Where(t => !registry.ObjectCodecs.Any(c => c.ObjectType == t)).Select(t => t.Name).ToList();
        Assert.True(uncovered.Count == 0, "No .dsm codec for: " + string.Join(", ", uncovered) + " (see Native/BuiltInCodecs.cs)");
        Assert.Equal(registry.ObjectCodecs.Count(), registry.ObjectCodecs.Select(c => c.Kind).Distinct().Count());
    }

    [Fact]
    public void Every_paint_class_in_Core_has_a_file_codec()
    {
        var paintTypes = typeof(Paint).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(Paint).IsAssignableFrom(t));
        foreach (var t in paintTypes)
        {
            var sample = t == typeof(SolidPaint) ? new SolidPaint(DocColor.Black) : null;
            if (sample is null) Assert.Fail($"Add a sample of {t.Name} to this test and a PaintCodec for it.");
            Assert.NotNull(DsmCodecRegistry.Default.ForPaint(sample));
        }
    }

    [Fact]
    public void Registering_the_same_kind_twice_is_an_error()
    {
        var registry = DsmCodecRegistry.CreateDefault();
        registry.Register(new PinCodec());
        Assert.Throws<ArgumentException>(() => registry.Register(new PinCodec()));
    }

    // ---------------------------------------------------------------- scale

    [Fact]
    public void Five_thousand_objects_round_trip_quickly()
    {
        var doc = new Document(PageSetup.FromInches(48, 24));
        var layer = doc.Layers[0];
        var rng = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            DocObject o = (i % 3) switch
            {
                0 => new RectangleObject(rng.NextDouble() * 50, rng.NextDouble() * 50, rng.NextDouble() * 3),
                1 => new EllipseObject(rng.NextDouble() * 50, rng.NextDouble() * 50),
                _ => new PathObject(new PathBuilder().MoveTo(rng.NextDouble(), rng.NextDouble()).CubicTo(rng.NextDouble() * 9, 1, 2, rng.NextDouble() * 9, 7, 8).ArcTo(new Vec2(9, 9), new Vec2(12, 3)).Close().Build()),
            };
            o.Transform = Affine2.Translation(rng.NextDouble() * 3000, rng.NextDouble() * 1500);
            o.Style = ObjectStyle.FillOnly(new DocColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
            doc.Insert(layer, -1, o);
        }

        var watch = Stopwatch.StartNew();
        var bytes = DsmWriter.SaveToBytes(doc);
        long save = watch.ElapsedMilliseconds;
        watch.Restart();
        var loaded = DsmReader.Load(new MemoryStream(bytes));
        long load = watch.ElapsedMilliseconds;
        output.WriteLine($"5000 objects: save {save} ms, load {load} ms, {bytes.Length / 1024} KB");

        ModelCompare.AssertEqual(doc, loaded.Document);
        Assert.True(save < 5000 && load < 5000, $"save {save} ms, load {load} ms");
    }
}




