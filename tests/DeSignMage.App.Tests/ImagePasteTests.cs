using Avalonia;
using Avalonia.Controls;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>A clipboard that lives in memory and behaves like the real one: setting our objects clears everything else.</summary>
internal sealed class FakeClipboard : ISystemClipboard
{
    public byte[]? Png { get; set; }
    public byte[]? Dib { get; set; }
    public List<string> Files { get; } = [];
    public string? Own { get; set; }
    public int OwnWrites { get; private set; }

    public ClipboardContent Available() =>
        (Png is null ? 0 : ClipboardContent.Png) | (Dib is null ? 0 : ClipboardContent.Dib) |
        (Files.Count == 0 ? 0 : ClipboardContent.Files) | (Own is null ? 0 : ClipboardContent.Own);

    public string? OwnToken() => Own;
    public byte[]? GetPng() => Png;
    public byte[]? GetDib() => Dib;
    public IReadOnlyList<string> GetFiles() => Files.ToList();

    public void SetOwn(string token, ClipboardPicture? picture)
    {
        OwnWrites++;
        Clear();
        Own = token;
        Png = picture?.Png;
        Dib = picture is { DibV5.Length: > 0 } ? picture.DibV5 : null;
    }

    public void Clear() { Png = Dib = null; Files.Clear(); Own = null; }

    /// <summary>Something copied in another program: it replaces everything.</summary>
    public void CopyElsewhere(byte[]? png = null, byte[]? dib = null, params string[] files)
    {
        Clear();
        Png = png; Dib = dib; Files.AddRange(files);
    }
}

/// <summary>Edit ▸ Paste / Ctrl+V with pictures from the system clipboard, and Ctrl+C offering a picture to other programs.</summary>
public class ImagePasteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-paste-tests-" + Guid.NewGuid().ToString("N"));

    public ImagePasteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (MainWindow Window, FakeClipboard Clip) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        var clip = new FakeClipboard();
        w.SystemClipboard = clip;
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, clip);
    }

    private static PixelBuffer Quadrants(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < w / 2 && y < h / 2) p.Set(x, y, 255, 0, 0); else p.Set(x, y, 0, 0, 255);
        return p;
    }

    private static byte[] QuadrantPng(int w = 192, int h = 96) => ImageCodec.EncodePng(Quadrants(w, h));

    /// <summary>The 24-bit DIB of <see cref="Quadrants"/> (bottom row first), what a program that offers only a bitmap puts on the clipboard.</summary>
    private static byte[] QuadrantDib(int w, int h)
    {
        var src = Quadrants(w, h);
        int stride = (w * 3 + 3) / 4 * 4;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, _) = src.Get(x, y);
                int o = 40 + (h - 1 - y) * stride + x * 3;
                dib[o] = b; dib[o + 1] = g; dib[o + 2] = r;
            }
        return dib;
    }

    private static void CtrlV(MainWindow w) => w.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
    private static void CtrlC(MainWindow w) => w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");

    /// <summary>Ctrl+V and wait until the paste hangs on the cursor (a paste never adds anything by itself).</summary>
    private static async Task PasteStart(MainWindow w)
    {
        CtrlV(w);
        await w.ClipboardIdle;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Session.IsPlacing, "the paste should be hanging on the cursor");
    }

    /// <summary>A left click at a document point, as the editor receives it (the canvas turns mouse events into exactly this).</summary>
    private static void Place(MainWindow w, Vec2 doc)
    {
        var screen = w.Session.Viewport.ToScreen(doc);
        w.Session.PointerMove(new PointerInput(screen, doc, PointerButton.None, ModifierKeys.None));
        w.Session.PointerDown(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        w.Session.PointerUp(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Paste, then click at the middle of the view.</summary>
    private static async Task<ImageObject> PasteOne(MainWindow w)
    {
        await PasteStart(w);
        Place(w, w.Session.Viewport.VisibleDocRect.Center);
        return Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));
    }

    private static PixelBuffer Pixels(MainWindow w, ImageObject img) => ImageCodec.Decode(w.Session.Document.Assets.Get(img.AssetId)!.Data);

    // ---- pictures ----

    [AvaloniaFact]
    public async Task A_PNG_is_pasted_centered_in_the_view_at_screen_size_and_undoes_in_one_step()
    {
        var (w, clip) = Open();
        w.Session.Viewport.ZoomToFit(Rect2.FromXYWH(200, 100, 600, 400));      // look at a part of the page
        var view = w.Session.Viewport.VisibleDocRect.Center;
        clip.CopyElsewhere(png: QuadrantPng(192, 96));                        // no resolution in it: 96 dpi = 2 x 1 inch

        var img = await PasteOne(w);

        Assert.Equal((144.0, 72.0), (img.Width, img.Height));
        Assert.Equal((192, 96), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(view.X, img.GetWorldBounds().Center.X, 3);
        Assert.Equal(view.Y, img.GetWorldBounds().Center.Y, 3);
        Assert.Equal("Pasted Picture", img.Name);
        Assert.Null(img.SourceName);
        Assert.Equal((255, 0, 0, 255), Pixels(w, img).Get(5, 5));
        Assert.Equal("Paste Picture", w.Session.History.UndoDescription);

        w.Session.Undo();
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_bigger_than_the_view_is_shrunk_to_fit_it_and_the_page()
    {
        var (w, clip) = Open();
        w.Session.Viewport.ZoomToFit(Rect2.FromXYWH(200, 100, 600, 400));
        var view = w.Session.Viewport.VisibleDocRect;
        clip.CopyElsewhere(png: QuadrantPng(4000, 2000));                     // 41 x 21 inches at 96 dpi

        var img = await PasteOne(w);

        var b = img.GetWorldBounds();
        Assert.True(b.Width <= view.Width * 0.9 + 1e-6 && b.Height <= view.Height * 0.9 + 1e-6, "fits the view");
        Assert.True(b.Width <= w.Session.Document.Page.Width + 1e-6 && b.Height <= w.Session.Document.Page.Height + 1e-6, "fits the page");
        Assert.Equal(2.0, b.Width / b.Height, 4);
        Assert.Equal(view.Center.X, b.Center.X, 3);
        Assert.Equal((4000, 2000), (img.PixelWidth, img.PixelHeight));
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_bitmap_without_PNG_is_pasted_the_right_way_up()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(dib: QuadrantDib(40, 20));

        var img = await PasteOne(w);

        var px = Pixels(w, img);
        Assert.Equal((40, 20), (px.Width, px.Height));
        Assert.Equal((255, 0, 0, 255), px.Get(2, 2));          // red quadrant at the top-left
        Assert.Equal((0, 0, 255, 255), px.Get(30, 2));
        Assert.Equal((0, 0, 255, 255), px.Get(2, 15));
        w.Close();
    }

    [AvaloniaFact]
    public async Task PNG_is_preferred_over_the_bitmap_form_of_the_same_clipboard()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: ImageCodec.EncodePng(new PixelBuffer(30, 10)), dib: QuadrantDib(40, 20));
        var img = await PasteOne(w);
        Assert.Equal((30, 10), (img.PixelWidth, img.PixelHeight));
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_unreadable_PNG_falls_back_to_the_bitmap()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: new byte[300], dib: QuadrantDib(40, 20));
        var img = await PasteOne(w);
        Assert.Equal((40, 20), (img.PixelWidth, img.PixelHeight));
        Assert.Null(w.LastPasteError);
        w.Close();
    }

    [AvaloniaFact]
    public async Task When_nothing_on_the_clipboard_is_readable_nothing_is_placed_and_the_user_is_told()
    {
        var (w, clip) = Open();
        var shown = new List<string>();
        w.DialogOverride = (_, text) => { shown.Add(text); return Task.FromResult(true); };
        clip.CopyElsewhere(png: new byte[300], dib: new byte[10]);

        CtrlV(w);
        await w.ClipboardIdle;

        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Assert.Empty(w.Session.History.UndoList);
        Assert.NotNull(w.LastPasteError);
        Assert.Single(shown);
        w.Close();
    }

    // ---- files ----

    [AvaloniaFact]
    public async Task Files_copied_in_Explorer_are_imported_like_File_Import()
    {
        var (w, clip) = Open();
        string a = Path.Combine(_dir, "logo.png"), b = Path.Combine(_dir, "photo.png"), text = Path.Combine(_dir, "notes.txt");
        File.WriteAllBytes(a, QuadrantPng(300, 150));
        File.WriteAllBytes(b, QuadrantPng(60, 60));
        File.WriteAllText(text, "not a picture");
        clip.CopyElsewhere(files: [text, a, b]);

        await PasteStart(w);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);                 // hanging on the cursor, not added yet
        Place(w, new Vec2(600, 300));

        Assert.Equal(["logo.png", "photo.png"], w.Session.Document.ActiveLayer.Children.Select(o => o.Name!).ToArray());
        var logo = (ImageObject)w.Session.Document.ActiveLayer.Children[0];
        Assert.Equal("logo.png", logo.SourceName);
        Assert.Equal(300, logo.EffectiveDpiX, 3);                              // a file without resolution: the import default
        Assert.Equal(2, w.Session.Selection.Count);
        var union = w.Session.Selection.GetBounds();
        Assert.Equal(600, union.Center.X, 3);                                  // both together are centered on the click
        Assert.Equal(300, union.Center.Y, 3);
        Assert.Single(w.Session.History.UndoList);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Files_that_are_not_pictures_paste_nothing()
    {
        var (w, clip) = Open();
        string text = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(text, "x");
        clip.CopyElsewhere(files: text);
        CtrlV(w);
        await w.ClipboardIdle;
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    // ---- objects copied here come first ----

    [AvaloniaFact]
    public async Task Objects_copied_here_are_pasted_while_the_clipboard_is_still_theirs()
    {
        var (w, clip) = Open();
        var rect = new RectangleObject(50, 30) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Set(rect);

        CtrlC(w);
        Assert.Equal(w.Session.ClipboardToken.ToString(), clip.Own);
        Assert.Null(clip.Png);                                                 // a shape has no picture to offer

        CtrlV(w);
        await w.ClipboardIdle;
        Assert.True(w.Session.IsPlacing);                                      // our own objects hang on the cursor too
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        Place(w, new Vec2(400, 300));
        var pasted = Assert.IsType<RectangleObject>(Assert.Single(w.Session.Selection.Items));
        Assert.NotSame(rect, pasted);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        Assert.Equal(400, pasted.GetWorldBounds().Center.X, 3);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Something_copied_elsewhere_later_wins_over_the_older_objects()
    {
        var (w, clip) = Open();
        var rect = new RectangleObject(50, 30) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Set(rect);
        CtrlC(w);

        clip.CopyElsewhere(png: QuadrantPng());                               // a screenshot taken after the copy
        var img = await PasteOne(w);

        Assert.Equal("Pasted Picture", img.Name);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);        // the rectangle and the picture; not a second rectangle
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_copied_picture_pastes_as_the_object_not_as_a_new_picture_from_its_own_PNG()
    {
        var (w, clip) = Open();
        w.Session.Viewport.ZoomToFit(Rect2.FromXYWH(0, 0, 900, 600));
        clip.CopyElsewhere(png: QuadrantPng());
        var original = await PasteOne(w);
        original.Name = "Original";
        w.Session.Selection.Set(original);

        CtrlC(w);
        await w.ClipboardIdle;
        Assert.NotNull(clip.Png);                                              // offered to other programs too
        await PasteStart(w);
        Place(w, original.GetWorldBounds().Center);                            // put down exactly over the original

        var copy = Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));
        Assert.NotSame(original, copy);
        Assert.Equal("Original", copy.Name);                                   // the object itself, not a new picture
        Assert.Equal(original.Transform.OffsetX, copy.Transform.OffsetX, 6);
        Assert.Equal(original.Transform.OffsetY, copy.Transform.OffsetY, 6);
        Assert.Equal(original.AssetId, copy.AssetId);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_foreign_tag_does_not_hide_a_picture()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng());
        clip.Own = Guid.NewGuid().ToString();                                  // objects copied in another window of this program
        var img = await PasteOne(w);
        Assert.Equal("Pasted Picture", img.Name);
        w.Close();
    }

    [AvaloniaFact]
    public async Task With_nothing_usable_on_the_system_clipboard_the_in_app_copy_still_pastes()
    {
        var (w, clip) = Open();
        var rect = new RectangleObject(50, 30) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Set(rect);
        CtrlC(w);
        clip.Clear();                                                          // e.g. text copied elsewhere, or the clipboard was busy

        await PasteStart(w);
        Place(w, new Vec2(300, 300));

        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        w.Close();
    }

    // ---- placing with the real mouse ----

    private static void ScreenIs(MainWindow w, PlaceTool tool, double x, double y)
    {
        var s = w.Session.Viewport.ToScreen(tool.TargetCenter!.Value);
        Assert.Equal(x, s.X, 3);
        Assert.Equal(y, s.Y, 3);
    }

    private static Avalonia.Point P(MainWindow w, double canvasX, double canvasY)
    {
        var canvas = w.FindControl<DeSignMage.App.Canvas.DesignCanvas>("Surface")!;
        return canvas.TranslatePoint(new Avalonia.Point(canvasX, canvasY), TopLevel.GetTopLevel(canvas)!).GetValueOrDefault();
    }

    [AvaloniaFact]
    public async Task A_pasted_picture_follows_the_mouse_and_the_next_left_click_places_it_there()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng(192, 96));
        w.MouseMove(P(w, 300, 250));                                           // the pointer is over the canvas when Ctrl+V is pressed

        CtrlV(w);
        await w.ClipboardIdle;
        Dispatcher.UIThread.RunJobs();

        var tool = w.Session.GetTool<PlaceTool>();
        Assert.True(w.Session.IsPlacing);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Assert.Empty(w.Session.History.UndoList);
        ScreenIs(w, tool, 300, 250);
        Assert.Contains("Click to place", w.Session.Hint);

        w.MouseMove(P(w, 450, 320));                                           // it follows
        ScreenIs(w, tool, 450, 320);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);

        w.MouseDown(P(w, 480, 340), MouseButton.Left);                         // the click puts it down
        w.MouseUp(P(w, 480, 340), MouseButton.Left);

        var img = Assert.IsType<ImageObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        var expected = w.Session.Viewport.ToDoc(new Vec2(480, 340));
        Assert.Equal(expected.X, img.GetWorldBounds().Center.X, 3);
        Assert.Equal(expected.Y, img.GetWorldBounds().Center.Y, 3);
        Assert.Single(w.Session.History.UndoList);
        Assert.Equal("Paste Picture", w.Session.History.UndoDescription);
        Assert.False(w.Session.IsPlacing);
        Assert.Same(img, Assert.Single(w.Session.Selection.Items));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Escape_cancels_a_paste_and_nothing_is_added()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng());
        await PasteStart(w);

        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(w.Session.IsPlacing);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Assert.Empty(w.Session.History.UndoList);
        w.MouseDown(P(w, 400, 300), MouseButton.Left);                         // a later click does not bring it back
        w.MouseUp(P(w, 400, 300), MouseButton.Left);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Right_button_zoom_during_a_paste_does_not_place_it()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng());
        w.MouseMove(P(w, 300, 250));
        await PasteStart(w);
        double zoom = w.Session.Viewport.Zoom;

        w.MouseDown(P(w, 200, 200), MouseButton.Right);                        // right-drag a zoom box
        w.MouseMove(P(w, 260, 230));
        w.MouseMove(P(w, 330, 280));
        w.MouseUp(P(w, 330, 280), MouseButton.Right);
        Assert.True(w.Session.Viewport.Zoom > zoom);
        Assert.True(w.Session.IsPlacing);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);

        w.MouseDown(P(w, 330, 280), MouseButton.Right);                        // right-click: back out
        w.MouseUp(P(w, 330, 280), MouseButton.Right);
        Assert.True(w.Session.IsPlacing);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Assert.Empty(w.Session.History.UndoList);

        w.MouseDown(P(w, 400, 300), MouseButton.Left);                         // only a left click places it
        w.MouseUp(P(w, 400, 300), MouseButton.Left);
        Assert.IsType<ImageObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Pasting_from_the_menu_with_the_mouse_elsewhere_starts_at_the_view_center_and_follows_once_it_enters()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng());
        var center = w.Session.Viewport.VisibleDocRect.Center;

        await PasteStart(w);                                                   // no mouse event over the canvas yet
        var tool = w.Session.GetTool<PlaceTool>();
        Assert.Equal(center.X, tool.TargetCenter!.Value.X, 3);
        Assert.Equal(center.Y, tool.TargetCenter!.Value.Y, 3);

        w.MouseMove(P(w, 150, 120));
        ScreenIs(w, tool, 150, 120);
        w.Close();
    }

    // ---- the menu ----

    [AvaloniaFact]
    public void Paste_is_disabled_without_anything_to_paste_and_enabled_with_a_picture()
    {
        var (w, clip) = Open();
        var edit = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Edit");
        var paste = (AppCommand)edit.Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand { Id: "edit.paste" }).Tag!;

        Assert.False(paste.CanExecute());
        clip.CopyElsewhere(png: QuadrantPng());
        Assert.True(paste.CanExecute());
        clip.CopyElsewhere(dib: QuadrantDib(4, 4));
        Assert.True(paste.CanExecute());
        clip.CopyElsewhere(files: Path.Combine(_dir, "x.png"));
        Assert.True(paste.CanExecute());
        clip.Clear();
        Assert.False(paste.CanExecute());
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_menu_command_pastes_too()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng());
        var edit = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Edit");
        var paste = (AppCommand)edit.Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand { Id: "edit.paste" }).Tag!;
        paste.Execute();
        await w.ClipboardIdle;
        Assert.True(w.Session.IsPlacing);
        Place(w, new Vec2(300, 300));
        Assert.IsType<ImageObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        w.Close();
    }

    // ---- copying a picture out ----

    [AvaloniaFact]
    public async Task Copying_a_picture_offers_PNG_and_a_bitmap_to_other_programs()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng(60, 40));
        var img = await PasteOne(w);

        CtrlC(w);
        await w.ClipboardIdle;

        Assert.NotNull(clip.Png);
        Assert.Equal((60, 40), (ImageCodec.Probe(clip.Png!).Width, ImageCodec.Probe(clip.Png!).Height));
        Assert.NotNull(clip.Dib);
        var back = ClipboardImages.DibToPixels(clip.Dib!);
        Assert.Equal((255, 0, 0, 255), back.Get(3, 3));
        Assert.Equal((0, 0, 255, 255), back.Get(50, 35));
        Assert.Equal(w.Session.ClipboardToken.ToString(), clip.Own);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_copied_in_another_format_is_offered_as_PNG()
    {
        var (w, clip) = Open();
        // A JPEG is stored as it is; copying it out converts.
        var pixels = Quadrants(20, 10);
        var asset = w.Session.Document.Assets.Add(CodecBytes.Jpeg(pixels), "image/jpeg");
        var img = new ImageObject(asset, 60, 30, 20, 10);
        w.Session.PlaceImages([img]);
        w.Session.Selection.Set(img);

        CtrlC(w);
        await w.ClipboardIdle;

        Assert.Equal("image/png", ImageCodec.Probe(clip.Png!).MediaType);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_copy_made_before_another_copy_does_not_overwrite_the_newer_clipboard()
    {
        var (w, clip) = Open();
        clip.CopyElsewhere(png: QuadrantPng(60, 40));
        await PasteOne(w);
        CtrlC(w);                                                              // starts converting the picture ...
        clip.CopyElsewhere(png: QuadrantPng(8, 8));                            // ... but something else is copied right away
        await w.ClipboardIdle;
        Assert.Null(clip.Own);
        Assert.Equal(8, ImageCodec.Probe(clip.Png!).Width);
        w.Close();
    }
}

internal static class CodecBytes
{
    public static byte[] Jpeg(PixelBuffer p)
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(p.Width, p.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(p.Rgba, 0, bmp.GetPixels(), p.Rgba.Length);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
