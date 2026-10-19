using System.IO.Compression;
using System.Text;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Geometry;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>
/// Live effects in <c>.dsm</c> format 4: the links of soft shadows, glows and bevels come back and keep following after reopening; links that files saved
/// while outlines and shadows were live carry for those are dropped; a file without links opens with plain effects.
/// </summary>
public class LiveEffectFileTests
{
    private static EditorSession Session(Document doc) => new(doc) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets };

    /// <summary>A plate with a soft shadow (live), a text with a hard extruded shadow (plain), a glow and a bevel (live).</summary>
    private static (EditorSession S, RectangleObject A, TextObject T) Design()
    {
        var s = Session(new Document(PageSetup.FromInches(24, 12)));
        var layer = s.Document.ActiveLayer;
        var a = new RectangleObject(100, 100) { Name = "plate", Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#2A9D8F")) };
        var t = new TextObject("OPEN", "Arial", 90) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#D62828")) };
        s.History.Execute(new AddObjectCommand(layer, a));
        s.History.Execute(new AddObjectCommand(layer, t));
        s.Selection.Set(a);
        s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 100));
        s.Selection.Set(t);
        Assert.True(s.Shadow(new ShadowSettings { ShiftX = 8, ShiftY = 6, Form = ShadowForm.Extruded, Outline = 1 }).Applied);
        s.Selection.Set(t.TopLevel);
        s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.Glow, 90, DocColor.FromHex("#D62828")));
        s.Selection.Set(t.TopLevel);
        s.ApplyBevel(new BevelSettings { Width = 0.04, LightAngle = 90 });
        return (s, a, t);
    }

    private static byte[] Rewrite(byte[] dsm, Func<string, string> edit)
    {
        using var input = new ZipArchive(new MemoryStream(dsm));
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries)
            {
                using var src = entry.Open();
                using var ms = new MemoryStream();
                src.CopyTo(ms);
                var data = entry.FullName == DsmFormat.DocumentEntry ? Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(ms.ToArray()))) : ms.ToArray();
                using var dst = zip.CreateEntry(entry.FullName).Open();
                dst.Write(data);
            }
        return output.ToArray();
    }

    private static string Json(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file));
        using var reader = new StreamReader(zip.GetEntry(DsmFormat.DocumentEntry)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Links_come_back_after_save_and_the_effects_keep_following_their_sources()
    {
        var (s, _, _) = Design();
        Assert.Equal(4, LiveEffects.All(s.Document).Count());                                // soft shadow, glow, two crescents (the hard shadow is plain)
        var bytes = DsmWriter.SaveToBytes(s.Document);
        Assert.Contains("\"formatVersion\": 4", Json(bytes));
        Assert.Contains("\"linkId\"", Json(bytes));

        var loaded = DsmReader.Load(new MemoryStream(bytes));
        Assert.Empty(loaded.Warnings);
        ModelCompare.AssertEqual(s.Document, loaded.Document);
        var doc = loaded.Document;
        var effects = LiveEffects.All(doc).ToList();
        Assert.Equal(4, effects.Count);
        Assert.All(effects, e => Assert.True(doc.Contains(e.Live!.Source)));

        // edit the sources in the reopened document: every effect follows
        var s2 = Session(doc);
        var plate = (RectangleObject)doc.AllObjects().Single(o => o.Name == "plate");
        var text = doc.AllObjects().OfType<TextObject>().Single();
        var edit = new SetVectorsCommand("Move Node", plate, -1, PathGeometry.Rectangle(new Rect2(0, 0, 200, 100)));   // the plate becomes a path, twice as wide
        s2.History.Execute(edit);
        s2.History.Execute(new SetTextCommand(text, "OPEN LATE"));
        foreach (var e in LiveEffects.All(doc))
        {
            var fresh = LiveEffects.Recompute(e, s2.BooleanService, s2.OffsetService)!.Transform(e.GetWorldTransform()).GetBounds();
            var now = e.GetWorldBounds();
            Assert.InRange(now.Width, fresh.Width - 0.05, fresh.Width + 0.05);
            Assert.InRange(now.Left, fresh.Left - 0.05, fresh.Left + 0.05);
        }
        var soft = LiveEffects.EffectsOf(doc, edit.Result).Single();
        Assert.InRange(soft.GetWorldBounds().Right, 309.9, 310.1);                            // shifted 10 from the 200 wide plate
    }

    [Fact]
    public void A_moved_effect_keeps_its_place_through_save_and_reopen()
    {
        var (s, a, _) = Design();
        var soft = LiveEffects.EffectsOf(s.Document, a).Single();
        s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [soft], Affine2.Translation(0, 40)));
        Assert.NotNull(soft.Live!.SourceFrame);

        var doc = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(s.Document))).Document;
        var s2 = Session(doc);
        var plate = doc.AllObjects().Single(o => o.Name == "plate");
        var edit = new SetVectorsCommand("Move Node", plate, -1, PathGeometry.Rectangle(new Rect2(0, 0, 100, 200)));   // twice as tall
        s2.History.Execute(edit);
        var moved = LiveEffects.EffectsOf(doc, edit.Result).Single();
        Assert.InRange(moved.GetWorldBounds().Top, 149.9, 150.1);                            // still 40 below where the soft shadow belongs
        Assert.InRange(moved.GetWorldBounds().Bottom, 349.9, 350.1);
    }

    [Fact]
    public void Links_on_outlines_inlines_and_shadows_from_files_of_the_live_outline_days_are_dropped_and_the_soft_ones_kept()
    {
        // what 0.4.3 / 0.4.4 saved: an outline, an inline and a shadow that followed their source, next to a live soft shadow
        var s = Session(new Document(PageSetup.FromInches(24, 12)));
        var layer = s.Document.ActiveLayer;
        var a = new RectangleObject(100, 100) { Name = "plate", Transform = Affine2.Translation(100, 100) };
        s.History.Execute(new AddObjectCommand(layer, a));
        PathObject Effect(string name, LiveEffectSpec spec)
        {
            var e = new PathObject(PathGeometry.Rectangle(new Rect2(80, 80, 220, 220))) { Name = name, Live = new LiveLink(a, spec) };
            s.History.Execute(new AddObjectCommand(layer, e));
            return e;
        }
        var outline = Effect("Outline", new LiveOutline(OutlineKind.Outline, new OutlineSettings { First = 20 }));
        var inline = Effect("Inline", new LiveOutline(OutlineKind.Inline, new OutlineSettings { First = 5 }));
        var shadow = Effect("Shadow", new LiveShadow(new ShadowSettings { ShiftX = 8, ShiftY = 8 }));
        s.Selection.Set(a);
        s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 100));
        var bytes = DsmWriter.SaveToBytes(s.Document);
        string json = Json(bytes);
        Assert.Contains("\"effect\": \"outline\"", json);
        Assert.Contains("\"effect\": \"shadow\"", json);

        var loaded = DsmReader.Load(new MemoryStream(bytes));
        Assert.Empty(loaded.Warnings);                                                        // dropped quietly: the objects are as they were saved
        var doc = loaded.Document;
        foreach (var name in new[] { "Outline", "Inline", "Shadow" })
        {
            var o = Assert.IsType<PathObject>(doc.AllObjects().Single(x => x.Name == name));
            Assert.Null(o.Live);
            Assert.Equal(new Rect2(80, 80, 220, 220), o.GetWorldBounds());
        }
        var soft = Assert.Single(LiveEffects.All(doc));
        Assert.IsType<LiveSoft>(soft.Live!.Spec);

        // and they stay put when the plate changes, while the soft shadow follows
        var s2 = Session(doc);
        var plate = doc.AllObjects().Single(o => o.Name == "plate");
        s2.History.Execute(TransformObjectsCommand.ApplyWorld("Size", [plate], Affine2.Scale(2, 1, new Vec2(100, 100))));
        Assert.Equal(new Rect2(80, 80, 220, 220), doc.AllObjects().Single(x => x.Name == "Outline").GetWorldBounds());
        Assert.InRange(soft.GetWorldBounds().Right, 309.9, 310.1);
    }

    [Fact]
    public void A_file_without_links_opens_with_plain_effects_and_a_lost_source_is_reported()
    {
        var (s, _, _) = Design();
        string json = Json(DsmWriter.SaveToBytes(s.Document));

        // an older file: no links at all
        var old = Rewrite(DsmWriter.SaveToBytes(s.Document), t => System.Text.RegularExpressions.Regex.Replace(t, "\"linkId\": \\d+,", ""));
        var plain = DsmReader.Load(new MemoryStream(old));
        Assert.Empty(LiveEffects.All(plain.Document));
        Assert.Contains(plain.Warnings, w => w.Contains("live effect"));
        Assert.Contains("\"live\"", json);
    }
}
