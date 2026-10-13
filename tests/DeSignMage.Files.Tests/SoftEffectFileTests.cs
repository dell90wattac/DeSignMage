using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Geometry;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Soft Shadow, Glow and Feather are saved as parameters (R8): the file holds the vector outline and the numbers, never pixels.</summary>
public class SoftEffectFileTests
{
    private static Document Reload(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc))).Document;

    private static EditorSession NewSession() => new(new Document(PageSetup.FromInches(24, 12)))
    {
        BooleanService = GeometryEngines.Booleans,
        OffsetService = GeometryEngines.Offsets,
    };

    [Fact]
    public void A_soft_shadow_a_glow_and_a_feather_come_back_with_the_same_parameters_in_the_same_place()
    {
        var s = NewSession();
        var layer = s.Document.ActiveLayer;
        var box = new RectangleObject(120, 80, 12) { Name = "Box", Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(new DocColor(200, 30, 40, 255, "Red", "Vinyl")) };
        s.History.Execute(new AddObjectCommand(layer, box));
        s.Selection.Set(box);
        s.ApplySoftEffect(new SoftSettings
        {
            Kind = SoftKind.SoftShadow, Opacity = 0.65, Softness = 0.8, ShiftX = 9, ShiftY = 14, Inset = 0.2, Slant = 20, HeightScale = 0.6,
            Color = new DocColor(20, 20, 60, 255, "Navy", "Vinyl"),
        });
        s.ApplySoftEffect(new SoftSettings { Kind = SoftKind.Glow, Opacity = 0.9, Softness = 0.3, Thickness = 1.5, Color = DocColor.White });
        var feathered = new RectangleObject(60, 60) { Name = "Soft", Transform = Affine2.Translation(400, 100), Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(layer, feathered));
        s.Selection.Set(feathered);
        Assert.True(s.SetFeather(0.07, 0.4));

        var loaded = Reload(s.Document);

        var before = s.Document.Layers[0].Children;
        var after = loaded.Layers[0].Children;
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].Style, after[i].Style);                                // including Blur and Feather, value for value
            Assert.Equal(before[i].GetWorldBounds(), after[i].GetWorldBounds());
            Assert.Equal(before[i].Parent is GroupObject, after[i].Parent is GroupObject);
        }
        var shadow = after.OfType<PathObject>().Single(p => p.Name == "Soft Shadow");
        Assert.Equal(0.65, shadow.Style.Blur!.Opacity);
        Assert.Equal(0.2, shadow.Style.Blur.Inset);
        Assert.Equal(new FeatherStyle(0.07, 0.4), after.OfType<RectangleObject>().Single(r => r.Name == "Soft").Style.Feather);
        var group = Assert.IsType<GroupObject>(shadow.Parent);
        Assert.Equal(3, group.Children.Count);                                           // the box and its two effects
    }

    [Fact]
    public void An_object_without_soft_effects_writes_no_soft_fields()
    {
        var s = NewSession();
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, new RectangleObject(50, 50) { Style = ObjectStyle.FillOnly(DocColor.Black) }));
        var text = System.Text.Encoding.UTF8.GetString(DsmWriter.SaveToBytes(s.Document));
        Assert.DoesNotContain("blur", text);
        Assert.DoesNotContain("feather", text);
    }
}
