using System.Diagnostics;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using Xunit.Abstractions;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// Live effects (2026-10-18): every undo step looks at every live effect, and rebuilds the ones whose source changed in the same step. Looking must cost next
/// to nothing on a big design, moving things must not rebuild anything (a move is a shift), and a rebuild of big lettering must stay well under a second.
/// Measured in Debug on the dev PC, best of several runs; budgets leave room for the old PC.
/// </summary>
[Collection("perf")]
public class LiveEffectPerfTests(ITestOutputHelper output)
{
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static EditorSession NewSession()
    {
        DeSignMage.Text.SkiaFontService.Install();
        var s = new EditorSession(new Document(PageSetup.FromInches(48, 24)))
        {
            BooleanService = GeometryEngines.Booleans,
            OffsetService = GeometryEngines.Offsets,
        };
        s.Viewport.SetViewSize(new Vec2(1600, 900));
        return s;
    }

    /// <summary>300 letters as separate shapes, each with a live soft shadow, and 700 plain shapes beside them.</summary>
    private static (EditorSession S, List<DocObject> Letters, List<RectangleObject> Plain) Sign()
    {
        var s = NewSession();
        var letters = new List<DocObject>();
        var plain = new List<RectangleObject>();
        var layer = s.Document.ActiveLayer;
        string text = "THEQUICKBROWNFOXJUMPSOVERLAZYD";   // no spaces: a space has no outline to shadow
        for (int i = 0; i < 300; i++)
        {
            var t = new TextObject(text[i % text.Length].ToString(), "Arial", 72) { Transform = Affine2.Translation(40 + i % 30 * 60, 100 + i / 30 * 90), Style = ObjectStyle.FillOnly(DocColor.Black) };
            s.History.Execute(new AddObjectCommand(layer, t));
            letters.Add(t);
        }
        for (int i = 0; i < 700; i++)
        {
            var r = new RectangleObject(20, 10) { Transform = Affine2.Translation(40 + i % 50 * 40, 1100 + i / 50 * 20) };
            s.History.Execute(new AddObjectCommand(layer, r));
            plain.Add(r);
        }
        s.Selection.Set(letters);
        s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 52));   // live soft shadows (hard shadows are plain objects)
        Assert.Equal(300, LiveEffects.All(s.Document).Count());
        return (s, letters, plain);
    }

    private double Best(int runs, Action setUp, Action work)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            setUp();
            long t = Stopwatch.GetTimestamp();
            work();
            best = Math.Min(best, Ms(Stopwatch.GetTimestamp() - t));
        }
        return best;
    }

    [Fact]
    public void An_edit_elsewhere_in_a_design_with_300_live_shadows_costs_next_to_nothing()
    {
        var (s, _, plain) = Sign();
        int rebuilds = s.LiveUpdater.Rebuilds;
        double plainMove = Best(8, () => { }, () => s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [plain[3]], Affine2.Translation(1, 0))));
        output.WriteLine($"move of one plain shape (300 live shadows looked at): {plainMove:0.00} ms");
        Assert.Equal(rebuilds, s.LiveUpdater.Rebuilds);
        Assert.True(plainMove < 5, $"{plainMove:0.00} ms");
    }

    [Fact]
    public void Moving_all_the_lettering_with_its_shadows_rebuilds_nothing()
    {
        var (s, letters, _) = Sign();
        var tops = letters.Select(l => l.TopLevel).Distinct().ToList();
        int rebuilds = s.LiveUpdater.Rebuilds;
        double together = Best(5, () => { }, () => s.History.Execute(TransformObjectsCommand.ApplyWorld("Size", tops, Affine2.Scale(1.01, 1.01))));
        output.WriteLine($"scale 300 letter groups with their shadows: {together:0.00} ms");
        Assert.Equal(rebuilds, s.LiveUpdater.Rebuilds);

        // the letters alone (picked inside their groups): every shadow follows by a shift, nothing is recomputed
        int shifts = s.LiveUpdater.Shifts;
        double alone = Best(5, () => { }, () => s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", letters, Affine2.Translation(2, 1))));
        output.WriteLine($"move 300 letters alone, shadows shifted: {alone:0.00} ms");
        Assert.Equal(rebuilds, s.LiveUpdater.Rebuilds);
        Assert.True(s.LiveUpdater.Shifts >= shifts + 300);
        Assert.True(together < 15, $"{together:0.00} ms");
        Assert.True(alone < 40, $"{alone:0.00} ms");
    }

    [Fact]
    public void Changing_the_font_of_300_live_shadowed_letters_rebuilds_them_quickly()
    {
        var (s, letters, _) = Sign();
        var texts = letters.OfType<TextObject>().ToList();
        string[] fonts = ["Times New Roman", "Arial"];
        int k = 0;
        double fontChange = Best(3, () => { }, () => s.History.Execute(new ChangeTextFormatCommand(texts, new TextFormatChange(Family: fonts[k++ % 2]))));
        output.WriteLine($"Change Font on 300 letters, 300 shadows rebuilt: {fontChange:0.0} ms");
        Assert.True(fontChange < 800, $"{fontChange:0.0} ms");
        foreach (var e in LiveEffects.All(s.Document).Take(5))
            Assert.Equal(e.GetWorldBounds().Width, LiveEffects.Recompute(e, s.BooleanService, s.OffsetService)!.Transform(e.GetWorldTransform()).GetBounds().Width, 3);
    }

    [Fact]
    public void Retyping_a_line_of_big_lettering_rebuilds_its_soft_shadow_and_bevel_quickly()
    {
        var s = NewSession();
        var t = new TextObject("SALE TODAY ONLY", "Arial", 300) { Transform = Affine2.Translation(100, 400), Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, t));
        s.Selection.Set(t);
        s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 216));
        s.Selection.Set(t);
        s.ApplyBevel(new BevelSettings());
        Assert.Equal(3, LiveEffects.EffectsOf(s.Document, t).Count);                         // soft shadow and two crescents

        string[] words = ["SALE TODAY ONLY!", "SALE TODAY ONLY"];
        int k = 0;
        double retype = Best(4, () => { }, () => s.History.Execute(new SetTextCommand(t, words[k++ % 2])));
        output.WriteLine($"retype a 300 pt line with a live soft shadow and bevel: {retype:0.0} ms");
        Assert.True(retype < 600, $"{retype:0.0} ms");

        double move = Best(4, () => { }, () => s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [t.TopLevel], Affine2.Translation(3, 0))));
        output.WriteLine($"move it with its group (effects along, nothing rebuilt): {move:0.00} ms");
        Assert.True(move < 10, $"{move:0.00} ms");
    }
}
