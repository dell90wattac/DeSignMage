using DeSignMage.Core.Model;
using DeSignMage.Formats.Vector;
using DeSignMage.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>Real files from <c>local-testdata/vector</c> (not in the repository): read, rendered to a picture for looking at, and checked for the basics.</summary>
public class VectorSampleTests(ITestOutputHelper output)
{
    private static string Sample(string name) => LocalData.Under("vector", name);

    private void Render(VectorImportResult r, string name)
    {
        var doc = r.Document;
        double scale = Math.Min(1200 / doc.Page.Width, 800 / doc.Page.Height);
        using var bmp = LytRenderTests.Render(doc, doc.Page.Bounds, scale);
        LytRenderTests.Save(bmp, name);
    }

    [LocalTheory("vector/hah1.pdf", "vector/hah2.pdf", "vector/hah2-white.pdf")]
    [InlineData("hah1.pdf", 15)]
    [InlineData("hah2.pdf", 15)]
    [InlineData("hah2-white.pdf", 2)]
    public void A_logo_pdf_comes_in_as_its_shapes(string file, int shapes)
    {
        var r = VectorImport.Read(Sample(file));
        output.WriteLine($"{file}: {r.ObjectCount} objects, {r.Document.GroupCount} groups, page {r.Document.Page.Width} x {r.Document.Page.Height}; warnings: {string.Join(" | ", r.Warnings)}");
        Render(r, "vector-" + Path.GetFileNameWithoutExtension(file) + ".png");
        Assert.Equal(VectorFormat.Pdf, r.Format);
        Assert.Equal(1685.0 * (file == "hah2-white.pdf" ? 1401.0 / 1685 : 1), r.Document.Page.Width, 0);
        Assert.Equal(shapes, r.ObjectCount);
        Assert.All(r.Document.Layers.SelectMany(l => l.Children), o => Assert.IsType<PathObject>(o));
        var content = r.Document.GetContentBounds();
        Assert.True(content.Left >= -1 && content.Top >= -1 && content.Right <= r.Document.Page.Width + 1 && content.Bottom <= r.Document.Page.Height + 1, $"content {content}");
    }

    [LocalFact("vector/batik-document.pdf")]
    public void A_pdf_with_text_clips_and_a_picture_comes_in_with_all_of_them()
    {
        var r = VectorImport.Read(Sample("batik-document.pdf"), new VectorImportOptions { BooleanService = WriterTestKit.Booleans });
        output.WriteLine($"{r.ObjectCount} objects, {r.Document.GroupCount} groups; warnings: {string.Join(" | ", r.Warnings)}");
        Render(r, "vector-batik-document.png");
        var all = r.Document.Layers.SelectMany(l => l.Children).ToList();
        Assert.NotEmpty(all.OfType<TextObject>());
        Assert.Single(all.OfType<ImageObject>());
        Assert.NotEmpty(all.OfType<PathObject>());
    }

    [LocalTheory("vector/logo.svg", "vector/gradient.svg")]
    [InlineData("logo.svg")]
    [InlineData("gradient.svg")]
    public void Svg_files_from_the_vectorizer_come_in(string file)
    {
        var r = VectorImport.Read(Sample(file));
        output.WriteLine($"{file}: {r.ObjectCount} objects, {r.Document.GroupCount} groups; page {r.Document.Page.Width} x {r.Document.Page.Height}; warnings: {string.Join(" | ", r.Warnings)}");
        Render(r, "vector-" + Path.GetFileNameWithoutExtension(file) + ".png");
        Assert.True(r.ObjectCount > 0);
    }
}
