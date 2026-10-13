using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Writing every format of File ▸ Export through the window (the part after the dialog): the files, their sizes, and what the legacy layout had to simplify.</summary>
public class ExportFormatsShellTests
{
    private static MainWindow Show(StartupOptions? options = null)
    {
        var w = new MainWindow(options ?? new StartupOptions(Demo: true)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.DialogOverride = (_, _) => Task.FromResult(true);
        return w;
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("dsm-formats-").FullName;

    [AvaloniaTheory]
    [InlineData(ExportFormat.Png, "a.png")]
    [InlineData(ExportFormat.Jpeg, "a.jpg")]
    [InlineData(ExportFormat.Pdf, "a.pdf")]
    [InlineData(ExportFormat.Svg, "a.svg")]
    [InlineData(ExportFormat.Eps, "a.eps")]
    [InlineData(ExportFormat.Ai, "a.ai")]
    [InlineData(ExportFormat.Lyt, "a.lyt")]
    public void Every_format_of_the_dialog_is_written_to_a_file_of_that_kind(ExportFormat format, string name)
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            var request = new ExportRequest(format, new ExportOptions { PixelWidth = 640 });

            Assert.True(w.RunExport(request, Path.Combine(dir, Path.GetFileNameWithoutExtension(name))));   // the extension is added
            var path = Path.Combine(dir, name);
            Assert.True(File.Exists(path));
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 300);

            switch (format)
            {
                case ExportFormat.Png:
                    using (var bmp = SKBitmap.Decode(bytes)) Assert.Equal((640, 320), (bmp.Width, bmp.Height));
                    break;
                case ExportFormat.Jpeg:
                    using (var bmp = SKBitmap.Decode(bytes)) Assert.Equal((640, 320), (bmp.Width, bmp.Height));
                    break;
                case ExportFormat.Pdf or ExportFormat.Ai:
                    Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 8));
                    break;
                case ExportFormat.Svg:
                    Assert.Contains("<svg", Encoding.UTF8.GetString(bytes));
                    break;
                case ExportFormat.Eps:
                    Assert.StartsWith("%!PS-Adobe-3.0 EPSF-3.0", Encoding.ASCII.GetString(bytes, 0, 30));
                    break;
                case ExportFormat.Lyt:
                    var imported = LytImporter.Open(path);       // the writer's file reads back
                    Assert.True(imported.Document.ObjectCount > 0);
                    Assert.Equal(w.Session.Document.Page.Width, imported.Document.Page.Width, 1);
                    break;
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void The_picture_formats_are_written_exactly_as_typed_and_the_vector_formats_ignore_a_pixel_size()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            Assert.True(w.RunExport(new ExportRequest(ExportFormat.Png, new ExportOptions { PixelWidth = 3200, PixelHeight = 1600, PngCompression = 9 }), Path.Combine(dir, "big.png")));
            using (var bmp = SKBitmap.Decode(Path.Combine(dir, "big.png"))) Assert.Equal((3200, 1600), (bmp.Width, bmp.Height));
            Assert.True(w.RunExport(new ExportRequest(ExportFormat.Jpeg, new ExportOptions { PixelHeight = 300, JpegQuality = 40 }), Path.Combine(dir, "small.jpg")));
            using (var bmp = SKBitmap.Decode(Path.Combine(dir, "small.jpg"))) Assert.Equal((600, 300), (bmp.Width, bmp.Height));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void The_legacy_layout_is_given_a_preview_picture_and_the_geometry_services_so_strokes_survive()
    {
        var dir = TempDir();
        try
        {
            var w = Show(new StartupOptions());
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, new RectangleObject(100, 60)
            {
                Transform = Affine2.Translation(50, 50),
                Style = new ObjectStyle(new SolidPaint(new DocColor(200, 30, 30)), StrokeStyle.Solid(DocColor.Black, 6)),
            }));
            var path = Path.Combine(dir, "layout.lyt");

            Assert.True(w.RunExport(new ExportRequest(ExportFormat.Lyt, new ExportOptions()), path));

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, bytes[..3]);                 // the layout starts with its small JPEG preview
            Assert.DoesNotContain(w.LastExportWarnings, m => m.Contains("stroke", StringComparison.OrdinalIgnoreCase));
            var back = LytImporter.Open(path).Document;
            Assert.True(back.AllObjects().Count() >= 2);                                 // the fill and the ring that stands for its stroke
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void What_the_legacy_layout_had_to_simplify_is_told_in_plain_words_after_the_export()
    {
        var dir = TempDir();
        try
        {
            var w = Show(new StartupOptions());
            var feathered = new EllipseObject(80, 80) { Transform = Affine2.Translation(50, 50), Style = new ObjectStyle(new SolidPaint(new DocColor(0, 120, 0)), null, null, new FeatherStyle(0.2, 0)) };
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, feathered));
            var shown = new List<(string Title, string Message)>();
            w.DialogOverride = (title, message) => { shown.Add((title, message)); return Task.FromResult(true); };

            Assert.True(w.RunExport(new ExportRequest(ExportFormat.Lyt, new ExportOptions()), Path.Combine(dir, "x.lyt")));

            Assert.Contains(w.LastExportWarnings, m => m.Contains("Feathered edges are not stored"));
            var (title, message) = Assert.Single(shown);
            Assert.Equal("Exported", title);
            Assert.Contains("x.lyt was written, but some things had to be simplified", message);
            Assert.Contains("• Feathered edges are not stored in a layout", message);

            // a plain export of the same design says nothing
            shown.Clear();
            Assert.True(w.RunExport(new ExportRequest(ExportFormat.Png, new ExportOptions { PixelWidth = 100 }), Path.Combine(dir, "x.png")));
            Assert.Empty(shown);
            Assert.Empty(w.LastExportWarnings);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task The_export_command_writes_the_chosen_format_and_adds_the_extension()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            w.ExportOverride = () => (new ExportRequest(ExportFormat.Eps, new ExportOptions()), Path.Combine(dir, "design"));

            await w.ExportAsync();

            Assert.True(File.Exists(Path.Combine(dir, "design.eps")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void The_file_picker_offers_the_right_type_for_each_format()
    {
        foreach (var f in ExportFormats.All)
        {
            Assert.EndsWith($"(*{f.Extension})", f.PickerLabel);
        }
    }
}
