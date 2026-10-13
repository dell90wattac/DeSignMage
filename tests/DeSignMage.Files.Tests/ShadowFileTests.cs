using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Geometry;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Shadows are ordinary path objects in an ordinary group, so a file holding them must come back the same: same drawing order, same group, same geometry.</summary>
public class ShadowFileTests
{
    private static Document Reload(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc))).Document;

    [Fact]
    public void Every_kind_of_shadow_survives_a_save_and_load_grouped_with_its_source_and_in_place()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(24, 12)))
        {
            BooleanService = GeometryEngines.Booleans,
            OffsetService = GeometryEngines.Offsets,
        };
        var layer = s.Document.ActiveLayer;
        var forms = new[] { ShadowForm.Flat, ShadowForm.Extruded, ShadowForm.Perspective, ShadowForm.Cast };
        var sources = new List<DocObject>();
        for (int i = 0; i < forms.Length; i++)
        {
            var rect = new RectangleObject(100, 60, 10)
            {
                Name = $"Source {i}", Transform = Affine2.Translation(100 + i * 200, 100),
                Style = ObjectStyle.FillOnly(new DocColor(200, 30, 40, 255, "Red", "Vinyl")),
            };
            s.History.Execute(new AddObjectCommand(layer, rect));
            s.Selection.Set(rect);
            Assert.True(s.Shadow(new ShadowSettings
            {
                Form = forms[i], Type = i == 1 ? ShadowType.Relief : ShadowType.Drop, ShiftX = 12, ShiftY = 9 + i, Outline = 2, DepthPercent = 60,
                Color = DocColor.FromHex("#303030"),
            }).Applied);
            sources.Add(rect);
        }
        // a shadow of an existing group: each member gets one, in that group
        var a = new RectangleObject(50, 50) { Transform = Affine2.Translation(100, 400) };
        var b = new RectangleObject(50, 50) { Transform = Affine2.Translation(180, 400) };
        s.History.Execute(new AddObjectCommand(layer, a));
        s.History.Execute(new AddObjectCommand(layer, b));
        var group = s.GroupObjects([a, b])!;
        s.Selection.Set(group);
        Assert.True(s.Shadow(new ShadowSettings { ShiftX = 8, ShiftY = 8 }).Applied);

        var loaded = Reload(s.Document);

        Assert.Equal(s.Document.GroupCount, loaded.GroupCount);
        var before = s.Document.Layers[0].Children;
        var after = loaded.Layers[0].Children;
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].GetType(), after[i].GetType());
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].Style, after[i].Style);
            Assert.Equal(before[i].GetWorldBounds(), after[i].GetWorldBounds());
            // the same membership: objects that shared a group still do, in the same order
            Assert.Equal(before[i].Parent is GroupObject, after[i].Parent is GroupObject);
        }
        foreach (var name in new[] { "Shadow", "Extruded Shadow", "Perspective Shadow", "Cast Shadow" })
        {
            var shadow = after.OfType<PathObject>().First(p => p.Name == name);
            var source = Assert.IsType<GroupObject>(shadow.Parent).Children.Single(c => c != shadow);
            Assert.StartsWith("Source", source.Name);
            Assert.Equal(before.OfType<PathObject>().First(p => p.Name == name).Geometry.Points.Count, shadow.Geometry.Points.Count);
        }
        var loadedGroup = Assert.IsType<GroupObject>(after.OfType<PathObject>().Last(p => p.Name == "Shadow").Parent);
        Assert.Equal(4, loadedGroup.Children.Count);                                 // the two squares, each with its own shadow
    }
}