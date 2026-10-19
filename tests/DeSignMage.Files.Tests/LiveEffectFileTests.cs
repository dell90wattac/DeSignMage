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

/// <summary>Live effects in <c>.dsm</c> format 4: the links come back, the effects keep following after reopening, and a file without links opens with plain effects.</summary>
public class LiveEffectFileTests
{
    private static EditorSession Session(Document doc) => new(doc) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets };

    private static (EditorSession S, RectangleObject A, TextObject T) Design()
    {
        var s = Session(new Document(PageSetup.FromInches(24, 12)));
        var layer = s.Document.ActiveLayer;
        var a = new RectangleObject(100, 100) { Name = "plate", Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#2A9D8F")) };
        var t = new TextObject("OPEN", "Arial", 90) { Transform = Affine2.Translation(400, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#D62828")) };
        s.History.Execute(new AddObjectCommand(layer, a));
        s.History.Execute(new AddObjectCommand(layer, t));
        s.Selection.Set(a);
        Assert.True(s.RunEffect(OutlineEffect.Plan(s, OutlineKind.Outline, new OutlineSettings { First = 10 })!).Applied);
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
        Assert.Equal(5, LiveEffects.All(s.Document).Count());                                // outline, shadow, glow, two crescents
        var bytes = DsmWriter.SaveToBytes(s.Document);
        Assert.Contains("\"formatVersion\": 4", Json(bytes));
        Assert.Contains("\"linkId\"", Json(bytes));

        var loaded = DsmReader.Load(new MemoryStream(bytes));
        Assert.Empty(loaded.Warnings);
        ModelCompare.AssertEqual(s.Document, loaded.Document);
        var doc = loaded.Document;
        var effects = LiveEffects.All(doc).ToList();
        Assert.Equal(5, effects.Count);
        Assert.All(effects, e => Assert.True(doc.Contains(e.Live!.Source)));

        // edit the sources in the reopened document: every effect follows
        var s2 = Session(doc);
        var plate = (RectangleObject)doc.AllObjects().Single(o => o.Name == "plate");
        var text = doc.AllObjects().OfType<TextObject>().Single();
        s2.History.Execute(TransformObjectsCommand.ApplyWorld("Size", [plate], Affine2.Scale(2, 1, new Vec2(100, 100))));
        s2.History.Execute(new SetTextCommand(text, "OPEN LATE"));
        foreach (var e in LiveEffects.All(doc))
        {
            var fresh = LiveEffects.Recompute(e, s2.BooleanService, s2.OffsetService)!.Transform(e.GetWorldTransform()).GetBounds();
            var now = e.GetWorldBounds();
            Assert.InRange(now.Width, fresh.Width - 0.05, fresh.Width + 0.05);
            Assert.InRange(now.Left, fresh.Left - 0.05, fresh.Left + 0.05);
        }
        var outline = LiveEffects.EffectsOf(doc, plate).Single();
        Assert.InRange(outline.GetWorldBounds().Right, 309.9, 310.1);
    }

    [Fact]
    public void A_moved_effect_keeps_its_place_through_save_and_reopen()
    {
        var (s, a, _) = Design();
        var outline = LiveEffects.EffectsOf(s.Document, a).Single();
        s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [outline], Affine2.Translation(0, 40)));
        Assert.NotNull(outline.Live!.SourceFrame);

        var doc = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(s.Document))).Document;
        var s2 = Session(doc);
        var plate = doc.AllObjects().Single(o => o.Name == "plate");
        s2.History.Execute(TransformObjectsCommand.ApplyWorld("Size", [plate], Affine2.Scale(1, 2, new Vec2(100, 100))));
        var moved = LiveEffects.EffectsOf(doc, plate).Single();
        Assert.InRange(moved.GetWorldBounds().Top, 129.9, 130.1);                            // still 40 below where the outline belongs
        Assert.InRange(moved.GetWorldBounds().Bottom, 349.9, 350.1);
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
