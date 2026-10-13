using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Pictures in the real window: File ▸ Import, dropping files, the Image menu (rotate, flip, invert, crop, rasterize, 1:1) and Vectorize on a placed picture.</summary>
public class ImageShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-image-tests-" + Guid.NewGuid().ToString("N"));

    public ImageShellTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- helpers ----

    private static (MainWindow Window, DesignCanvas Canvas) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    private static string Strip(object? header) => ((string)header!).Replace("_", "");

    private static MenuItem TopMenu(MainWindow w, string name) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == name);

    private static AppCommand Cmd(MainWindow w, string menu, string header) =>
        (AppCommand)TopMenu(w, menu).Items.OfType<MenuItem>().Single(m => m.Tag is AppCommand && Strip(m.Header) == header).Tag!;

    private static void Run(MainWindow w, string menu, string header)
    {
        var c = Cmd(w, menu, header);
        Assert.True(c.CanExecute(), $"{menu} > {header} is disabled");
        c.Execute();
        Pump(() => true);
    }

    private static void Pump(Func<bool> done, int timeoutMs = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A PNG on disk: pixel (x, y) = f(x, y).</summary>
    private string WritePng(string name, int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> f)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y);
                p.Set(x, y, r, g, b, a);
            }
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, ImageCodec.EncodePng(p));
        return path;
    }

    private string WriteFlat(string name, int w, int h) => WritePng(name, w, h, (_, _) => (30, 120, 200, 255));

    /// <summary>Top-left quadrant red, the rest blue: tells every rotation and flip apart.</summary>
    private string WriteQuadrants(string name, int w, int h) =>
        WritePng(name, w, h, (x, y) => x < w / 2 && y < h / 2 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255));

    private static async Task<ImageObject> ImportOne(MainWindow w, string path)
    {
        Assert.Equal(1, await w.ImportImagesAsync([path], null));
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));
    }

    private static PixelBuffer Pixels(MainWindow w, ImageObject img) => ImageCodec.Decode(w.Session.Document.Assets.Get(img.AssetId)!.Data);

    private static Point P(DesignCanvas c, Vec2 screen) => c.TranslatePoint(new Point(screen.X, screen.Y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    private static TextBlock Info(MainWindow w) => w.FindControl<TextBlock>("InfoLine")!;

    // ---- the menu ----

    [AvaloniaFact]
    public void Image_menu_follows_SWs_layout_and_leaves_unbuilt_entries_disabled()
    {
        var (w, _) = Open();
        var items = TopMenu(w, "Image").Items.OfType<Control>().ToList();
        string Name(Control c) => c is MenuItem m ? Strip(m.Header) : "-";
        Assert.Equal(
        [
            "Vectorize...", "Crop Tool", "Rasterize...", "-", "Modify Pixels", "-",
            "Rotate 90 degrees clockwise", "Rotate 90 degrees counter-clockwise", "Flip Horizontal", "Flip Vertical", "Invert", "Commit Rotation", "1:1 Zoom", "-",
            "Color Depth", "Extract Alpha Channel", "-", "Save As...", "-",
            "Manage Image Fill Library...", "Update Image Fill Library", "Add to Image Fill Library..",
        ], items.Select(Name).ToArray());

        // Nothing selected: picture commands are off (Vectorize asks for a file, so it stays on).
        bool Enabled(string header) => Cmd(w, "Image", header).CanExecute();
        Assert.True(Enabled("Vectorize..."));
        foreach (var h in new[] { "Crop Tool", "Rasterize...", "Rotate 90 degrees clockwise", "Rotate 90 degrees counter-clockwise", "Flip Horizontal", "Flip Vertical", "Invert", "1:1 Zoom" })
            Assert.False(Enabled(h), h);
        // Never built yet: disabled whatever is selected.
        foreach (var h in new[] { "Commit Rotation", "Extract Alpha Channel", "Save As...", "Manage Image Fill Library...", "Update Image Fill Library", "Add to Image Fill Library.." })
            Assert.False(Enabled(h), h);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Selecting_a_picture_turns_its_commands_on_and_shortcuts_stay_unbound()
    {
        var (w, _) = Open();
        await ImportOne(w, WriteFlat("a.png", 60, 30));

        foreach (var h in new[] { "Crop Tool", "Rotate 90 degrees clockwise", "Rotate 90 degrees counter-clockwise", "Flip Horizontal", "Flip Vertical", "Invert", "1:1 Zoom" })
            Assert.True(Cmd(w, "Image", h).CanExecute(), h);
        Assert.False(Cmd(w, "Image", "Rasterize...").CanExecute());   // a picture alone has nothing to rasterize
        // Shortcuts (Alt+V, Ctrl+F7, Alt+1) wait for the user's approval.
        foreach (var h in new[] { "Vectorize...", "Crop Tool", "1:1 Zoom" })
            Assert.Empty(Cmd(w, "Image", h).Gestures);
        w.Close();
    }

    // ---- import ----

    [AvaloniaFact]
    public async Task Import_places_the_picture_centered_in_the_view_at_its_natural_size()
    {
        var (w, _) = Open();
        string path = WriteFlat("logo.png", 600, 300);                // no dpi in the file: 300 dpi = 2 x 1 inch
        w.PickImagesOverride = () => Task.FromResult<IReadOnlyList<string>>([path]);
        w.Session.Viewport.ZoomToFit(Rect2.FromXYWH(200, 100, 400, 300));   // view a part of the page
        var viewCenter = w.Session.Viewport.VisibleDocRect.Center;

        Cmd(w, "File", "Import...").Execute();
        Pump(() => w.Session.Document.ActiveLayer.Children.Count > 0);

        var img = Assert.IsType<ImageObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        Assert.Equal((144.0, 72.0), (img.Width, img.Height));
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal("logo.png", img.Name);
        Assert.Equal(300, img.EffectiveDpiX, 3);
        var c = img.GetWorldBounds().Center;
        Assert.Equal(viewCenter.X, c.X, 3);
        Assert.Equal(viewCenter.Y, c.Y, 3);
        Assert.Same(img, Assert.Single(w.Session.Selection.Items));
        Assert.Equal("Import Bitmap", w.Session.History.UndoDescription);

        w.Session.Undo();
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_picture_bigger_than_the_page_is_shrunk_to_fit_it()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteFlat("wide.png", 20000, 400));   // 66 inches at 300 dpi; the page is 24
        var page = w.Session.Document.Page;
        Assert.True(img.GetWorldBounds().Width <= page.Width + 1e-6);
        Assert.Equal(page.Width, img.GetWorldBounds().Width, 3);
        Assert.Equal(50.0, img.Width / img.Height, 3);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_info_line_counts_the_bitmaps_in_the_document()
    {
        var (w, _) = Open();
        Assert.Contains("Bitmaps: 0.00 MB total", Info(w).Text);
        await ImportOne(w, WriteFlat("a.png", 1024, 512));                 // 1024 x 512 x 4 bytes = 2 MB
        Pump(() => Info(w).Text!.Contains("2.00 MB"));
        Assert.Contains("Bitmaps: 2.00 MB total", Info(w).Text);
        w.Session.Undo();
        Pump(() => Info(w).Text!.Contains("0.00 MB"));
        Assert.Contains("Bitmaps: 0.00 MB total", Info(w).Text);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Several_files_are_one_undo_step_and_unreadable_ones_are_reported()
    {
        var (w, _) = Open();
        var shown = new List<string>();
        w.DialogOverride = (title, text) => { shown.Add(text); return Task.FromResult(true); };
        string a = WriteFlat("a.png", 50, 50), b = WriteFlat("b.png", 50, 50);
        string bad = Path.Combine(_dir, "bad.png");
        File.WriteAllBytes(bad, new byte[300]);
        string missing = Path.Combine(_dir, "missing.png");

        int placed = await w.ImportImagesAsync([a, bad, b, missing], null);

        Assert.Equal(2, placed);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        Assert.Equal(2, w.Session.Selection.Count);
        Assert.Single(w.Session.History.UndoList);
        Assert.NotNull(w.LastImportError);
        Assert.Contains("bad.png", w.LastImportError);
        Assert.Contains("missing.png", w.LastImportError);
        var boxes = w.Session.Document.ActiveLayer.Children.Select(o => o.GetWorldBounds()).ToList();
        Assert.NotEqual(boxes[0].Left, boxes[1].Left);                     // not stacked exactly on top of each other
        w.Session.Undo();
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public void Dropped_pictures_land_where_they_were_dropped()
    {
        var (w, _) = Open();
        string path = WriteFlat("drop.png", 300, 300);
        w.HandleDroppedFiles([path], new Vec2(400, 250));
        Pump(() => w.Session.Document.ActiveLayer.Children.Count > 0);
        var c = w.Session.Document.ActiveLayer.Children[0].GetWorldBounds().Center;
        Assert.Equal(400, c.X, 3);
        Assert.Equal(250, c.Y, 3);
        w.Close();
    }

    [AvaloniaFact]
    public void A_dropped_layout_still_opens_as_a_layout_even_with_pictures_along()
    {
        var (w, _) = Open();
        var asked = new List<string>();
        w.DialogOverride = (title, text) => { asked.Add(title); return Task.FromResult(true); };
        string lyt = Path.Combine(_dir, "nope.lyt");                       // does not exist: opening fails and says so
        string png = WriteFlat("p.png", 20, 20);

        w.HandleDroppedFiles([png, lyt], null);
        Pump(() => w.LastOpenError is not null);

        Assert.NotNull(w.LastOpenError);                                   // it went down the layout path
        Assert.Empty(w.Session.Document.ActiveLayer.Children);              // and the picture was not imported
        w.Close();
    }

    [AvaloniaFact]
    public void Other_dropped_files_get_the_usual_not_supported_message()
    {
        var (w, _) = Open();
        w.DialogOverride = (_, _) => Task.FromResult(true);
        string txt = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(txt, "hello");
        w.HandleDroppedFiles([txt], null);
        Pump(() => w.LastOpenError is not null);
        Assert.Contains("not supported", w.LastOpenError);
        w.Close();
    }

    // ---- rotate, flip, invert ----

    [AvaloniaFact]
    public async Task Rotate_clockwise_turns_the_pixels_and_the_frame_and_undoes()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 40, 20));      // red top-left quadrant
        var before = img.GetWorldBounds();

        Run(w, "Image", "Rotate 90 degrees clockwise");

        var px = Pixels(w, img);
        Assert.Equal((20, 40), (px.Width, px.Height));
        Assert.Equal((255, 0, 0), (px.Get(15, 5).R, px.Get(15, 5).G, px.Get(15, 5).B));   // red moved to the top-right
        Assert.Equal(255, px.Get(2, 5).B);                                               // top-left is blue now
        Assert.Equal((20, 40), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(before.Height, img.GetWorldBounds().Width, 6);                       // frame swapped, same center
        Assert.Equal(before.Center.X, img.GetWorldBounds().Center.X, 6);
        Assert.Equal(before.Center.Y, img.GetWorldBounds().Center.Y, 6);
        Assert.Equal("Rotate 90 Degrees Clockwise", w.Session.History.UndoDescription);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal((40, 20), (Pixels(w, img).Width, Pixels(w, img).Height));
        Assert.Equal(255, Pixels(w, img).Get(2, 2).R);
        Assert.Equal(before, img.GetWorldBounds());
        w.Close();
    }

    [AvaloniaFact]
    public async Task Rotate_counter_clockwise_flip_and_invert_change_the_pixels()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 40, 20));

        Run(w, "Image", "Rotate 90 degrees counter-clockwise");
        Assert.Equal((20, 40), (Pixels(w, img).Width, Pixels(w, img).Height));
        Assert.Equal(255, Pixels(w, img).Get(2, 25).R);                        // red quadrant moved to the bottom-left
        Assert.Equal(255, Pixels(w, img).Get(15, 5).B);                         // and the top-right is blue
        w.Session.Undo();

        Run(w, "Image", "Flip Horizontal");
        Assert.Equal(255, Pixels(w, img).Get(35, 2).R);                        // red now top-right
        w.Session.Undo();

        Run(w, "Image", "Flip Vertical");
        Assert.Equal(255, Pixels(w, img).Get(2, 17).R);                        // red now bottom-left
        w.Session.Undo();

        Run(w, "Image", "Invert");
        var inverted = Pixels(w, img).Get(2, 2);
        Assert.Equal((0, 255, 255), (inverted.R, inverted.G, inverted.B));     // red inverts to cyan
        Assert.Equal(255, inverted.A);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_image_row_shows_the_dpi_and_its_buttons_work()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 600, 300));
        Pump(() => true);
        var bar = Assert.IsType<SelectionContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        var row = bar.GetLogicalDescendants().OfType<ImageContextRow>().Single();
        Assert.True(row.IsVisible);
        var label = row.Children.OfType<TextBlock>().Single().Text!;
        Assert.Contains("600 x 300 px", label);
        Assert.Contains("300 dpi", label);

        var button = row.Children.OfType<Button>().Single(b => (string)b.Content! == "Flip H");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Flip Horizontal", w.Session.History.UndoDescription);
        w.Close();
    }

    // ---- saving ----

    [AvaloniaFact]
    public async Task Edited_pictures_survive_save_and_reopen_with_their_pixels_and_file_name()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 40, 20));
        Run(w, "Image", "Rotate 90 degrees clockwise");
        Run(w, "Image", "Invert");

        var bytes = DeSignMage.Formats.Native.DsmWriter.SaveToBytes(w.Session.Document);
        var back = DeSignMage.Formats.Native.DsmReader.Load(new MemoryStream(bytes)).Document.AllObjects().OfType<ImageObject>().Single();

        Assert.Equal((20, 40), (back.PixelWidth, back.PixelHeight));
        Assert.Equal("q.png", back.SourceName);
        Assert.Equal(img.Width, back.Width, 6);
        Assert.Equal(img.Height, back.Height, 6);
        Assert.Equal(img.Transform, back.Transform);
        var saved = ImageCodec.Decode(DeSignMage.Formats.Native.DsmReader.Load(new MemoryStream(bytes)).Document.Assets.Get(back.AssetId)!.Data);
        var original = Pixels(w, img);
        Assert.True(saved.Rgba.AsSpan().SequenceEqual(original.Rgba));
        Assert.Contains(img.AssetId, DeSignMage.Core.Imaging.BitmapStats.ReferencedAssetIds(w.Session.Document));
        Assert.True(w.Session.Document.Assets.All.Count() > DeSignMage.Core.Imaging.BitmapStats.ReferencedAssetIds(w.Session.Document).Count);   // replaced pictures stay for undo
        w.Close();
    }

    // ---- crop ----

    [AvaloniaFact]
    public async Task Crop_with_the_mouse_and_Enter_cuts_the_picture_and_undoes()
    {
        var (w, c) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 400, 200));    // 2 x 1 inch... 400 px at 300 dpi
        // Zoom in on the picture so a drag is easy to make.
        w.Session.ZoomToSelection();
        Run(w, "Image", "Crop Tool");
        Assert.IsType<CropTool>(w.Session.ActiveTool);
        Assert.IsType<CropContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);

        var b = img.GetWorldBounds();
        var tl = w.Session.Viewport.ToScreen(new Vec2(b.Left + b.Width * 0.25, b.Top + b.Height * 0.25));
        var br = w.Session.Viewport.ToScreen(new Vec2(b.Left + b.Width * 0.75, b.Top + b.Height * 0.75));
        w.MouseDown(P(c, tl), MouseButton.Left);
        w.MouseMove(P(c, (tl + br) / 2));
        w.MouseMove(P(c, br));
        w.MouseUp(P(c, br), MouseButton.Left);
        Assert.Equal((100, 50, 200, 100), w.Session.GetTool<CropTool>().BoxPixels);

        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        Assert.IsType<SelectTool>(w.Session.ActiveTool);
        Assert.Equal((200, 100), (img.PixelWidth, img.PixelHeight));
        Assert.Equal((200, 100), (Pixels(w, img).Width, Pixels(w, img).Height));
        Assert.Equal(b.Width / 2, img.GetWorldBounds().Width, 3);
        Assert.Equal(b.Left + b.Width / 4, img.GetWorldBounds().Left, 2);   // the kept part has not moved
        Assert.Equal(255, Pixels(w, img).Get(10, 10).R);                    // we cut out the middle: the red quadrant's corner survives
        Assert.Equal(300, img.EffectiveDpiX, 3);                              // cropping keeps the resolution
        Assert.Equal("Crop Bitmap", w.Session.History.UndoDescription);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Equal((400, 200), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(b, img.GetWorldBounds());
        w.Close();
    }

    [AvaloniaFact]
    public async Task Crop_check_mark_applies_and_Escape_cancels()
    {
        var (w, c) = Open();
        var img = await ImportOne(w, WriteQuadrants("q.png", 400, 200));
        w.Session.ZoomToSelection();
        Run(w, "Image", "Crop Tool");
        var b = img.GetWorldBounds();
        void DragBox()
        {
            var tl = w.Session.Viewport.ToScreen(new Vec2(b.Left + 10, b.Top + 10));
            var br = w.Session.Viewport.ToScreen(new Vec2(b.Left + b.Width / 2, b.Top + b.Height / 2));
            w.MouseDown(P(c, tl), MouseButton.Left);
            w.MouseMove(P(c, (tl + br) / 2));
            w.MouseMove(P(c, br));
            w.MouseUp(P(c, br), MouseButton.Left);
        }

        DragBox();
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsType<SelectTool>(w.Session.ActiveTool);
        Assert.Equal(400, img.PixelWidth);

        Run(w, "Image", "Crop Tool");
        DragBox();
        var bar = Assert.IsType<CropContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
        var apply = bar.GetLogicalDescendants().OfType<Button>().Single(x => ((string)x.Content!).Contains("Crop"));
        Assert.True(apply.IsEnabled);
        apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(img.PixelWidth < 400);
        Assert.IsType<SelectTool>(w.Session.ActiveTool);
        w.Close();
    }

    // ---- 1:1, rasterize ----

    [AvaloniaFact]
    public async Task One_to_one_zoom_shows_each_pixel_once()
    {
        var (w, _) = Open();
        var img = await ImportOne(w, WriteFlat("a.png", 600, 300));
        w.Session.ZoomToPage();
        Run(w, "Image", "1:1 Zoom");
        // One picture pixel per screen pixel (the headless window has scale 1): 600 px across 144 pt.
        Assert.Equal(600.0 / 144 / w.RenderScaling, w.Session.Viewport.Zoom, 3);
        Assert.Equal(1, w.Session.Viewport.ZoomStackDepth);
        w.Close();
    }

    [AvaloniaFact]
    public void Rasterize_is_switched_off_and_nothing_in_the_UI_reaches_it()
    {
        var (w, _) = Open();
        var rect = new RectangleObject(144, 72) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#00AA00")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
        w.Session.Selection.Set(rect);

        // Greyed in the Image menu even with vectors selected (the session code stays, tested in the Core tests).
        Assert.False(Cmd(w, "Image", "Rasterize...").CanExecute());
        Assert.Contains(w.Session.Document.ActiveLayer.Children, o => ReferenceEquals(o, rect));

        // No other entry point: the picture's double-click menu and its Context Bar have no Rasterize.
        var img = new ImageObject("x", 10, 10, 5, 5);
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, img));
        w.Session.Selection.Set(img);
        var bar = w.FindControl<ContentControl>("ContextBarHost")!.Content as Control;
        Assert.DoesNotContain(bar!.GetLogicalDescendants().OfType<Button>(), b => (b.Content as string ?? (b.Content as TextBlock)?.Text ?? "").Contains("Raster"));
        w.Close();
    }

    // ---- selecting, moving, grouping like any object ----

    [AvaloniaFact]
    public async Task Pictures_select_move_and_scale_with_the_mouse_like_other_objects()
    {
        var (w, c) = Open();
        var img = await ImportOne(w, WriteFlat("a.png", 600, 300));
        w.Session.Selection.Clear();
        w.Session.ZoomToPage();
        var before = img.GetWorldBounds().Center;
        var center = w.Session.Viewport.ToScreen(before);

        w.MouseDown(P(c, center), MouseButton.Left);
        w.MouseMove(P(c, center + new Vec2(20, 0)));
        w.MouseMove(P(c, center + new Vec2(40, 0)));
        w.MouseUp(P(c, center + new Vec2(40, 0)), MouseButton.Left);

        Assert.Same(img, Assert.Single(w.Session.Selection.Items));        // a click selects it (anywhere inside the frame)
        Assert.Equal(before.X + 40 / w.Session.Viewport.Zoom, img.GetWorldBounds().Center.X, 3);
        Assert.Equal("Move", w.Session.History.UndoDescription);
        Assert.Equal(600, img.PixelWidth);                                  // moving never changes pixels
        w.Close();
    }

    // ---- Vectorize on a placed picture ----

    [AvaloniaFact]
    public async Task Vectorize_uses_the_selected_picture_in_place_without_asking_for_a_file()
    {
        var (w, _) = Open();
        // A white picture with a black square: one shape to find.
        var img = await ImportOne(w, WritePng("shape.png", 120, 80, (x, y) => x is >= 30 and < 70 && y is >= 20 and < 60 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255)));
        var frame = img.GetWorldBounds();

        var run = VectorizeFlow.RunAsync(w, w.Session);
        VectorizeDialog? dialog = null;
        Pump(() => (dialog = w.OwnedWindows.OfType<VectorizeDialog>().FirstOrDefault()) is not null);
        Assert.NotNull(dialog);                                             // the options dialog opened straight away: no file picker
        Pump(() => dialog!.PreviewsCompleted >= 1);
        _ = dialog!.AcceptAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var group = Assert.IsType<GroupObject>(w.Session.Selection.Items.Single());
        var gb = group.GetWorldBounds();
        // The traced shapes sit exactly on the picture: the background shape covers its whole frame ...
        Assert.Equal(frame.Left, gb.Left, 0);
        Assert.Equal(frame.Top, gb.Top, 0);
        Assert.Equal(frame.Width, gb.Width, 0);
        Assert.Equal(frame.Height, gb.Height, 0);
        // ... and the black square (40 of 120 pixels wide, centered at pixel 50, 40) is where it is in the picture.
        var square = group.Children.Select(c => c.GetWorldBounds()).Single(r => r.Width < frame.Width * 0.5);
        Assert.Equal(frame.Left + frame.Width * 50 / 120, square.Center.X, 0);
        Assert.Equal(frame.Top + frame.Height * 40 / 80, square.Center.Y, 0);
        Assert.Equal(frame.Width / 3, square.Width, 0);        Assert.Contains(img, w.Session.Document.ActiveLayer.Children);      // the picture itself is still there
        w.Close();
    }

    // ---- screenshot ----

    [AvaloniaFact]
    public async Task Screenshot()
    {
        // Development aid: set DSM_SCREENSHOTS to a folder to get a picture of the crop tool at work there.
        var folder = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;

        string photo = WritePng("sunset.png", 900, 600, (x, y) =>
        {
            double t = y / 600.0;
            double dx = x - 620, dy = y - 330;
            if (dx * dx + dy * dy < 70 * 70) return (255, 235, 140, 255);                              // sun
            if (y > 400 + 40 * Math.Sin(x / 90.0)) return (30, (byte)(70 + 40 * Math.Sin(x / 50.0)), 60, 255); // hills
            return ((byte)(255 - 120 * t), (byte)(140 + 60 * t), (byte)(80 + 140 * t), 255);           // sky
        });
        string badge = WritePng("badge.png", 300, 300, (x, y) =>
        {
            double dx = x - 150, dy = y - 150, r = Math.Sqrt(dx * dx + dy * dy);
            return r < 140 ? (r < 100 ? ((byte)200, (byte)30, (byte)40, (byte)255) : ((byte)250, (byte)250, (byte)250, (byte)255)) : ((byte)0, (byte)0, (byte)0, (byte)0);
        });

        var w = new MainWindow(new StartupOptions(Demo: false)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.Session.Viewport.ZoomToFit(Rect2.FromXYWH(0, 0, 900, 460));
        await w.ImportImagesAsync([badge], new Vec2(130, 215));
        var logo = (ImageObject)w.Session.Selection.Items[0];
        w.Session.TransformSelection("Size", Affine2.Scale(0.55, 0.55, logo.GetWorldBounds().Center));
        await w.ImportImagesAsync([photo], new Vec2(500, 230));
        var big = (ImageObject)w.Session.Selection.Items[0];
        w.Session.TransformSelection("Size", Affine2.Scale(0.9, 0.9, big.GetWorldBounds().Center));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Session.BeginCrop());
        var bounds = big.GetWorldBounds();
        var tool = w.Session.GetTool<CropTool>();
        void Drag(Vec2 a, Vec2 b)
        {
            var input = (Vec2 doc) => new PointerInput(w.Session.Viewport.ToScreen(doc), doc, PointerButton.Left, ModifierKeys.None);
            w.Session.PointerDown(input(a));
            w.Session.PointerMove(input((a + b) / 2));
            w.Session.PointerMove(input(b));
            w.Session.PointerUp(input(b));
        }
        Drag(new Vec2(bounds.Left + bounds.Width * 0.12, bounds.Top + bounds.Height * 0.15), new Vec2(bounds.Left + bounds.Width * 0.82, bounds.Top + bounds.Height * 0.80));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(tool.Box);
        w.Session.Viewport.ZoomToFit(w.Session.Document.GetContentBounds().Inflate(60));
        Dispatcher.UIThread.RunJobs();

        var frame = w.CaptureRenderedFrame()!;
        Directory.CreateDirectory(folder);
#pragma warning disable CS0618
        frame.Save(Path.Combine(folder, "images.png"));
#pragma warning restore CS0618
        w.Close();
    }
}
